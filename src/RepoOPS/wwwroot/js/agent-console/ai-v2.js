// ===== AI Assistant V2 — Self-driving orchestrator with visible PTY terminals =====

(function () {
    const { state, api, utils } = window.AgentConsole || {};
    const { escapeHtml, formatRelative, showToast, statusTone } = utils || {};

    let selectedV2RunId = null;

    // ── V2 API layer ──

    const v2Api = {
        getRuns: () => fetch('/api/v2/runs').then(r => r.json()),
        getRun: (id) => fetch(`/api/v2/runs/${encodeURIComponent(id)}`).then(r => r.json()),
        getSnapshot: (id) => fetch(`/api/v2/runs/${encodeURIComponent(id)}/snapshot`).then(r => r.json()),
        createRun: (payload) => fetch('/api/v2/runs', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }).then(r => r.json()),
        stopRun: (id) => fetch(`/api/v2/runs/${encodeURIComponent(id)}/stop`, { method: 'POST' }).then(r => r.json()),
        openPath: (id, relativePath) => fetch(`/api/v2/runs/${encodeURIComponent(id)}/open-path`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ relativePath })
        }).then(r => r.json()),
        getTemplates: () => fetch('/api/v2/templates').then(r => r.json())
    };

    // ── State ──

    let v2Runs = [];
    let v2Snapshots = new Map();
    let activeWorkerTab = null;      // currently selected worker tab (workerId)

    /** V2-owned PTY connection (separate from Practice tab's PtyTerminal) */
    let v2PtyConn = null;

    /**
     * Map: sessionId → { terminal, fitAddon, workerId, roleName, icon, isRunning }
     * These are V2-specific terminals, NOT added to the global `terminals` Map.
     */
    const v2Sessions = new Map();

    /** Map: workerId → sessionId (for tab-switching) */
    const workerSessionMap = new Map();

    /** Map: workerId → commandPreview string */
    const workerCmdPreviews = new Map();

    /** The main thread's current sessionId */
    let mainThreadSessionId = null;
    const permissionHintCooldown = new Map();

    function maybeShowPermissionHint(sessionId, text) {
        if (!text) return;
        const marker = /Path confirmation|Allow directory access|Do you want to allow this\?/i;
        if (!marker.test(text)) return;

        const now = Date.now();
        const last = permissionHintCooldown.get(sessionId) || 0;
        if (now - last < 5000) return;
        permissionHintCooldown.set(sessionId, now);

        showToast?.('检测到权限确认：请先点击对应终端，然后用 ↑/↓ 选择，Enter 确认。', 'info');
    }

    function focusTerminal(sessionId) {
        const info = v2Sessions.get(sessionId);
        if (!info || !info.terminal) return;
        try { info.terminal.focus(); } catch (e) { /* ignore */ }
    }

    // ── PtyHub connection for V2 ──

    function initPtyConnection() {
        v2PtyConn = new signalR.HubConnectionBuilder()
            .withUrl('/hub/pty')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        v2PtyConn.on('PtyOutput', (sessionId, text) => {
            const info = v2Sessions.get(sessionId);
            if (info && info.terminal) {
                info.terminal.write(text);
                maybeShowPermissionHint(sessionId, text);
            }
        });

        v2PtyConn.on('PtyCompleted', (sessionId, exitCode) => {
            const info = v2Sessions.get(sessionId);
            if (!info) return;
            info.isRunning = false;
            info.exitCode = exitCode;
            // Update the worker tab light
            if (info.workerId) {
                updateWorkerTabLight(info.workerId, exitCode === 0 ? 'completed' : 'failed');
            }
            // If it's the main thread session
            if (sessionId === mainThreadSessionId) {
                updateMainThreadIndicator(exitCode === 0 ? 'completed' : 'failed');
            }
        });

        startV2PtyConnection();
    }

    async function startV2PtyConnection() {
        try {
            await v2PtyConn.start();
            console.log('[V2] PtyHub connected');
        } catch (err) {
            console.error('[V2] PtyHub connection failed:', err);
            setTimeout(startV2PtyConnection, 5000);
        }
    }

    // ── xterm.js terminal creation ──

    function createV2Terminal(sessionId, containerId) {
        const container = document.getElementById(containerId);
        if (!container) return null;

        const terminal = new Terminal({
            theme: (typeof Theme !== 'undefined' && Theme.getTerminalTheme) ? Theme.getTerminalTheme() : {},
            fontFamily: 'Consolas, "Courier New", monospace',
            fontSize: 13,
            lineHeight: 1.2,
            scrollback: 10000,
            cursorBlink: true,
            disableStdin: false,
            convertEol: false
        });

        const fitAddon = new FitAddon.FitAddon();
        terminal.loadAddon(fitAddon);

        // Create a wrapper div for this terminal
        const wrapper = document.createElement('div');
        wrapper.className = 'v2-terminal-wrapper';
        wrapper.id = `v2-term-${sessionId}`;
        wrapper.style.width = '100%';
        wrapper.style.height = '100%';
        container.innerHTML = '';
        container.appendChild(wrapper);

        terminal.open(wrapper);

        // Small delay to let DOM layout settle before fitting
        requestAnimationFrame(() => {
            try { fitAddon.fit(); } catch (e) { /* ignore */ }
        });

        // Wire keyboard input → PtyHub
        terminal.onData((data) => {
            if (v2PtyConn && v2PtyConn.state === signalR.HubConnectionState.Connected) {
                v2PtyConn.invoke('SendPtyInput', sessionId, data).catch(() => {});
            }
        });

        // Wire resize → PtyHub
        terminal.onResize(({ cols, rows }) => {
            if (v2PtyConn && v2PtyConn.state === signalR.HubConnectionState.Connected) {
                v2PtyConn.invoke('ResizePty', sessionId, cols, rows).catch(() => {});
            }
        });

        return { terminal, fitAddon };
    }

    // ── Initialization ──

    function bindStaticEvents() {
        const btnStart = document.getElementById('btnV2Start');
        const btnStop = document.getElementById('btnV2Stop');
        const btnTest = document.getElementById('btnV2CopilotTest');
        if (btnStart) btnStart.addEventListener('click', handleStart);
        if (btnStop) btnStop.addEventListener('click', handleStop);
        if (btnTest) btnTest.addEventListener('click', handleCopilotTest);
    }

    function quotePowerShellLiteral(value) {
        if (!value) return "''";
        return `'${String(value).replace(/'/g, "''")}'`;
    }

    function resolveTestWorkingDirectory() {
        const fromInput = document.getElementById('v2WorkspaceInput')?.value?.trim();
        if (fromInput) return fromInput;

        const snapshot = selectedV2RunId ? v2Snapshots.get(selectedV2RunId) : null;
        const fromRun = snapshot?.run?.workspaceRoot;
        if (fromRun) return fromRun;

        return '.';
    }

    async function handleCopilotTest() {
        const btn = document.getElementById('btnV2CopilotTest');
        if (btn) {
            btn.disabled = true;
            btn.textContent = '⏳ 测试中…';
        }

        try {
            if (!v2PtyConn || v2PtyConn.state !== signalR.HubConnectionState.Connected) {
                await startV2PtyConnection();
            }

            if (!v2PtyConn || v2PtyConn.state !== signalR.HubConnectionState.Connected) {
                throw new Error('PTY 连接未建立，请稍后重试。');
            }

            // Stop previous main-thread test session if it's still alive.
            if (mainThreadSessionId) {
                try { await v2PtyConn.invoke('StopPtyTask', mainThreadSessionId); } catch (e) { /* ignore */ }
            }

            const workingDirectory = resolveTestWorkingDirectory();
            const commandLine = 'pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass';

            const sessionId = await v2PtyConn.invoke('StartRawPtySession', commandLine, workingDirectory, 120, 30);
            mainThreadSessionId = sessionId;

            const result = createV2Terminal(sessionId, 'v2MainThreadOutput');
            if (result) {
                v2Sessions.set(sessionId, {
                    ...result,
                    workerId: null,
                    roleName: 'Test Main Thread',
                    icon: '🧪',
                    isRunning: true,
                    exitCode: null
                });
                focusTerminal(sessionId);
            }

            updateMainThreadIndicator('running');

            const preview = `${commandLine}\nSet-Location -LiteralPath ${quotePowerShellLiteral(workingDirectory)}\ncopilot`;
            showCmdPreview('v2MainThreadCmd', preview);

            const inputScript = `Set-Location -LiteralPath ${quotePowerShellLiteral(workingDirectory)}\r\ncopilot\r\n`;
            await v2PtyConn.invoke('SendPtyInput', sessionId, inputScript);

            showFeedback('已在主窗体启动测试：copilot（可直接在终端交互）。', 'info');
        } catch (e) {
            showFeedback(e?.message || '启动测试失败', 'error');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = '🧪 测试';
            }
        }
    }

    async function loadData() {
        try {
            v2Runs = await v2Api.getRuns();
            renderRunsList();
        } catch (e) {
            console.error('V2 load failed:', e);
        }
    }

    // ── Event handlers ──

    async function handleStart() {
        const goal = document.getElementById('v2GoalInput')?.value?.trim();
        if (!goal) { showToast?.('请输入目标', 'error'); return; }

        const workspace = document.getElementById('v2WorkspaceInput')?.value?.trim() || undefined;
        const maxRounds = parseInt(document.getElementById('v2MaxRoundsInput')?.value, 10) || 6;

        const btn = document.getElementById('btnV2Start');
        if (btn) { btn.disabled = true; btn.textContent = '⏳ 启动中…'; }

        // Reset terminal state
        v2Sessions.clear();
        workerSessionMap.clear();
        mainThreadSessionId = null;
        activeWorkerTab = null;
        clearTerminalContainers();

        try {
            const run = await v2Api.createRun({ goal, workspaceRoot: workspace, maxRounds, autoStart: true });
            selectedV2RunId = run.runId;
            v2Runs.unshift(run);
            renderRunsList();
            renderSelectedRun();
            showFeedback('V2 运行已启动 — 终端窗口即将出现', 'success');
            document.getElementById('btnV2Stop').disabled = false;
        } catch (e) {
            showFeedback(e.message || '启动失败', 'error');
        } finally {
            if (btn) { btn.disabled = false; btn.textContent = '🚀 开始执行'; }
        }
    }

    async function handleStop() {
        if (!selectedV2RunId) return;
        try {
            await v2Api.stopRun(selectedV2RunId);
            showFeedback('已停止', 'info');
            document.getElementById('btnV2Stop').disabled = true;
            await refreshSelectedRun();
        } catch (e) {
            showFeedback(e.message || '停止失败', 'error');
        }
    }

    async function selectRun(runId) {
        selectedV2RunId = runId;
        renderRunsList();
        await refreshSelectedRun();
    }

    async function refreshSelectedRun() {
        if (!selectedV2RunId) return;
        try {
            const snapshot = await v2Api.getSnapshot(selectedV2RunId);
            v2Snapshots.set(selectedV2RunId, snapshot);
            renderSelectedRun();
        } catch (e) {
            console.error('V2 snapshot fetch failed:', e);
        }
    }

    // ── Rendering: Runs list ──

    function renderRunsList() {
        const container = document.getElementById('v2RunsList');
        if (!container) return;

        if (v2Runs.length === 0) {
            container.innerHTML = '<div class="workspace-empty-state">暂无 V2 运行</div>';
            return;
        }

        container.innerHTML = v2Runs.map(run => {
            const selected = run.runId === selectedV2RunId;
            const tone = statusTone?.(run.status) || 'neutral';
            return `<div class="agent-list-item ${selected ? 'active' : ''}" data-run-id="${escapeHtml(run.runId)}">
                <span class="status-dot status-dot-${tone}"></span>
                <div class="agent-list-item-body">
                    <div class="agent-list-item-title">${escapeHtml(run.title)}</div>
                    <div class="agent-list-item-meta">R${run.currentRound}/${run.maxRounds} · ${escapeHtml(run.status)} · ${formatRelative?.(run.updatedAt) || ''}</div>
                </div>
            </div>`;
        }).join('');

        container.querySelectorAll('.agent-list-item').forEach(el => {
            el.addEventListener('click', () => selectRun(el.dataset.runId));
        });
    }

    // ── Rendering: Selected run detail ──

    function renderSelectedRun() {
        const snapshot = v2Snapshots.get(selectedV2RunId);
        if (!snapshot) {
            renderEmptyDetail();
            return;
        }

        const run = snapshot.run;
        const badge = document.getElementById('v2RoundBadge');
        if (badge) {
            badge.hidden = false;
            badge.textContent = `Round ${run.currentRound}/${run.maxRounds}`;
            badge.className = `v2-round-badge v2-round-${run.status === 'running' ? 'active' : run.status}`;
        }

        const title = document.getElementById('v2DetailTitle');
        if (title) title.textContent = `📡 ${run.title}`;

        const stopBtn = document.getElementById('btnV2Stop');
        if (stopBtn) stopBtn.disabled = (run.status !== 'running' && run.status !== 'planning');

        // Only render worker TABS (not terminal content — that's managed by xterm)
        renderWorkerTabs(snapshot.workers || []);
        renderArtifactQuickLinks(run);
        renderDecisions(snapshot.decisions || []);
    }

    function renderEmptyDetail() {
        const badge = document.getElementById('v2RoundBadge');
        if (badge) badge.hidden = true;
        clearTerminalContainers();
        document.getElementById('v2WorkerTabs').innerHTML = '';
        document.getElementById('v2ArtifactQuickLinks').innerHTML = '';
        document.getElementById('v2DecisionLog').innerHTML = '';
    }

    function roundArtifactBasePath(run) {
        const round = String(Math.max(1, run?.currentRound || 1)).padStart(3, '0');
        return `.repoops/v2/runs/${run.runId}/rounds/round-${round}`;
    }

    function renderArtifactQuickLinks(run) {
        const container = document.getElementById('v2ArtifactQuickLinks');
        if (!container) return;
        if (!run || !run.runId) {
            container.innerHTML = '';
            return;
        }

        const base = roundArtifactBasePath(run);
        const links = [
            { label: '📘 本轮计划', path: `${base}/main-plan.json` },
            { label: '🔍 本轮评审', path: `${base}/review.json` },
            { label: '👥 Worker产物', path: `${base}/workers` }
        ];

        container.innerHTML = links.map(item =>
            `<button class="editor-btn-secondary" type="button" data-artifact-path="${escapeHtml(item.path)}" title="打开 ${escapeHtml(item.path)}">${item.label}</button>`
        ).join('');

        container.querySelectorAll('[data-artifact-path]').forEach(btn => {
            btn.addEventListener('click', async () => {
                const relativePath = btn.getAttribute('data-artifact-path');
                if (!relativePath || !selectedV2RunId) return;
                try {
                    const result = await v2Api.openPath(selectedV2RunId, relativePath);
                    if (result?.error) {
                        showFeedback(result.error, 'error');
                        return;
                    }
                    showFeedback(`已打开：${relativePath}`, 'success');
                } catch (e) {
                    showFeedback(e?.message || '打开产物失败', 'error');
                }
            });
        });
    }

    function clearTerminalContainers() {
        const mainOutput = document.getElementById('v2MainThreadOutput');
        if (mainOutput) mainOutput.innerHTML = '<div class="workspace-empty-state">启动后主线程终端将在这里显示</div>';
        const workerOutputs = document.getElementById('v2WorkerOutputs');
        if (workerOutputs) workerOutputs.innerHTML = '<div class="workspace-empty-state">等待主线程派发任务…</div>';
        // Hide command previews
        const mainCmd = document.getElementById('v2MainThreadCmd');
        if (mainCmd) mainCmd.hidden = true;
        const workerCmd = document.getElementById('v2WorkerCmd');
        if (workerCmd) workerCmd.hidden = true;
        workerCmdPreviews.clear();
    }

    // ── Worker tabs (with red/green lights) — does NOT recreate terminals ──

    function renderWorkerTabs(workers) {
        const tabsContainer = document.getElementById('v2WorkerTabs');
        if (!tabsContainer) return;

        if (workers.length === 0) {
            tabsContainer.innerHTML = '';
            return;
        }

        if (activeWorkerTab && !workers.find(w => w.workerId === activeWorkerTab))
            activeWorkerTab = null;
        if (!activeWorkerTab && workers.length > 0)
            activeWorkerTab = workers[0].workerId;

        tabsContainer.innerHTML = workers.map(w => {
            const isActive = w.workerId === activeWorkerTab;
            const lightClass = w.status === 'running' ? 'v2-light-red' :
                              w.status === 'completed' ? 'v2-light-green' :
                              w.status === 'failed' ? 'v2-light-red v2-light-solid' : 'v2-light-idle';
            return `<button class="v2-worker-tab ${isActive ? 'active' : ''}" data-worker-id="${escapeHtml(w.workerId)}">
                <span class="v2-light ${lightClass}"></span>
                <span class="v2-worker-tab-icon">${escapeHtml(w.icon || '🤖')}</span>
                <span class="v2-worker-tab-name">${escapeHtml(w.roleName)}</span>
                <span class="v2-worker-tab-status">${escapeHtml(w.status)}</span>
            </button>`;
        }).join('');

        tabsContainer.querySelectorAll('.v2-worker-tab').forEach(el => {
            el.addEventListener('click', () => {
                activeWorkerTab = el.dataset.workerId;
                showWorkerTerminal(el.dataset.workerId);
                // Re-render tabs for active state
                renderWorkerTabs(workers);
            });
        });

        // Show the active worker's terminal
        showWorkerTerminal(activeWorkerTab);
    }

    /** Show/hide terminal wrappers based on which worker tab is active */
    function showWorkerTerminal(workerId) {
        const outputContainer = document.getElementById('v2WorkerOutputs');
        if (!outputContainer) return;

        const sessionId = workerSessionMap.get(workerId);
        if (!sessionId) return;

        // Show command preview for this worker
        const cmdPreview = workerCmdPreviews.get(workerId);
        showCmdPreview('v2WorkerCmd', cmdPreview);

        // Hide all worker terminal wrappers, show the active one
        outputContainer.querySelectorAll('.v2-terminal-wrapper').forEach(w => {
            w.style.display = 'none';
        });

        const activeWrapper = document.getElementById(`v2-term-${sessionId}`);
        if (activeWrapper) {
            activeWrapper.style.display = 'block';
            // Re-fit terminal
            const info = v2Sessions.get(sessionId);
            if (info && info.fitAddon) {
                requestAnimationFrame(() => {
                    try { info.fitAddon.fit(); } catch (e) { /* ignore */ }
                });
            }
            focusTerminal(sessionId);
        }
    }

    /** Update a worker tab's light color */
    function updateWorkerTabLight(workerId, status) {
        const tabsContainer = document.getElementById('v2WorkerTabs');
        if (!tabsContainer) return;
        const btn = tabsContainer.querySelector(`[data-worker-id="${workerId}"]`);
        if (!btn) return;
        const light = btn.querySelector('.v2-light');
        if (!light) return;
        light.className = 'v2-light ' + (
            status === 'completed' ? 'v2-light-green' :
            status === 'failed' ? 'v2-light-red v2-light-solid' :
            status === 'running' ? 'v2-light-red' : 'v2-light-idle'
        );
        const statusSpan = btn.querySelector('.v2-worker-tab-status');
        if (statusSpan) statusSpan.textContent = status;
    }

    function updateMainThreadIndicator(status) {
        const header = document.querySelector('.v2-main-thread-panel .v2-section-header');
        if (!header) return;
        const icon = status === 'running' ? '🔄' : status === 'completed' ? '✅' : '❌';
        header.textContent = `${icon} 主线程 (${status})`;
    }

    function renderDecisions(decisions) {
        const container = document.getElementById('v2DecisionLog');
        if (!container) return;

        if (decisions.length === 0) {
            container.innerHTML = '<div class="v2-decision-empty">无决策记录</div>';
            return;
        }

        container.innerHTML = decisions.map(d => {
            const kindIcon = d.kind === 'run-created' ? '🆕' :
                            d.kind === 'round-started' ? '▶️' :
                            d.kind === 'worker-dispatched' ? '📤' :
                            d.kind === 'round-completed' ? '✅' :
                            d.kind === 'review-triggered' ? '🔍' :
                            d.kind === 'run-completed' ? '🏁' :
                            d.kind === 'run-failed' || d.kind === 'run-stopped' ? '❌' : '📝';
            return `<div class="v2-decision-item">
                <span class="v2-decision-icon">${kindIcon}</span>
                <span class="v2-decision-text">${escapeHtml(d.summary)}</span>
                <span class="v2-decision-time">${formatRelative?.(d.createdAt) || ''}</span>
            </div>`;
        }).join('');

        // Auto-scroll to bottom
        container.scrollTop = container.scrollHeight;
    }

    // ── Command preview ──

    function showCmdPreview(elementId, commandPreview) {
        const el = document.getElementById(elementId);
        if (!el) return;
        if (!commandPreview) { el.hidden = true; return; }
        el.hidden = false;
        const body = el.querySelector('.v2-cmd-preview-body');
        if (body) body.textContent = commandPreview;
    }

    // ── Drag-to-resize between main thread and workers ──

    function initDragResize() {
        const handle = document.getElementById('v2ResizeHandle');
        const topPanel = document.getElementById('v2MainThreadPanel');
        const bottomPanel = document.getElementById('v2WorkersPanel');
        if (!handle || !topPanel || !bottomPanel) return;

        let startY = 0, startTopH = 0, startBottomH = 0, dragging = false;

        handle.addEventListener('mousedown', (e) => {
            e.preventDefault();
            dragging = true;
            startY = e.clientY;
            startTopH = topPanel.getBoundingClientRect().height;
            startBottomH = bottomPanel.getBoundingClientRect().height;
            handle.classList.add('dragging');
            document.body.style.cursor = 'ns-resize';
            document.body.style.userSelect = 'none';
        });

        document.addEventListener('mousemove', (e) => {
            if (!dragging) return;
            const delta = e.clientY - startY;
            const newTopH = Math.max(100, startTopH + delta);
            const newBottomH = Math.max(100, startBottomH - delta);
            topPanel.style.flex = 'none';
            bottomPanel.style.flex = 'none';
            topPanel.style.height = newTopH + 'px';
            bottomPanel.style.height = newBottomH + 'px';
        });

        document.addEventListener('mouseup', () => {
            if (!dragging) return;
            dragging = false;
            handle.classList.remove('dragging');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
            // Re-fit all terminals after resize
            for (const [, info] of v2Sessions) {
                if (info.fitAddon) {
                    try { info.fitAddon.fit(); } catch (e) { /* ignore */ }
                }
            }
        });
    }

    // ── Feedback ──

    function showFeedback(message, type) {
        const panel = document.getElementById('v2FeedbackPanel');
        if (!panel) return;
        panel.hidden = false;
        panel.className = `agent-feedback-panel agent-feedback-${type}`;
        panel.textContent = message;
        if (type === 'success' || type === 'info') {
            setTimeout(() => { panel.hidden = true; }, 4000);
        }
    }

    // ── SignalR event handling (TaskHub for V2 events) ──

    function registerHubEvents() {
        if (typeof connection === 'undefined' || !connection || typeof connection.on !== 'function') {
            setTimeout(registerHubEvents, 600);
            return;
        }

        // V2 run updated
        connection.on('V2RunUpdated', (run) => {
            const idx = v2Runs.findIndex(r => r.runId === run.runId);
            if (idx >= 0) v2Runs[idx] = run;
            else v2Runs.unshift(run);
            renderRunsList();
            if (run.runId === selectedV2RunId) {
                v2Snapshots.set(run.runId, {
                    run,
                    workers: run.workers || [],
                    rounds: run.rounds || [],
                    decisions: run.decisions || []
                });
                renderSelectedRun();
            }
        });

        // Main thread started — prepare the main thread terminal area
        connection.on('V2MainThreadStarted', (runId, sessionIdHint) => {
            if (runId !== selectedV2RunId) return;
            updateMainThreadIndicator('running');
        });

        // Main thread PTY session started — create/replace the main thread xterm terminal
        connection.on('V2MainThreadPtyStarted', (runId, sessionId, label, commandPreview) => {
            if (runId !== selectedV2RunId) return;
            console.log(`[V2] Main thread PTY started: ${sessionId} (${label})`);
            mainThreadSessionId = sessionId;

            // Show command preview
            showCmdPreview('v2MainThreadCmd', commandPreview);

            const result = createV2Terminal(sessionId, 'v2MainThreadOutput');
            if (result) {
                v2Sessions.set(sessionId, {
                    ...result,
                    workerId: null,
                    roleName: 'Main Thread',
                    icon: '🧵',
                    isRunning: true,
                    exitCode: null
                });
                focusTerminal(sessionId);
            }
            updateMainThreadIndicator('running');
        });

        // Main thread activity update
        connection.on('V2MainThreadActivity', (runId, label, status) => {
            if (runId !== selectedV2RunId) return;
            updateMainThreadIndicator(status);
        });

        // Worker PTY session started — create a new xterm terminal for this worker
        connection.on('V2WorkerStarted', (runId, workerId, sessionId, roleName, icon, status, commandPreview) => {
            if (runId !== selectedV2RunId) return;
            console.log(`[V2] Worker started: ${workerId} → PTY ${sessionId} (${roleName})`);

            workerSessionMap.set(workerId, sessionId);
            if (commandPreview) workerCmdPreviews.set(workerId, commandPreview);

            // Create terminal container for this worker (inside v2WorkerOutputs)
            const outputContainer = document.getElementById('v2WorkerOutputs');
            if (!outputContainer) return;

            // Remove the empty state if present
            const emptyState = outputContainer.querySelector('.workspace-empty-state');
            if (emptyState) emptyState.remove();

            // Create a wrapper div for this worker's terminal
            const wrapper = document.createElement('div');
            wrapper.className = 'v2-terminal-wrapper';
            wrapper.id = `v2-term-${sessionId}`;
            wrapper.style.width = '100%';
            wrapper.style.height = '100%';
            wrapper.style.display = (workerId === activeWorkerTab) ? 'block' : 'none';
            outputContainer.appendChild(wrapper);

            const terminal = new Terminal({
                theme: (typeof Theme !== 'undefined' && Theme.getTerminalTheme) ? Theme.getTerminalTheme() : {},
                fontFamily: 'Consolas, "Courier New", monospace',
                fontSize: 13,
                lineHeight: 1.2,
                scrollback: 10000,
                cursorBlink: true,
                disableStdin: false,
                convertEol: false
            });

            const fitAddon = new FitAddon.FitAddon();
            terminal.loadAddon(fitAddon);
            terminal.open(wrapper);

            requestAnimationFrame(() => {
                try { fitAddon.fit(); } catch (e) { /* ignore */ }
            });

            // Wire keyboard input
            terminal.onData((data) => {
                if (v2PtyConn && v2PtyConn.state === signalR.HubConnectionState.Connected) {
                    v2PtyConn.invoke('SendPtyInput', sessionId, data).catch(() => {});
                }
            });

            // Wire resize
            terminal.onResize(({ cols, rows }) => {
                if (v2PtyConn && v2PtyConn.state === signalR.HubConnectionState.Connected) {
                    v2PtyConn.invoke('ResizePty', sessionId, cols, rows).catch(() => {});
                }
            });

            v2Sessions.set(sessionId, {
                terminal,
                fitAddon,
                workerId,
                roleName,
                icon,
                isRunning: true,
                exitCode: null
            });

            focusTerminal(sessionId);

            // If no active tab, select this one
            if (!activeWorkerTab) {
                activeWorkerTab = workerId;
                wrapper.style.display = 'block';
            }
        });

        // Worker status changed
        connection.on('V2WorkerStatusChanged', (runId, workerId, status, roleName, icon) => {
            if (runId !== selectedV2RunId) return;
            updateWorkerTabLight(workerId, status);
        });
    }

    // ── Resize observer for re-fitting terminals ──

    function setupResizeObserver() {
        const mainOutput = document.getElementById('v2MainThreadOutput');
        const workerOutputs = document.getElementById('v2WorkerOutputs');

        const observer = new ResizeObserver(() => {
            for (const [, info] of v2Sessions) {
                if (info.fitAddon) {
                    try { info.fitAddon.fit(); } catch (e) { /* ignore */ }
                }
            }
        });

        if (mainOutput) observer.observe(mainOutput);
        if (workerOutputs) observer.observe(workerOutputs);
    }

    // ── Module registration ──

    const V2Module = {
        bindStaticEvents,
        loadData,
        renderRunsList,
        renderSelectedRun,
        registerHubEvents,
        initPtyConnection,
        initDragResize,
        setupResizeObserver
    };

    if (!window.AgentConsole) window.AgentConsole = {};
    window.AgentConsole.V2 = V2Module;

    // Auto-init when agent console initializes
    const origInit = window.AgentConsole.init;
    window.AgentConsole.init = async function () {
        if (origInit) await origInit.call(window.AgentConsole);
        V2Module.initPtyConnection();
        V2Module.bindStaticEvents();
        V2Module.registerHubEvents();
        V2Module.initDragResize();
        V2Module.setupResizeObserver();
        await V2Module.loadData();
    };
})();
