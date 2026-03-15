(function () {
    const AgentConsole = window.AgentConsole || {};
    const { escapeHtml, formatRelative, showToast, statusTone } = AgentConsole.utils || {};

    let selectedRunId = null;
    let runs = [];
    let snapshots = new Map();
    let ptyConnection = null;
    let mainSessionId = null;
    let subSessionId = null;
    const sessions = new Map();
    const sheetState = {
        control: 'overview',
        thread: 'main',
        history: 'timeline'
    };

    function isElementVisible(element) {
        return !!element
            && !element.hidden
            && element.getClientRects().length > 0
            && element.offsetWidth > 0
            && element.offsetHeight > 0;
    }

    function fitTerminalSession(sessionId) {
        if (!sessionId) return false;

        const info = sessions.get(sessionId);
        const wrapper = document.getElementById(`v3-term-${sessionId}`);
        const host = wrapper?.parentElement;
        if (!info?.fitAddon || !isElementVisible(wrapper) || !isElementVisible(host)) {
            return false;
        }

        try {
            info.fitAddon.fit();
            return true;
        } catch {
            return false;
        }
    }

    function scheduleTerminalFit(sessionId) {
        if (!sessionId) return;

        [0, 50, 180].forEach(delay => {
            window.setTimeout(() => {
                requestAnimationFrame(() => {
                    fitTerminalSession(sessionId);
                });
            }, delay);
        });
    }

    function scheduleActiveThreadFit() {
        if (sheetState.thread === 'main') {
            scheduleTerminalFit(mainSessionId);
            return;
        }

        if (sheetState.thread === 'sub') {
            scheduleTerminalFit(subSessionId);
        }
    }

    function disposeTerminalSession(sessionId) {
        const info = sessions.get(sessionId);
        if (!info) return;

        try {
            info.terminal?.dispose?.();
        } catch { }

        sessions.delete(sessionId);
    }

    function disposeSessionsByKind(kind) {
        for (const [sessionId, info] of sessions.entries()) {
            if (info?.kind === kind) {
                disposeTerminalSession(sessionId);
            }
        }
    }

    async function requestJson(url, options) {
        const response = await fetch(url, options);
        const data = await response.json().catch(() => null);
        if (!response.ok) {
            throw new Error(data?.error || data?.title || data?.detail || '请求失败');
        }
        return data;
    }

    const api = {
        getRuns: () => requestJson('/api/v3/runs'),
        getSnapshot: (id) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}/snapshot`),
        createRun: (payload) => requestJson('/api/v3/runs', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        }),
        approveInitialPlan: (id) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}/approve-initial-plan`, {
            method: 'POST'
        }),
        rejectInitialPlan: (id, comment) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}/reject-initial-plan`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ comment })
        }),
        stopRun: (id) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}/stop`, { method: 'POST' }),
        deleteRun: (id) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}`, { method: 'DELETE' }),
        continueRun: (id, instruction, additionalRounds) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}/continue`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ instruction: instruction || null, additionalRounds })
        }),
        saveInterjection: (id, text, useWingman) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}/interjection`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ text, useWingman: !!useWingman })
        }),
        clearInterjection: (id) => requestJson(`/api/v3/runs/${encodeURIComponent(id)}/interjection`, { method: 'DELETE' })
    };

    function initPtyConnection() {
        ptyConnection = new signalR.HubConnectionBuilder()
            .withUrl('/hub/pty')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        ptyConnection.on('PtyOutput', (sessionId, text) => {
            const info = sessions.get(sessionId);
            if (info?.terminal) {
                info.terminal.write(text);
            }
        });

        ptyConnection.on('PtyCompleted', (sessionId, exitCode) => {
            const info = sessions.get(sessionId);
            if (!info) return;
            info.exitCode = exitCode;
            info.isRunning = false;
            const badgeId = info.kind === 'main' ? 'v3MainStatusBadge' : 'v3SubStatusBadge';
            updateStatusBadge(badgeId, exitCode === 0 ? 'completed' : 'failed');
        });

        startPtyConnection();
    }

    async function startPtyConnection() {
        try {
            await ptyConnection.start();
        } catch (error) {
            console.error('[V3] PtyHub connection failed:', error);
            setTimeout(startPtyConnection, 5000);
        }
    }

    function createTerminal(sessionId, containerId, kind) {
        const container = document.getElementById(containerId);
        if (!container) return null;

        disposeSessionsByKind(kind);
        container.innerHTML = '';
        const wrapper = document.createElement('div');
        wrapper.className = 'v2-terminal-wrapper';
        wrapper.id = `v3-term-${sessionId}`;
        container.appendChild(wrapper);

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
        scheduleTerminalFit(sessionId);

        terminal.onData(data => {
            if (ptyConnection && ptyConnection.state === signalR.HubConnectionState.Connected) {
                ptyConnection.invoke('SendPtyInput', sessionId, data).catch(() => { });
            }
        });

        terminal.onResize(({ cols, rows }) => {
            if (ptyConnection && ptyConnection.state === signalR.HubConnectionState.Connected) {
                ptyConnection.invoke('ResizePty', sessionId, cols, rows).catch(() => { });
            }
        });

        sessions.set(sessionId, { terminal, fitAddon, kind, isRunning: true, exitCode: null });
        try { terminal.focus(); } catch { }
        return { terminal, fitAddon };
    }

    function bindStaticEvents() {
        document.getElementById('btnV3Start')?.addEventListener('click', handleStart);
        document.getElementById('btnV3Stop')?.addEventListener('click', handleStop);
        document.getElementById('btnV3Delete')?.addEventListener('click', handleDeleteRun);
        bindSheetEvents();
    }

    function bindSheetEvents() {
        document.querySelectorAll('[data-v3-sheet-group][data-v3-sheet]').forEach(button => {
            button.addEventListener('click', () => {
                const group = button.getAttribute('data-v3-sheet-group');
                const sheet = button.getAttribute('data-v3-sheet');
                if (!group || !sheet) return;
                sheetState[group] = sheet;
                applySheetState();
            });
        });
    }

    function applySheetState() {
        document.querySelectorAll('[data-v3-sheet-group][data-v3-sheet]').forEach(button => {
            const group = button.getAttribute('data-v3-sheet-group');
            const sheet = button.getAttribute('data-v3-sheet');
            const active = !!group && !!sheet && sheetState[group] === sheet;
            button.classList.toggle('active', active);
            button.setAttribute('aria-selected', active ? 'true' : 'false');
        });

        document.querySelectorAll('[data-v3-sheet-group][data-v3-sheet-panel]').forEach(panel => {
            const group = panel.getAttribute('data-v3-sheet-group');
            const sheet = panel.getAttribute('data-v3-sheet-panel');
            const active = !!group && !!sheet && sheetState[group] === sheet;
            panel.classList.toggle('active', active);
            panel.hidden = !active;
        });

        scheduleActiveThreadFit();
    }

    async function handleStart() {
        const goal = document.getElementById('v3GoalInput')?.value?.trim();
        if (!goal) {
            showFeedback('请输入总目标', 'error');
            return;
        }

        const workspaceRoot = document.getElementById('v3WorkspaceInput')?.value?.trim() || undefined;
        const maxRounds = parseInt(document.getElementById('v3MaxRoundsInput')?.value, 10) || 5;
        const button = document.getElementById('btnV3Start');
        if (button) {
            button.disabled = true;
            button.textContent = '⏳ 启动中…';
        }

        try {
            const run = await api.createRun({ goal, workspaceRoot, maxRounds, autoStart: true });
            selectedRunId = run.runId;
            upsertRun(run);
            renderRunsList();
            renderSelectedRun();
            document.getElementById('btnV3Stop').disabled = false;
            showFeedback('V3 run 已启动：主线先起案一次，之后每轮按“子线执行 → 主线复核并发下一轮任务卡”推进。', 'success');
        } catch (error) {
            showFeedback(error?.message || '启动 V3 失败', 'error');
        } finally {
            if (button) {
                button.disabled = false;
                button.textContent = '🪄 启动 V3';
            }
        }
    }

    async function handleStop() {
        if (!selectedRunId) return;
        try {
            await api.stopRun(selectedRunId);
            showFeedback('V3 run 已停止', 'info');
            document.getElementById('btnV3Stop').disabled = true;
            await refreshSelectedRun();
        } catch (error) {
            showFeedback(error?.message || '停止失败', 'error');
        }
    }

    async function handleDeleteRun() {
        if (!selectedRunId) return;
        const confirmed = window.confirm('删除这个 V3 run 后，会移除对应的 .repoops/v3 工件与该 run 的 prompt 记录；列表也会同步消失。要继续吗？');
        if (!confirmed) return;

        try {
            const deletingId = selectedRunId;
            await api.deleteRun(deletingId);
            snapshots.delete(deletingId);
            runs = runs.filter(item => item.runId !== deletingId);
            selectedRunId = runs[0]?.runId || null;
            renderRunsList();
            if (selectedRunId) {
                await refreshSelectedRun();
            } else {
                renderSelectedRun();
            }
            showFeedback('V3 run 已删除；对应落地工件也已从列表体系中移除。', 'info');
        } catch (error) {
            showFeedback(error?.message || '删除 V3 run 失败', 'error');
        }
    }

    async function loadData() {
        try {
            runs = await api.getRuns();
            renderRunsList();
            if (!selectedRunId && runs.length > 0) {
                selectedRunId = runs[0].runId;
                await refreshSelectedRun();
            }
        } catch (error) {
            console.error('[V3] load failed:', error);
        }
    }

    function upsertRun(run) {
        const index = runs.findIndex(item => item.runId === run.runId);
        if (index >= 0) runs[index] = run;
        else runs.unshift(run);
    }

    async function selectRun(runId) {
        selectedRunId = runId;
        renderRunsList();
        await refreshSelectedRun();
    }

    async function refreshSelectedRun() {
        if (!selectedRunId) return;
        try {
            const snapshot = await api.getSnapshot(selectedRunId);
            snapshots.set(selectedRunId, snapshot);
            renderSelectedRun();
        } catch (error) {
            console.error('[V3] snapshot load failed:', error);
        }
    }

    function renderRunsList() {
        const container = document.getElementById('v3RunsList');
        if (!container) return;

        if (!runs.length) {
            container.innerHTML = '<div class="workspace-empty-state">还没有 V3 run，先启动一个双人往复式任务吧。</div>';
            return;
        }

        container.innerHTML = runs.map(run => {
            const active = run.runId === selectedRunId;
            const tone = statusTone?.(run.status) || 'neutral';
            return `
                <button class="agent-list-item ${active ? 'active' : ''}" type="button" data-v3-run-id="${escapeHtml(run.runId)}">
                    <div class="agent-list-item-title"><span class="status-dot status-dot-${tone}"></span> ${escapeHtml(run.title)}</div>
                    <div class="agent-list-item-subtitle">R${run.currentRound || 0}/${run.maxRounds || 0} · ${escapeHtml(run.status || 'draft')} · ${formatRelative?.(run.updatedAt) || '刚刚'}</div>
                </button>
            `;
        }).join('');

        container.querySelectorAll('[data-v3-run-id]').forEach(button => {
            button.addEventListener('click', () => selectRun(button.getAttribute('data-v3-run-id')));
        });
    }

    function renderSelectedRun() {
        const snapshot = selectedRunId ? snapshots.get(selectedRunId) : null;
        const title = document.getElementById('v3DetailTitle');
        const badge = document.getElementById('v3RoundBadge');
        const stopButton = document.getElementById('btnV3Stop');
        const deleteButton = document.getElementById('btnV3Delete');

        if (!snapshot?.run) {
            if (title) title.textContent = '📡 V3 调度详情';
            if (badge) badge.hidden = true;
            if (stopButton) stopButton.disabled = true;
            if (deleteButton) deleteButton.disabled = true;
            renderOverview(null);
            renderInterjection(null);
            renderContinuePanel(null);
            renderTimeline([]);
            renderDecisionLog([]);
            applySheetState();
            return;
        }

        const run = snapshot.run;
        if (title) title.textContent = `📡 ${run.title}`;
        if (badge) {
            badge.hidden = false;
            badge.textContent = `Round ${run.currentRound || 0}/${run.maxRounds || 0}`;
            badge.className = `v3-round-badge v3-round-${run.status === 'running' || run.status === 'planning' || run.status === 'reviewing' ? 'active' : run.status}`;
        }
        if (stopButton) stopButton.disabled = !(run.status === 'planning' || run.status === 'awaiting-approval' || run.status === 'running' || run.status === 'reviewing');
        if (deleteButton) deleteButton.disabled = false;

        renderOverview(snapshot);
    renderInterjection(snapshot);
    renderContinuePanel(snapshot);
        renderTimeline(snapshot.rounds || []);
        renderDecisionLog(snapshot.decisions || []);
        applySheetState();
        updateStatusBadge('v3MainStatusBadge', run.mainThreadStatus || 'idle');
        updateStatusBadge('v3SubStatusBadge', run.subThreadStatus || 'idle');

        if (run.mainThreadCommandPreview) {
            showCmdPreview('v3MainCmd', run.mainThreadCommandPreview);
        }
        if (run.subThreadCommandPreview) {
            showCmdPreview('v3SubCmd', run.subThreadCommandPreview);
        }

        if (run.mainThreadSessionId && !sessions.has(run.mainThreadSessionId)) {
            mainSessionId = run.mainThreadSessionId;
            createTerminal(run.mainThreadSessionId, 'v3MainTerminal', 'main');
        }
        if (run.subThreadSessionId && !sessions.has(run.subThreadSessionId)) {
            subSessionId = run.subThreadSessionId;
            createTerminal(run.subThreadSessionId, 'v3SubTerminal', 'sub');
        }

        scheduleActiveThreadFit();
    }

    function formatRichTextBlock(value) {
        if (!value) return '';
        return escapeHtml(value).replace(/\r?\n/g, '<br>');
    }

    function collectPlanSections(run, latestRound, awaitingInitialApproval, isRewritingInitialPlan) {
        const sections = [];
        const pushSection = (title, content, wide = false) => {
            if (!content || !String(content).trim()) return;
            sections.push({ title, content: String(content).trim(), wide });
        };

        pushSection('阶段总图', run.stagePlanSummary || run.StagePlanSummary || latestRound?.mainPlanSummary || latestRound?.MainPlanSummary, true);

        if (awaitingInitialApproval || isRewritingInitialPlan) {
            pushSection('主线起案摘要', run.initialPlanSummary || run.InitialPlanSummary || run.latestMainReviewSummary || run.LatestMainReviewSummary, true);
            pushSection('首轮目标', run.initialPlanRoundGoal || run.InitialPlanRoundGoal);
            pushSection('首轮任务卡', run.initialPlanTaskCard || run.InitialPlanTaskCard || run.latestTaskCard || run.LatestTaskCard, true);
            pushSection('首轮复核重点', run.initialPlanReviewFocus || run.InitialPlanReviewFocus, true);
            pushSection('最近打回意见', run.lastInitialPlanReviewComment || run.LastInitialPlanReviewComment, true);
        } else {
            pushSection('当前阶段', run.currentStageLabel || run.CurrentStageLabel);
            pushSection('当前阶段目标', run.currentStageGoal || run.CurrentStageGoal, true);
            pushSection('当前轮 / 下一轮任务卡', run.latestTaskCard || run.LatestTaskCard || latestRound?.taskCard || latestRound?.TaskCard, true);
            pushSection('主线最新判断', run.latestMainReviewSummary || run.LatestMainReviewSummary || latestRound?.reviewSummary || latestRound?.ReviewSummary, true);
            pushSection('下一轮复核重点', run.latestReviewFocus || run.LatestReviewFocus || latestRound?.reviewFocus || latestRound?.ReviewFocus, true);
            pushSection('最近保留 / 修正决定', run.latestChangeDecision || run.LatestChangeDecision || latestRound?.changeDecision || latestRound?.ChangeDecision, true);
        }

        pushSection('架构红线', run.architectureGuardrails || run.ArchitectureGuardrails, true);

        if (!sections.length) {
            pushSection('当前已知计划信息', run.latestTaskCard || run.LatestTaskCard || run.latestMainReviewSummary || run.LatestMainReviewSummary || latestRound?.mainPlanSummary || latestRound?.MainPlanSummary || '主线暂时还没返回可展开的计划内容。', true);
        }

        return sections;
    }

    function renderFullPlanCard(run, latestRound, awaitingInitialApproval, isRewritingInitialPlan) {
        const sections = collectPlanSections(run, latestRound, awaitingInitialApproval, isRewritingInitialPlan);
        if (!sections.length) return '';

        return `
            <article class="v3-overview-card accent-main v3-overview-card-full">
                <div class="v3-overview-label">完整主线计划</div>
                <h3>${escapeHtml(awaitingInitialApproval || isRewritingInitialPlan ? '主线起案完整内容' : '当前主线计划全貌')}</h3>
                <p>这里直接展开主线当前可用的大部分计划内容：阶段总图、当前阶段、架构红线、任务卡、复核重点和最新判断，不再只显示一小块摘要。</p>
                <div class="v3-plan-section-grid">
                    ${sections.map(section => `
                        <section class="v3-plan-section ${section.wide ? 'wide' : ''}">
                            <div class="v3-plan-section-title">${escapeHtml(section.title)}</div>
                            <div class="v3-plan-section-body">${formatRichTextBlock(section.content)}</div>
                        </section>
                    `).join('')}
                </div>
            </article>
        `;
    }

    function renderInitialPlanActionCard(run) {
        if (!run?.awaitingInitialApproval) return '';

        return `
            <article class="v3-overview-card accent-review v3-overview-card-action">
                <div class="v3-overview-label">操作入口</div>
                <h3>在这里确认或打回首轮方案</h3>
                <p>你要找的按钮就在这张卡里：确认后才会启动子线并消耗正式轮次；打回时必须填写意见，主线会原地重写，轮次不会增加。</p>
                <label class="agent-label" for="v3InitialPlanRejectInput">打回意见（拒绝时必填）</label>
                <textarea id="v3InitialPlanRejectInput" class="agent-textarea v3-interjection-textarea" placeholder="例如：不要把最终目标默认限定成伴生端；可以分阶段，但不能先把长期目标做小。">${escapeHtml(run.lastInitialPlanReviewComment || '')}</textarea>
                <div class="inline-actions v3-interjection-actions v3-approval-actions">
                    <button id="btnV3ApproveInitialPlan" class="editor-btn-primary" type="button">✅ 同意方案并继续</button>
                    <button id="btnV3RejectInitialPlan" class="editor-btn-secondary" type="button">❌ 拒绝并要求重写</button>
                </div>
                <div class="v3-interjection-banner pending">同意 = 立刻进入子线执行；拒绝 = 主线按你的意见重写，正式轮次保持不变。</div>
            </article>
        `;
    }

    function renderOverview(snapshot) {
        const container = document.getElementById('v3OverviewGrid');
        if (!container) return;

        if (!snapshot?.run) {
            container.innerHTML = '<div class="workspace-empty-state">启动一个 V3 run 后，这里会显示主线判断、子线任务卡、最新 verdict 和轮次轨迹。</div>';
            return;
        }

        const run = snapshot.run;
        const latestRound = (snapshot.rounds || []).slice().sort((a, b) => (b.roundNumber || 0) - (a.roundNumber || 0))[0];
        const goalStatus = run.latestGoalStatus || (run.goalCompleted ? 'COMPLETE' : 'INCOMPLETE');
        const goalStatusLabel = goalStatus === 'COMPLETE' ? '终极目标已完成' : '终极目标未完成';
        const latestDirective = run.latestMainDirective || latestRound?.reviewDirective || (goalStatus === 'COMPLETE' ? '当前没有额外整改意见或下一轮任务卡。' : '等待主线给出下一轮任务卡或继续推进指令。');
        const awaitingInitialApproval = !!run.awaitingInitialApproval;
        const isRewritingInitialPlan = !awaitingInitialApproval && run.currentRound === 0 && run.status === 'planning' && (run.initialPlanRejectedCount || 0) > 0;
        const initialPlanVersion = Number.isInteger(run.initialPlanVersion) && run.initialPlanVersion > 0 ? run.initialPlanVersion : 0;
        const fullPlanCard = renderFullPlanCard(run, latestRound, awaitingInitialApproval, isRewritingInitialPlan);
        const initialPlanActionCard = renderInitialPlanActionCard(run);
        const initialPlanCard = (awaitingInitialApproval || isRewritingInitialPlan)
            ? `
                <article class="v3-overview-card accent-review v3-overview-card-wide">
                    <div class="v3-overview-label">起案确认</div>
                    <h3>${awaitingInitialApproval ? `首轮方案 v${escapeHtml(String(initialPlanVersion || 1))} 待确认` : '主线正在按打回意见重写方案'}</h3>
                    <p>${awaitingInitialApproval
                        ? escapeHtml(run.initialPlanSummary || run.latestMainReviewSummary || '主线已经产出首轮方案，请确认是否放行。')
                        : escapeHtml(`已打回 ${run.initialPlanRejectedCount || 0} 次；主线正在根据你的意见重写，轮次不会增加，子线也不会启动。`)}</p>
                    <div class="v3-interjection-status-grid">
                        <div class="v3-interjection-note">
                            <div class="v3-overview-label">当前版本</div>
                            <p>${escapeHtml(`v${initialPlanVersion || 1}`)}</p>
                        </div>
                        <div class="v3-interjection-note">
                            <div class="v3-overview-label">产品边界</div>
                            <p>${escapeHtml(run.currentStageLabel || '等待主线填写')}</p>
                        </div>
                        <div class="v3-interjection-note">
                            <div class="v3-overview-label">首轮任务卡</div>
                            <p>${escapeHtml(run.initialPlanTaskCard || run.latestTaskCard || '等待主线填写')}</p>
                        </div>
                        <div class="v3-interjection-note">
                            <div class="v3-overview-label">最近打回意见</div>
                            <p>${escapeHtml(run.lastInitialPlanReviewComment || '暂无')}</p>
                        </div>
                    </div>
                    ${awaitingInitialApproval ? `
                        <div class="v3-interjection-banner pending">操作按钮已移到下方“操作入口”卡片，避免被布局压住；你可以在那里直接同意或拒绝方案。</div>
                    ` : `
                        <div class="v3-interjection-banner neutral">主线重写期间会占用主线窗口；顶部审批卡会在新版方案生成后自动刷新。</div>
                    `}
                </article>
            `
            : '';

        container.innerHTML = `
            ${initialPlanCard}
            ${initialPlanActionCard}
            ${fullPlanCard}
            <article class="v3-overview-card accent-main">
                <div class="v3-overview-label">当前任务卡</div>
                <h3>${escapeHtml(run.mainRoleName || 'Helm')}</h3>
                <p>${escapeHtml(awaitingInitialApproval ? (run.initialPlanTaskCard || run.latestTaskCard || '等待你确认首轮方案后再启动子线。') : (run.latestTaskCard || latestRound?.taskCard || '主线还没给出启动卡或下一轮任务卡。'))}</p>
            </article>
            <article class="v3-overview-card accent-sub">
                <div class="v3-overview-label">子线最新摘要</div>
                <h3>${escapeHtml(run.subRoleName || 'Pathfinder')}</h3>
                <p>${escapeHtml(awaitingInitialApproval || isRewritingInitialPlan ? '子线尚未启动：当前正在等待你确认首轮方案。' : (run.latestSublineSummary || latestRound?.sublineSummary || '子线还没有提交可审查摘要。'))}</p>
            </article>
            <article class="v3-overview-card accent-review">
                <div class="v3-overview-label">主线最新判定</div>
                <h3>${escapeHtml(run.latestVerdict || latestRound?.reviewVerdict || '等待复核')} · ${escapeHtml(goalStatusLabel)}</h3>
                <p>${escapeHtml(run.latestMainReviewSummary || latestRound?.reviewSummary || '主线还没有给出本轮判定。')}</p>
            </article>
            <article class="v3-overview-card accent-note">
                <div class="v3-overview-label">下一轮任务卡 / 整改意见</div>
                <h3>${escapeHtml(run.status || 'draft')} · ${escapeHtml(goalStatus)}</h3>
                <p>${escapeHtml(latestDirective)}</p>
            </article>
        `;

        container.querySelector('#btnV3ApproveInitialPlan')?.addEventListener('click', handleApproveInitialPlan);
        container.querySelector('#btnV3RejectInitialPlan')?.addEventListener('click', handleRejectInitialPlan);
    }

    async function handleApproveInitialPlan() {
        if (!selectedRunId) return;

        try {
            const run = await api.approveInitialPlan(selectedRunId);
            upsertRun(run);
            await refreshSelectedRun();
            showFeedback('已确认首轮方案，子线开始继续推进。', 'success');
        } catch (error) {
            showFeedback(error?.message || '确认首轮方案失败', 'error');
        }
    }

    async function handleRejectInitialPlan() {
        if (!selectedRunId) return;

        const input = document.getElementById('v3InitialPlanRejectInput');
        const comment = input?.value?.trim();
        if (!comment) {
            showFeedback('打回首轮方案时必须填写意见。', 'error');
            input?.focus();
            return;
        }

        try {
            const run = await api.rejectInitialPlan(selectedRunId, comment);
            upsertRun(run);
            await refreshSelectedRun();
            showFeedback('已打回首轮方案；主线会按你的意见重写，正式轮次不会增加。', 'info');
        } catch (error) {
            showFeedback(error?.message || '打回首轮方案失败', 'error');
        }
    }

    function renderTimeline(rounds) {
        const container = document.getElementById('v3Timeline');
        if (!container) return;
        if (!rounds?.length) {
            container.innerHTML = '<div class="workspace-empty-state">还没有轮次记录。</div>';
            return;
        }

        container.innerHTML = rounds.slice().sort((a, b) => (b.roundNumber || 0) - (a.roundNumber || 0)).map(round => `
            <article class="v3-timeline-item">
                <div class="v3-timeline-head">
                    <div class="v3-timeline-round">Round ${round.roundNumber}</div>
                    <div class="agent-badge ${statusTone?.(round.status) || 'neutral'}">${escapeHtml(round.status || 'planning')}</div>
                </div>
                <div class="v3-timeline-body">
                    <div><strong>目标：</strong>${escapeHtml(round.objective || '未写明')}</div>
                    <div><strong>任务卡：</strong>${escapeHtml(round.taskCard || '—')}</div>
                    <div><strong>子线：</strong>${escapeHtml(round.sublineSummary || round.sublineStatus || '—')}</div>
                    <div><strong>主线判定：</strong>${escapeHtml(round.reviewSummary || round.reviewVerdict || '—')}</div>
                    <div><strong>终极目标：</strong>${escapeHtml(round.goalStatus || (round.goalCompleted ? 'COMPLETE' : 'INCOMPLETE'))}</div>
                </div>
            </article>
        `).join('');
    }

    function describeInterjectionWindow(run) {
        const insertionSlot = '主线 prompt 的【用户插话 / 下一轮偏置要求】段落';

        if (!run) {
            return {
                editable: false,
                phaseText: '尚未选择 V3 run',
                waitText: '—',
                applyTarget: insertionSlot,
                applyPhase: '—'
            };
        }

        if (['completed', 'failed', 'stopped'].includes(run.status)) {
            const endedByGoal = !!run.goalCompleted;
            return {
                editable: false,
                phaseText: run.status === 'completed'
                    ? (endedByGoal ? '当前 run 已完成终极目标' : '当前 run 已收口，但终极目标未完成')
                    : `当前 run 已${run.status === 'stopped' ? '停止' : '失败'}`,
                waitText: '不会再生效',
                applyTarget: insertionSlot,
                applyPhase: 'run 已结束'
            };
        }

        if (run.mainThreadStatus === 'running') {
            return {
                editable: true,
                phaseText: '当前在主线程阶段，本次主线 prompt 已发出',
                waitText: '预计还要等待 2 次 AI 交互',
                applyTarget: insertionSlot,
                applyPhase: '下一次主线阶段（通常是下一轮主线复核）'
            };
        }

        if (run.subThreadStatus === 'running') {
            return {
                editable: true,
                phaseText: '当前在子线程阶段，主线还没开始下一次复核',
                waitText: '预计还要等待 1 次 AI 交互',
                applyTarget: insertionSlot,
                applyPhase: '本轮子线结束后的下一次主线复核'
            };
        }

        if ((run.currentRound || 0) === 0) {
            return {
                editable: true,
                phaseText: '还处在启动前后窗口，主线尚未稳定进入轮次',
                waitText: '预计还要等待 1 次 AI 交互',
                applyTarget: insertionSlot,
                applyPhase: '主线起案'
            };
        }

        return {
            editable: true,
            phaseText: '当前不在主线执行中，插话会排队等待下一次主线阶段',
            waitText: '预计还要等待 1 次 AI 交互',
            applyTarget: insertionSlot,
            applyPhase: '下一次主线复核'
        };
    }

    function renderInterjection(snapshot) {
        const container = document.getElementById('v3InterjectionPanel');
        if (!container) return;

        if (!snapshot?.run) {
            container.innerHTML = '<div class="workspace-empty-state">选择一个 V3 run 后，你可以在这里插话；系统会告诉你这句话会在主线的哪个位置、还要等几次 AI 交互才会生效，并允许你在生效前随时修改或删除。</div>';
            return;
        }

        const run = snapshot.run;
        const info = describeInterjectionWindow(run);
        const pending = run.pendingInterjectionText || '';
        const pendingWingman = run.pendingInterjectionWingmanText || '';
        const pendingUseWingman = !!(run.pendingInterjectionUseWingman || pendingWingman);
        const lastApplied = run.lastAppliedInterjectionText || '';
        const lastAppliedWingman = run.lastAppliedInterjectionWingmanText || '';
        const lastAppliedMeta = escapeHtml(`生效位置：${run.lastAppliedInterjectionPhase || 'mainline'} · 轮次：${run.lastAppliedInterjectionRound ?? '0'} · ${formatRelative?.(run.lastAppliedInterjectionAt) || ''}`);

        container.innerHTML = `
            <div class="v3-interjection-status-grid">
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">当前阶段</div>
                    <p>${escapeHtml(info.phaseText)}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">预计生效时机</div>
                    <p>${escapeHtml(info.applyPhase)}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">还需等待</div>
                    <p>${escapeHtml(info.waitText)}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">插入位置</div>
                    <p>${escapeHtml(info.applyTarget)}</p>
                </div>
            </div>

            <label class="agent-label" for="v3InterjectionInput">给下一轮主线插一句话</label>
            <textarea id="v3InterjectionInput" class="agent-textarea v3-interjection-textarea" ${info.editable ? '' : 'disabled'} placeholder="例如：下一轮优先把用户可见结果做通，但不要为了这件事大改架构。">${escapeHtml(pending)}</textarea>
            <div class="agent-helper-text">这句话不会直接塞给子线，而是会插入主线 prompt 的“用户插话 / 下一轮偏置要求”位置，由主线吸收到下一轮任务卡或复核重点中。生效前你可以随时修改或删除。</div>

            <label class="v3-interjection-toggle ${info.editable ? '' : 'disabled'}" for="v3InterjectionWingmanToggle">
                <input id="v3InterjectionWingmanToggle" type="checkbox" ${pendingUseWingman ? 'checked' : ''} ${info.editable ? '' : 'disabled'}>
                <span>
                    <strong>让毒舌助攻手先强化这句插嘴</strong>
                    <small>它会额外挖出当前阶段最该警惕的风险、边界和反例提醒；主线会同时看到你的原话与助攻增强稿，但会明确知道哪段是你说的、哪段是助攻手补的。</small>
                </span>
            </label>

            <div class="inline-actions v3-interjection-actions">
                <button id="btnV3SaveInterjection" class="editor-btn-primary" type="button" ${info.editable ? '' : 'disabled'}>${pending ? '更新插话' : '插入下一轮'}</button>
                <button id="btnV3ClearInterjection" class="editor-btn-secondary" type="button" ${(info.editable && pending) ? '' : 'disabled'}>删除待生效插话</button>
            </div>

            ${pending ? `<div class="v3-interjection-banner pending">待生效：这句插话还没进 prompt，${escapeHtml(info.waitText)} 后会插入到 ${escapeHtml(info.applyTarget)}。</div>` : '<div class="v3-interjection-banner neutral">当前没有待生效插话。</div>'}
            ${pendingWingman ? `
                <div class="v3-interjection-history v3-interjection-history-wingman">
                    <div class="v3-overview-label">待一并注入主线 · 助攻手增强稿</div>
                    <div class="v3-interjection-rich-body">${formatRichTextBlock(pendingWingman)}</div>
                    <small>这段不是替你改写目标，而是帮主线更快看到你这句插嘴背后的风险和底线。</small>
                </div>
            ` : ''}
            ${lastApplied ? `
                <div class="v3-interjection-history">
                    <div class="v3-overview-label">最近一次已生效 · 用户原话</div>
                    <div class="v3-interjection-rich-body">${formatRichTextBlock(lastApplied)}</div>
                    <small>${lastAppliedMeta}</small>
                </div>
            ` : ''}
            ${lastAppliedWingman ? `
                <div class="v3-interjection-history v3-interjection-history-wingman">
                    <div class="v3-overview-label">最近一次已生效 · 助攻手增强稿</div>
                    <div class="v3-interjection-rich-body">${formatRichTextBlock(lastAppliedWingman)}</div>
                    <small>${lastAppliedMeta}</small>
                </div>
            ` : ''}
        `;

        container.querySelector('#btnV3SaveInterjection')?.addEventListener('click', handleSaveInterjection);
        container.querySelector('#btnV3ClearInterjection')?.addEventListener('click', handleClearInterjection);
    }

    function canUseContinuePush(run) {
        if (!run) return false;
        return !!run.recoveredFromStorage || run.status === 'completed' || run.status === 'failed';
    }

    function renderContinuePanel(snapshot) {
        const container = document.getElementById('v3ContinuePanel');
        if (!container) return;

        if (!snapshot?.run) {
            container.innerHTML = '<div class="workspace-empty-state">选择一个 V3 run 后，这里会显示继续推进窗口。平时它会保持灰态，只有在软件重开恢复出 run，或 run 已完成 / 失败后，才允许基于现有事实继续工作。</div>';
            return;
        }

        const run = snapshot.run;
        const enabled = canUseContinuePush(run);
        const goalStatus = run.latestGoalStatus || (run.goalCompleted ? 'COMPLETE' : 'INCOMPLETE');
        const continueRoundIncrement = Number.isInteger(run.lastContinueRoundIncrement) && run.lastContinueRoundIncrement > 0
            ? run.lastContinueRoundIncrement
            : 3;
        const projectedMaxRounds = (Number.isFinite(run.maxRounds) ? run.maxRounds : 0) + continueRoundIncrement;
        const reason = enabled
            ? (run.recoveredFromStorage
                ? '该 run 来自软件重开后的恢复态，可以基于已落地事实继续推进。'
                : run.status === 'completed'
                    ? (run.goalCompleted
                        ? '该 run 已完成终极目标；如果你觉得还可深挖，可以基于现有事实继续推进。'
                        : '该 run 本轮已收口，但终极目标仍未完成；可以基于现有事实继续推进。')
                    : '该 run 已失败；你可以基于现有事实重新组织下一轮推进。')
            : '继续推进平时保持灰态；只有软件重开恢复出的 run，或 run 已完成 / 失败时才会启用。';

        container.innerHTML = `
            <div class="v3-interjection-status-grid">
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">启用状态</div>
                    <p>${enabled ? '可用' : '灰态锁定'}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">当前 run 状态</div>
                    <p>${escapeHtml(run.status || 'draft')}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">终极目标状态</div>
                    <p>${escapeHtml(goalStatus)}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">恢复态</div>
                    <p>${run.recoveredFromStorage ? '是：重开软件后恢复' : '否'}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">最近续推说明</div>
                    <p>${escapeHtml(run.lastContinueInstruction || '暂无')}</p>
                </div>
                <div class="v3-interjection-note">
                    <div class="v3-overview-label">续推增加轮次</div>
                    <p>${escapeHtml(String(continueRoundIncrement))}（预计上限：${escapeHtml(String(run.maxRounds || 0))} → ${escapeHtml(String(projectedMaxRounds))}）</p>
                </div>
            </div>

            <label class="agent-label" for="v3ContinueInput">基于现有事实继续推进</label>
            <textarea id="v3ContinueInput" class="agent-textarea v3-interjection-textarea" ${enabled ? '' : 'disabled'} placeholder="例如：基于现在已有事实继续推进，优先补齐最后一段闭环，但不要重新大拆结构。"></textarea>
            <div class="settings-grid settings-grid-2 compact-grid">
                <div>
                    <label class="agent-label" for="v3ContinueRoundsInput">继续推进增加轮次</label>
                    <input id="v3ContinueRoundsInput" class="agent-input" type="number" min="1" step="1" value="${escapeHtml(String(continueRoundIncrement))}" ${enabled ? '' : 'disabled'} required>
                </div>
            </div>
            <div class="agent-helper-text">${escapeHtml(reason)} 这个框不是普通聊天框，而是让主线基于现有轮次事实重新起案，决定是否继续、继续什么、下一轮怎么给子线发任务卡。</div>

            <div class="inline-actions v3-interjection-actions">
                <button id="btnV3ContinuePush" class="editor-btn-primary" type="button" ${enabled ? '' : 'disabled'}>继续推进</button>
            </div>

            <div class="v3-interjection-banner ${enabled ? 'pending' : 'neutral'}">${escapeHtml(reason)}</div>
        `;

        container.querySelector('#btnV3ContinuePush')?.addEventListener('click', handleContinuePush);
    }

    async function handleSaveInterjection() {
        if (!selectedRunId) return;
        const input = document.getElementById('v3InterjectionInput');
        const wingmanToggle = document.getElementById('v3InterjectionWingmanToggle');
        const text = input?.value?.trim();
        const useWingman = !!wingmanToggle?.checked;
        if (!text) {
            showFeedback('先写一句要插给主线的话。', 'error');
            return;
        }

        try {
            const run = await api.saveInterjection(selectedRunId, text, useWingman);
            upsertRun(run);
            await refreshSelectedRun();
            const info = describeInterjectionWindow(run);
            if (useWingman && run.pendingInterjectionWingmanText) {
                showFeedback(`插话与助攻增强稿已排队：${info.phaseText}；${info.waitText} 后会一起插入到 ${info.applyTarget}。`, 'success');
            } else if (useWingman) {
                showFeedback(`插话已排队，但这次助攻手没有产出可用增强稿；${info.waitText} 后仍会把你的原话插入到 ${info.applyTarget}。`, 'info');
            } else {
                showFeedback(`插话已排队：${info.phaseText}；${info.waitText} 后会插入到 ${info.applyTarget}。`, 'success');
            }
        } catch (error) {
            showFeedback(error?.message || '保存插话失败', 'error');
        }
    }

    async function handleClearInterjection() {
        if (!selectedRunId) return;

        try {
            const run = await api.clearInterjection(selectedRunId);
            upsertRun(run);
            await refreshSelectedRun();
            showFeedback('待生效插话已删除；主线下一轮不会再收到它。', 'info');
        } catch (error) {
            showFeedback(error?.message || '删除插话失败', 'error');
        }
    }

    async function handleContinuePush() {
        if (!selectedRunId) return;
        const input = document.getElementById('v3ContinueInput');
        const roundsInput = document.getElementById('v3ContinueRoundsInput');
        const instruction = input?.value?.trim() || '';
        const additionalRounds = Number.parseInt(roundsInput?.value || '', 10);

        if (!Number.isInteger(additionalRounds) || additionalRounds <= 0) {
            showFeedback('请填写继续推进要增加的轮次，必须是大于 0 的整数。', 'error');
            roundsInput?.focus();
            return;
        }

        try {
            const run = await api.continueRun(selectedRunId, instruction, additionalRounds);
            upsertRun(run);
            await refreshSelectedRun();
            showFeedback(`已提交“继续推进”；当前 run 轮次上限将增加 ${additionalRounds}。`, 'success');
        } catch (error) {
            showFeedback(error?.message || '继续推进失败', 'error');
        }
    }

    function renderDecisionLog(decisions) {
        const container = document.getElementById('v3DecisionLog');
        if (!container) return;
        if (!decisions?.length) {
            container.innerHTML = '<div class="workspace-empty-state">等待主线给出第一条判断。</div>';
            return;
        }

        container.innerHTML = decisions.slice().reverse().map(decision => `
            <div class="v3-decision-item">
                <div class="v3-decision-kind">${escapeHtml(decision.kind || 'note')}</div>
                <div class="v3-decision-text">
                    <div>${escapeHtml(decision.summary || '')}</div>
                    <small>${formatRelative?.(decision.createdAt) || ''}</small>
                </div>
            </div>
        `).join('');
    }

    function showCmdPreview(containerId, commandPreview) {
        const container = document.getElementById(containerId);
        if (!container) return;
        const body = container.querySelector('.v2-cmd-preview-body');
        if (!commandPreview) {
            container.hidden = true;
            if (body) body.textContent = '';
            return;
        }
        container.hidden = false;
        if (body) body.textContent = commandPreview;
    }

    function updateStatusBadge(badgeId, status) {
        const badge = document.getElementById(badgeId);
        if (!badge) return;
        const tone = statusTone?.(status) || 'neutral';
        badge.className = `agent-badge ${tone}`;
        badge.textContent = status || 'idle';
    }

    function showFeedback(message, type = 'success') {
        const panel = document.getElementById('v3FeedbackPanel');
        if (!panel) return;
        panel.hidden = false;
        panel.className = `agent-feedback-panel ${type}`;
        panel.textContent = message;
        if (type === 'success' || type === 'info') {
            setTimeout(() => { panel.hidden = true; }, 4200);
        }
    }

    function registerHubEvents() {
        if (typeof connection === 'undefined' || !connection || typeof connection.on !== 'function') {
            setTimeout(registerHubEvents, 600);
            return;
        }

        connection.on('V3RunUpdated', run => {
            upsertRun(run);
            renderRunsList();
            if (run.runId === selectedRunId) {
                snapshots.set(run.runId, {
                    run,
                    rounds: run.rounds || [],
                    decisions: run.decisions || []
                });
                renderSelectedRun();
            }
        });

        connection.on('V3RunDeleted', runId => {
            snapshots.delete(runId);
            runs = runs.filter(item => item.runId !== runId);
            if (selectedRunId === runId) {
                selectedRunId = runs[0]?.runId || null;
                if (selectedRunId) {
                    refreshSelectedRun();
                } else {
                    renderSelectedRun();
                }
            }
            renderRunsList();
        });

        connection.on('V3MainThreadPtyStarted', (runId, sessionId, label, commandPreview) => {
            if (runId !== selectedRunId) return;
            mainSessionId = sessionId;
            showCmdPreview('v3MainCmd', commandPreview);
            createTerminal(sessionId, 'v3MainTerminal', 'main');
            updateStatusBadge('v3MainStatusBadge', 'running');
        });

        connection.on('V3SublinePtyStarted', (runId, sessionId, label, commandPreview) => {
            if (runId !== selectedRunId) return;
            subSessionId = sessionId;
            showCmdPreview('v3SubCmd', commandPreview);
            createTerminal(sessionId, 'v3SubTerminal', 'sub');
            updateStatusBadge('v3SubStatusBadge', 'running');
        });

        connection.on('V3MainThreadActivity', (runId, label, status) => {
            if (runId !== selectedRunId) return;
            updateStatusBadge('v3MainStatusBadge', status);
        });

        connection.on('V3SublineActivity', (runId, label, status) => {
            if (runId !== selectedRunId) return;
            updateStatusBadge('v3SubStatusBadge', status);
        });
    }

    function setupResizeObserver() {
        const observer = new ResizeObserver(() => {
            for (const [sessionId] of sessions) {
                fitTerminalSession(sessionId);
            }
        });

        const main = document.getElementById('v3MainTerminal');
        const sub = document.getElementById('v3SubTerminal');
        const threadShell = document.querySelector('.v3-sheet-shell-thread');
        const detailBody = document.querySelector('.v3-detail-body');
        if (main) observer.observe(main);
        if (sub) observer.observe(sub);
        if (threadShell) observer.observe(threadShell);
        if (detailBody) observer.observe(detailBody);
    }

    const V3Module = {
        bindStaticEvents,
        loadData,
        registerHubEvents,
        initPtyConnection,
        setupResizeObserver,
        applySheetState,
        renderRunsList,
        renderSelectedRun,
        refreshSelectedRun
    };

    if (!window.AgentConsole) window.AgentConsole = {};
    window.AgentConsole.V3 = V3Module;

    const origInit = window.AgentConsole.init;
    window.AgentConsole.init = async function () {
        if (origInit) await origInit.call(window.AgentConsole);
        V3Module.initPtyConnection();
        V3Module.bindStaticEvents();
        V3Module.registerHubEvents();
        V3Module.setupResizeObserver();
        await V3Module.loadData();
    };
})();
