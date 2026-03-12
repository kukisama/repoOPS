// ===== RepoOPS Agent Console =====

const AgentConsole = (() => {
    const state = {
        roles: [],
        settings: {},
        runs: [],
        selectedRunId: null,
        selectedRoleId: null,
        currentView: 'tasks'
    };

    let agentConnection = null;

    function init() {
        bindWorkspaceTabs();
        bindStaticButtons();
        initAgentConnection();
        loadInitialData();
    }

    async function loadInitialData() {
        await Promise.all([loadRoles(), loadRuns()]);
    }

    function bindWorkspaceTabs() {
        document.querySelectorAll('#workspaceNav .workspace-tab').forEach(btn => {
            btn.addEventListener('click', () => switchView(btn.getAttribute('data-view') || 'tasks'));
        });
    }

    function bindStaticButtons() {
        document.getElementById('btnCreateRun')?.addEventListener('click', createRun);
        document.getElementById('btnAddRole')?.addEventListener('click', addRole);
        document.getElementById('btnSaveRoles')?.addEventListener('click', saveRoles);
        document.getElementById('btnSaveSettings')?.addEventListener('click', saveSettings);
    }

    function switchView(view) {
        state.currentView = view;
        document.querySelectorAll('#workspaceNav .workspace-tab').forEach(btn => {
            btn.classList.toggle('active', btn.getAttribute('data-view') === view);
        });

        document.querySelectorAll('.workspace-view').forEach(panel => {
            panel.classList.toggle('active', panel.id === `view-${view}`);
        });
    }

    function initAgentConnection() {
        agentConnection = new signalR.HubConnectionBuilder()
            .withUrl('/hub/tasks')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        agentConnection.on('RunUpdated', (run) => {
            mergeRun(run);
            renderRunsList();
            renderRunDetail();
        });

        agentConnection.on('AgentWorkerOutput', (runId, workerId, output) => {
            const run = state.runs.find(item => item.runId === runId);
            const worker = run?.workers?.find(item => item.workerId === workerId);
            if (!worker) {
                return;
            }

            worker.lastOutputPreview = trimOutput(`${worker.lastOutputPreview || ''}${output}`);
            const pre = document.getElementById(`worker-output-${workerId}`);
            if (pre) {
                pre.textContent = worker.lastOutputPreview || '暂无输出';
            }
        });

        agentConnection.on('AgentWorkerStatusChanged', (runId, workerId, status) => {
            const run = state.runs.find(item => item.runId === runId);
            const worker = run?.workers?.find(item => item.workerId === workerId);
            if (!worker) {
                return;
            }

            worker.status = status;
            renderRunsList();
            renderRunDetail();
        });

        agentConnection.on('AgentWorkerCompleted', (runId, workerId, exitCode, summary) => {
            const run = state.runs.find(item => item.runId === runId);
            const worker = run?.workers?.find(item => item.workerId === workerId);
            if (!worker) {
                return;
            }

            worker.exitCode = exitCode;
            worker.lastSummary = summary;
            renderRunDetail();
        });

        agentConnection.on('SupervisorDecisionMade', (runId, summary) => {
            const run = state.runs.find(item => item.runId === runId);
            if (!run) {
                return;
            }

            run.latestSummary = summary;
            renderRunDetail();
        });

        agentConnection.on('VerificationCompleted', (runId, verification) => {
            const run = state.runs.find(item => item.runId === runId);
            if (!run) {
                return;
            }

            run.lastVerification = verification;
            renderRunsList();
            renderRunDetail();
        });

        startAgentConnection();
    }

    async function startAgentConnection() {
        try {
            await agentConnection.start();
        } catch (err) {
            console.error('Agent SignalR connection error:', err);
            setTimeout(startAgentConnection, 5000);
        }
    }

    async function loadRoles() {
        try {
            const data = await apiGet('/api/agent/roles');
            state.roles = data.roles || [];
            state.settings = data.settings || {};
            if (!state.selectedRoleId && state.roles.length > 0) {
                state.selectedRoleId = state.roles[0].roleId;
            }
            renderRoleOptions();
            renderRolesList();
            renderRoleEditor();
            renderSettingsEditor();
        } catch (err) {
            console.error('Failed to load roles:', err);
            notify('加载角色失败', 'error');
        }
    }

    async function loadRuns() {
        try {
            state.runs = await apiGet('/api/agent/runs');
            if (!state.selectedRunId && state.runs.length > 0) {
                state.selectedRunId = state.runs[0].runId;
            }
            renderRunsList();
            renderRunDetail();
        } catch (err) {
            console.error('Failed to load runs:', err);
            notify('加载 runs 失败', 'error');
        }
    }

    function renderRoleOptions() {
        const container = document.getElementById('runRoleOptions');
        if (!container) {
            return;
        }

        if (state.roles.length === 0) {
            container.innerHTML = '<div class="workspace-empty-inline">暂无角色，请先去 Roles 页面添加。</div>';
            return;
        }

        container.innerHTML = state.roles.map(role => `
            <label class="agent-checkbox-row">
                <input type="checkbox" value="${escapeAttr(role.roleId)}" checked>
                <span>${escapeHtml(role.icon || '🎭')} ${escapeHtml(role.name)}</span>
            </label>
        `).join('');
    }

    function renderRunsList() {
        const container = document.getElementById('runsList');
        if (!container) {
            return;
        }

        if (state.runs.length === 0) {
            container.innerHTML = '<div class="workspace-empty-inline">还没有任何 run，先创建一个吧。</div>';
            return;
        }

        container.innerHTML = state.runs.map(run => `
            <button type="button" class="agent-list-item ${run.runId === state.selectedRunId ? 'active' : ''}" onclick="AgentConsole.selectRun('${run.runId}')">
                <div class="agent-list-item-title">${escapeHtml(run.title || 'Untitled Run')}</div>
                <div class="agent-meta-row">
                    <span class="agent-badge ${statusClass(run.status)}">${escapeHtml(run.status || 'unknown')}</span>
                    <span>${(run.workers || []).length} roles</span>
                </div>
                <div class="agent-list-item-subtitle">${escapeHtml(run.goal || '')}</div>
            </button>
        `).join('');
    }

    function renderRunDetail() {
        const container = document.getElementById('runDetail');
        if (!container) {
            return;
        }

        const run = state.runs.find(item => item.runId === state.selectedRunId);
        if (!run) {
            container.innerHTML = '<div class="workspace-empty-state">选择左侧 Run，或者先创建一个新的协作任务。</div>';
            return;
        }

        container.innerHTML = `
            <div class="agent-detail-shell">
                <section class="agent-hero-card">
                    <div>
                        <div class="agent-eyebrow">Run</div>
                        <h2>${escapeHtml(run.title)}</h2>
                        <p>${escapeHtml(run.goal)}</p>
                    </div>
                    <div class="agent-hero-actions">
                        <span class="agent-badge ${statusClass(run.status)}">${escapeHtml(run.status || 'unknown')}</span>
                        <span class="agent-badge ${run.autoPilotEnabled ? 'running' : 'neutral'}">${run.autoPilotEnabled ? 'autopilot on' : 'autopilot off'}</span>
                        ${run.pendingAutoStepRequested ? '<span class="agent-badge warning">auto-step queued</span>' : ''}
                        <button class="editor-btn-secondary" type="button" onclick="AgentConsole.toggleAutopilot(${run.autoPilotEnabled ? 'false' : 'true'})">${run.autoPilotEnabled ? '关闭自动推进' : '开启自动推进'}</button>
                        <button class="editor-btn-secondary" type="button" onclick="AgentConsole.verifyRun()">运行验证</button>
                        <button class="editor-btn-primary" type="button" onclick="AgentConsole.autoStep()">自动推进一轮</button>
                        <button class="editor-btn-primary agent-primary-btn" type="button" onclick="AgentConsole.askSupervisor()">询问总调度</button>
                    </div>
                </section>

                <section class="agent-form-card">
                    <div class="agent-meta-row">
                        <span>自动推进轮次</span>
                        <strong>${run.autoStepCount || 0} / ${run.maxAutoSteps || 0}</strong>
                    </div>
                    <div class="agent-meta-row">
                        <span>工作区根目录</span>
                        <code>${escapeHtml(run.workspaceRoot || '—')}</code>
                    </div>
                    <div class="agent-helper-text">当所有 worker 结束且开启 autopilot 时，系统会自动先做一轮验证，再让总调度员生成 JSON 计划并按需继续或重开会话。</div>
                    ${run.pendingAutoStepRequested ? `<div class="agent-helper-text">已排队一轮自动推进，等待当前运行中的角色结束后自动执行。${escapeHtml(run.pendingAutoStepInstruction || '')}</div>` : ''}
                </section>

                <section class="agent-form-card">
                    <div class="agent-form-title">调度附加说明</div>
                    <textarea id="supervisorExtraInstruction" class="agent-textarea" placeholder="例如：优先收敛为可编译 MVP，再决定是否拆更多子任务"></textarea>
                </section>

                <section class="agent-form-card">
                    <div class="agent-form-title">统一续跑提示</div>
                    <textarea id="workerPromptInput" class="agent-textarea" placeholder="可选：给某个角色补一句 follow-up，比如‘继续修复构建错误并说明原因’"></textarea>
                </section>

                <section>
                    <div class="section-title-row">
                        <h3>角色会话</h3>
                    </div>
                    <div class="worker-grid">
                        ${(run.workers || []).map(worker => renderWorkerCard(worker)).join('')}
                    </div>
                </section>

                <section class="agent-form-card">
                    <div class="section-title-row">
                        <h3>最近验证结果</h3>
                    </div>
                    ${renderVerificationCard(run.lastVerification)}
                </section>

                <section class="agent-form-card">
                    <div class="section-title-row">
                        <h3>调度时间线</h3>
                    </div>
                    <div class="decision-list">
                        ${(run.decisions || []).slice().reverse().map(decision => `
                            <article class="decision-card">
                                <div class="agent-meta-row">
                                    <span class="agent-badge neutral">${escapeHtml(decision.kind || 'note')}</span>
                                    <span>${formatTime(decision.createdAt)}</span>
                                </div>
                                <div class="decision-summary">${escapeHtml(decision.summary || '')}</div>
                            </article>
                        `).join('') || '<div class="workspace-empty-inline">还没有调度记录。</div>'}
                    </div>
                </section>

                <section class="agent-form-card">
                    <div class="section-title-row">
                        <h3>最新总调度结论</h3>
                    </div>
                    <div class="worker-summary">
                        <div><strong>调度命令：</strong><code>${escapeHtml(run.lastSupervisorCommandPreview || '尚未执行')}</code></div>
                    </div>
                    <pre class="agent-log-pre">${escapeHtml(run.latestSummary || '尚未生成总调度建议')}</pre>
                </section>
            </div>
        `;
    }

    function renderVerificationCard(verification) {
        if (!verification) {
            return '<div class="workspace-empty-inline">还没有验证记录。</div>';
        }

        return `
            <div class="verification-card">
                <div class="agent-meta-row">
                    <span class="agent-badge ${verification.passed ? 'success' : (verification.status === 'skipped' ? 'neutral' : 'error')}">${escapeHtml(verification.status || 'unknown')}</span>
                    <span>${formatTime(verification.completedAt)}</span>
                </div>
                <div class="worker-summary">
                    <div><strong>命令：</strong><code>${escapeHtml(verification.command || '—')}</code></div>
                    <div><strong>摘要：</strong>${escapeHtml(verification.summary || '—')}</div>
                </div>
                <pre class="agent-log-pre">${escapeHtml(verification.outputPreview || '暂无输出')}</pre>
            </div>
        `;
    }

    function renderWorkerCard(worker) {
        return `
            <article class="worker-card">
                <div class="worker-card-header">
                    <div>
                        <div class="worker-name">${escapeHtml(worker.icon || '🤖')} ${escapeHtml(worker.roleName)}</div>
                        <div class="worker-subtitle">session: <code>${escapeHtml(worker.sessionId)}</code></div>
                    </div>
                    <span class="agent-badge ${statusClass(worker.status)}">${escapeHtml(worker.status || 'idle')}</span>
                </div>
                <div class="worker-summary">
                    <div><strong>工作路径：</strong><code>${escapeHtml(worker.workspacePath || '—')}</code></div>
                    <div><strong>任务：</strong>${escapeHtml(worker.currentTask || worker.roleDescription || '—')}</div>
                    <div><strong>返回标识：</strong>${worker.hasStructuredReport ? escapeHtml(worker.lastReportedStatus || '已返回') : '未显式返回'}</div>
                    <div><strong>摘要：</strong>${escapeHtml(worker.lastSummary || '暂无')}</div>
                    <div><strong>下一步：</strong>${escapeHtml(worker.lastNextStep || '—')}</div>
                </div>
                <div class="worker-summary">
                    <div><strong>原始命令：</strong><code>${escapeHtml(worker.effectiveCommandPreview || '尚未启动')}</code></div>
                </div>
                <div class="worker-actions">
                    <button class="editor-btn-secondary" type="button" onclick="AgentConsole.startWorker('${worker.workerId}')">启动</button>
                    <button class="editor-btn-secondary" type="button" onclick="AgentConsole.continueWorker('${worker.workerId}')">继续</button>
                    <button class="editor-btn-secondary" type="button" onclick="AgentConsole.stopWorker('${worker.workerId}')">停止</button>
                </div>
                <pre id="worker-output-${worker.workerId}" class="agent-log-pre">${escapeHtml(worker.lastOutputPreview || '暂无输出')}</pre>
            </article>
        `;
    }

    function renderRolesList() {
        const container = document.getElementById('rolesList');
        if (!container) {
            return;
        }

        if (state.roles.length === 0) {
            container.innerHTML = '<div class="workspace-empty-inline">没有角色，点上面的 ➕ 来添加。</div>';
            return;
        }

        container.innerHTML = state.roles.map(role => `
            <button type="button" class="agent-list-item ${role.roleId === state.selectedRoleId ? 'active' : ''}" onclick="AgentConsole.selectRole('${role.roleId}')">
                <div class="agent-list-item-title">${escapeHtml(role.icon || '🎭')} ${escapeHtml(role.name || '未命名角色')}</div>
                <div class="agent-list-item-subtitle">${escapeHtml(role.description || '')}</div>
            </button>
        `).join('');
    }

    function renderRoleEditor() {
        const container = document.getElementById('roleEditor');
        if (!container) {
            return;
        }

        const role = state.roles.find(item => item.roleId === state.selectedRoleId);
        if (!role) {
            container.innerHTML = '<div class="workspace-empty-state">选择一个角色开始编辑。</div>';
            return;
        }

        container.innerHTML = `
            <div class="agent-detail-shell">
                <section class="agent-form-card">
                    <div class="section-title-row">
                        <h3>基础信息</h3>
                        <button class="editor-btn-secondary" type="button" onclick="AgentConsole.removeRole()">删除角色</button>
                    </div>
                    <label class="agent-label">角色 ID</label>
                    <input class="agent-input" type="text" value="${escapeAttr(role.roleId || '')}" oninput="AgentConsole.updateRoleField('roleId', this.value)">
                    <label class="agent-label">显示名称</label>
                    <input class="agent-input" type="text" value="${escapeAttr(role.name || '')}" oninput="AgentConsole.updateRoleField('name', this.value)">
                    <label class="agent-label">图标</label>
                    <input class="agent-input" type="text" value="${escapeAttr(role.icon || '')}" oninput="AgentConsole.updateRoleField('icon', this.value)">
                    <label class="agent-label">描述</label>
                    <textarea class="agent-textarea" oninput="AgentConsole.updateRoleField('description', this.value)">${escapeHtml(role.description || '')}</textarea>
                </section>

                <section class="agent-form-card">
                    <h3>Prompt 模板</h3>
                    <p class="agent-helper-text">支持占位符：<code>{{goal}}</code>、<code>{{roleName}}</code>、<code>{{roleDescription}}</code>、<code>{{runTitle}}</code>、<code>{{peerRoles}}</code></p>
                    <textarea class="agent-textarea agent-textarea-lg" oninput="AgentConsole.updateRoleField('promptTemplate', this.value)">${escapeHtml(role.promptTemplate || '')}</textarea>
                </section>

                <section class="agent-form-card">
                    <h3>执行设置</h3>
                    <label class="agent-label">模型</label>
                    <input class="agent-input" type="text" value="${escapeAttr(role.model || 'gpt-5.4')}" oninput="AgentConsole.updateRoleField('model', this.value)">
                    <label class="agent-label">工作路径（相对仓库根目录或仓库内绝对路径）</label>
                    <input class="agent-input" type="text" value="${escapeAttr(role.workspacePath || '.')}" oninput="AgentConsole.updateRoleField('workspacePath', this.value)">
                    <label class="agent-checkbox-row">
                        <input type="checkbox" ${role.allowAllTools ? 'checked' : ''} onchange="AgentConsole.updateRoleField('allowAllTools', this.checked)">
                        <span>自动允许所有工具（<code>--allow-all-tools</code>）</span>
                    </label>
                    <label class="agent-checkbox-row">
                        <input type="checkbox" ${role.allowAllPaths ? 'checked' : ''} onchange="AgentConsole.updateRoleField('allowAllPaths', this.checked)">
                        <span>允许访问任意路径（<code>--allow-all-paths</code>，默认不建议）</span>
                    </label>
                    <label class="agent-checkbox-row">
                        <input type="checkbox" ${role.allowAllUrls ? 'checked' : ''} onchange="AgentConsole.updateRoleField('allowAllUrls', this.checked)">
                        <span>允许访问任意 URL（<code>--allow-all-urls</code>）</span>
                    </label>
                    <label class="agent-label">允许访问的 URL（每行一个，对应 <code>--allow-url</code>）</label>
                    <textarea class="agent-textarea" oninput="AgentConsole.updateRoleListField('allowedUrls', this.value)">${escapeHtml(stringifyList(role.allowedUrls))}</textarea>
                    <label class="agent-label">允许的工具（每行一个，对应 <code>--allow-tool</code>；当上方勾选全部工具时可留空）</label>
                    <textarea class="agent-textarea" oninput="AgentConsole.updateRoleListField('allowedTools', this.value)">${escapeHtml(stringifyList(role.allowedTools))}</textarea>
                    <label class="agent-label">禁止的工具（每行一个，对应 <code>--deny-tool</code>）</label>
                    <textarea class="agent-textarea" oninput="AgentConsole.updateRoleListField('deniedTools', this.value)">${escapeHtml(stringifyList(role.deniedTools))}</textarea>
                    <label class="agent-label">额外允许的路径（每行一个，对应 <code>--add-dir</code>；未勾选全部路径时生效）</label>
                    <textarea class="agent-textarea" oninput="AgentConsole.updateRoleListField('allowedPaths', this.value)">${escapeHtml(stringifyList(role.allowedPaths))}</textarea>
                    <label class="agent-label">环境变量（每行一个 <code>KEY=VALUE</code>）</label>
                    <textarea class="agent-textarea" oninput="AgentConsole.updateRoleDictField('environmentVariables', this.value)">${escapeHtml(stringifyDict(role.environmentVariables))}</textarea>
                </section>

                <section class="agent-form-card">
                    <h3>命令预览</h3>
                    <p class="agent-helper-text">这是该角色按当前配置启动时的大致原始命令。真正运行时会把 prompt / sessionId / 工作路径替换成实时值。</p>
                    <div class="worker-summary">
                        <div><strong>工作路径：</strong><code>${escapeHtml(role.workspacePath || '.')}</code></div>
                    </div>
                    <pre class="agent-log-pre">${escapeHtml(buildRoleCommandPreview(role))}</pre>
                </section>
            </div>
        `;
    }

    async function createRun() {
        const goal = document.getElementById('runGoalInput')?.value?.trim() || '';
        const title = document.getElementById('runTitleInput')?.value?.trim() || '';
        const workspaceRoot = document.getElementById('runWorkspaceRootInput')?.value?.trim() || '.';
        const autoPilotEnabled = document.getElementById('runAutopilotInput')?.checked ?? true;
        const maxAutoSteps = Number.parseInt(document.getElementById('runMaxAutoStepsInput')?.value || '6', 10);
        const roleIds = Array.from(document.querySelectorAll('#runRoleOptions input[type="checkbox"]:checked')).map(input => input.value);
        const roleValidationErrors = collectRoleValidationErrors(state.roles);

        if (!goal) {
            notify('请先填写总目标', 'error');
            return;
        }

        if (roleIds.length === 0) {
            notify('至少选择一个角色', 'error');
            return;
        }

        if (roleValidationErrors.length > 0) {
            notify(roleValidationErrors[0], 'error');
            return;
        }

        if (!Number.isInteger(maxAutoSteps) || maxAutoSteps < 1) {
            notify('最大自动推进轮次必须是大于 0 的整数', 'error');
            return;
        }

        if (!workspaceRoot) {
            notify('请填写项目起始目录，默认可用 .', 'error');
            return;
        }

        try {
            const run = await apiPost('/api/agent/runs', { title, goal, workspaceRoot, roleIds, autoStart: true, autoPilotEnabled, maxAutoSteps });
            mergeRun(run);
            state.selectedRunId = run.runId;
            renderRunsList();
            renderRunDetail();
            switchView('runs');
            notify('Run 已创建并启动', 'success');
        } catch (err) {
            console.error('Failed to create run:', err);
            notify(err.message || '创建 run 失败', 'error');
        }
    }

    async function askSupervisor() {
        const run = getSelectedRun();
        if (!run) {
            return;
        }

        const extraInstruction = document.getElementById('supervisorExtraInstruction')?.value?.trim() || null;
        try {
            const updatedRun = await apiPost(`/api/agent/runs/${run.runId}/supervisor`, { extraInstruction });
            mergeRun(updatedRun);
            renderRunDetail();
            notify('已生成总调度建议', 'success');
        } catch (err) {
            console.error('Failed to ask supervisor:', err);
            notify(err.message || '调度失败', 'error');
        }
    }

    async function autoStep() {
        const run = getSelectedRun();
        if (!run) {
            return;
        }

        const extraInstruction = document.getElementById('supervisorExtraInstruction')?.value?.trim() || null;
        try {
            const updatedRun = await apiPost(`/api/agent/runs/${run.runId}/auto-step`, { extraInstruction, runVerificationFirst: true });
            mergeRun(updatedRun);
            renderRunsList();
            renderRunDetail();
            notify(updatedRun.pendingAutoStepRequested ? '已加入自动推进队列，等待当前角色结束' : '已自动推进一轮', 'success');
        } catch (err) {
            console.error('Failed to auto-step run:', err);
            notify(err.message || '自动推进失败', 'error');
        }
    }

    async function verifyRun() {
        const run = getSelectedRun();
        if (!run) {
            return;
        }

        try {
            const updatedRun = await apiPost(`/api/agent/runs/${run.runId}/verify`, {});
            mergeRun(updatedRun);
            renderRunsList();
            renderRunDetail();
            notify('验证已完成', updatedRun.lastVerification?.passed ? 'success' : 'error');
        } catch (err) {
            console.error('Failed to verify run:', err);
            notify(err.message || '验证失败', 'error');
        }
    }

    async function toggleAutopilot(enabled) {
        const run = getSelectedRun();
        if (!run) {
            return;
        }

        try {
            const updatedRun = await apiPost(`/api/agent/runs/${run.runId}/autopilot/${enabled}`, {});
            mergeRun(updatedRun);
            renderRunsList();
            renderRunDetail();
            notify(enabled ? '已开启自动推进' : '已关闭自动推进', 'success');
        } catch (err) {
            console.error('Failed to toggle autopilot:', err);
            notify(err.message || '切换自动推进失败', 'error');
        }
    }

    async function startWorker(workerId) {
        const run = getSelectedRun();
        if (!run) {
            return;
        }

        const prompt = document.getElementById('workerPromptInput')?.value?.trim() || null;
        try {
            const updatedRun = await apiPost(`/api/agent/runs/${run.runId}/workers/${workerId}/start`, { prompt });
            mergeRun(updatedRun);
            renderRunDetail();
        } catch (err) {
            console.error('Failed to start worker:', err);
            notify(err.message || '启动角色失败', 'error');
        }
    }

    async function continueWorker(workerId) {
        const run = getSelectedRun();
        if (!run) {
            return;
        }

        const prompt = document.getElementById('workerPromptInput')?.value?.trim() || null;
        try {
            const updatedRun = await apiPost(`/api/agent/runs/${run.runId}/workers/${workerId}/continue`, { prompt });
            mergeRun(updatedRun);
            renderRunDetail();
        } catch (err) {
            console.error('Failed to continue worker:', err);
            notify(err.message || '继续角色失败', 'error');
        }
    }

    async function stopWorker(workerId) {
        const run = getSelectedRun();
        if (!run) {
            return;
        }

        try {
            const updatedRun = await apiPost(`/api/agent/runs/${run.runId}/workers/${workerId}/stop`, {});
            mergeRun(updatedRun);
            renderRunDetail();
        } catch (err) {
            console.error('Failed to stop worker:', err);
            notify(err.message || '停止角色失败', 'error');
        }
    }

    function selectRun(runId) {
        state.selectedRunId = runId;
        renderRunsList();
        renderRunDetail();
    }

    function selectRole(roleId) {
        state.selectedRoleId = roleId;
        renderRolesList();
        renderRoleEditor();
    }

    function addRole() {
        const baseId = getNextRoleId();
        const role = {
            roleId: baseId,
            name: `New Role ${state.roles.length + 1}`,
            description: '',
            icon: '🎭',
            promptTemplate: '项目目标：{{goal}}。你的角色：{{roleName}}。请开始工作，并在结尾输出 STATUS / SUMMARY / NEXT。',
            model: state.settings.defaultModel || 'gpt-5.4',
            allowAllTools: true,
            allowAllPaths: false,
            allowAllUrls: false,
            workspacePath: '.',
            allowedUrls: [],
            allowedTools: [],
            deniedTools: [],
            allowedPaths: [],
            environmentVariables: {}
        };

        state.roles.push(role);
        state.selectedRoleId = role.roleId;
        renderRoleOptions();
        renderRolesList();
        renderRoleEditor();
    }

    function updateRoleField(field, value) {
        const role = state.roles.find(item => item.roleId === state.selectedRoleId);
        if (!role) {
            return;
        }

        const previousRoleId = role.roleId;
        role[field] = value;
        if (field === 'roleId') {
            state.selectedRoleId = value || previousRoleId;
        }
        renderRoleOptions();
        renderRolesList();
        renderRoleEditor();
    }

    function updateRoleListField(field, value) {
        const role = state.roles.find(item => item.roleId === state.selectedRoleId);
        if (!role) {
            return;
        }

        role[field] = parseListInput(value);
        renderRoleEditor();
    }

    async function saveRoles() {
        const validationErrors = collectRoleValidationErrors(state.roles);
        if (validationErrors.length > 0) {
            notify(validationErrors[0], 'error');
            return;
        }

        try {
            const saved = await apiPut('/api/agent/roles', { settings: state.settings, roles: state.roles });
            state.roles = saved.roles || [];
            state.settings = saved.settings || {};
            if (!state.selectedRoleId && state.roles.length > 0) {
                state.selectedRoleId = state.roles[0].roleId;
            } else if (state.selectedRoleId && !state.roles.some(item => item.roleId === state.selectedRoleId)) {
                state.selectedRoleId = state.roles[0]?.roleId || null;
            }
            renderRoleOptions();
            renderRolesList();
            renderRoleEditor();
            renderSettingsEditor();
            notify('角色已保存', 'success');
        } catch (err) {
            console.error('Failed to save roles:', err);
            notify(err.message || '保存角色失败', 'error');
        }
    }

    function removeRole() {
        if (state.roles.length <= 1) {
            notify('至少保留一个角色模板', 'error');
            return;
        }

        state.roles = state.roles.filter(item => item.roleId !== state.selectedRoleId);
        state.selectedRoleId = state.roles[0]?.roleId || null;
        renderRoleOptions();
        renderRolesList();
        renderRoleEditor();
    }

    function mergeRun(run) {
        const index = state.runs.findIndex(item => item.runId === run.runId);
        if (index >= 0) {
            state.runs[index] = run;
        } else {
            state.runs.unshift(run);
        }
    }

    function getSelectedRun() {
        return state.runs.find(item => item.runId === state.selectedRunId) || null;
    }

    async function apiGet(url) {
        const response = await fetch(url);
        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        return response.json();
    }

    async function apiPost(url, payload) {
        const response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload ?? {})
        });

        const body = await response.json().catch(() => null);
        if (!response.ok) {
            throw new Error(body?.detail || body?.error || `HTTP ${response.status}`);
        }

        return body;
    }

    async function apiPut(url, payload) {
        const response = await fetch(url, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload ?? {})
        });

        const body = await response.json().catch(() => null);
        if (!response.ok) {
            throw new Error(body?.detail || body?.error || `HTTP ${response.status}`);
        }

        return body;
    }

    function notify(message, type = 'success') {
        const toast = document.createElement('div');
        toast.className = `toast toast-${type}`;
        toast.textContent = message;
        document.body.appendChild(toast);
        setTimeout(() => toast.classList.add('toast-visible'), 10);
        setTimeout(() => {
            toast.classList.remove('toast-visible');
            setTimeout(() => toast.remove(), 300);
        }, 2600);
    }

    function trimOutput(text) {
        if (!text) {
            return '';
        }

        return text.length > 3000 ? text.slice(-3000) : text;
    }

    function collectRoleValidationErrors(roles) {
        if (!Array.isArray(roles) || roles.length === 0) {
            return ['至少保留一个角色模板'];
        }

        const errors = [];
        const seenRoleIds = new Set();

        roles.forEach((role, index) => {
            const roleId = (role?.roleId || '').trim();
            const name = (role?.name || '').trim();
            const promptTemplate = (role?.promptTemplate || '').trim();
            const workspacePath = (role?.workspacePath || '.').trim();
            const roleLabel = roleId || `第 ${index + 1} 个角色`;
            const normalizedRoleId = roleId.toLowerCase();

            if (!roleId) {
                errors.push(`第 ${index + 1} 个角色缺少角色 ID`);
            } else if (seenRoleIds.has(normalizedRoleId)) {
                errors.push(`角色 ID“${roleId}”重复，请修改后再保存`);
            } else {
                seenRoleIds.add(normalizedRoleId);
            }

            if (!name) {
                errors.push(`${roleLabel}缺少显示名称`);
            }

            if (!promptTemplate) {
                errors.push(`${roleLabel}缺少 Prompt 模板`);
            }

            if (!workspacePath) {
                errors.push(`${roleLabel}缺少工作路径`);
            }
        });

        return errors;
    }

    function getNextRoleId() {
        let index = state.roles.length + 1;
        while (state.roles.some(role => (role?.roleId || '').trim().toLowerCase() === `role-${index}`)) {
            index += 1;
        }

        return `role-${index}`;
    }

    function statusClass(status) {
        switch ((status || '').toLowerCase()) {
            case 'running':
            case 'queued':
                return 'running';
            case 'completed':
            case 'review':
                return 'success';
            case 'failed':
                return 'error';
            case 'stopped':
                return 'warning';
            default:
                return 'neutral';
        }
    }

    function formatTime(value) {
        if (!value) {
            return '—';
        }

        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text ?? '';
        return div.innerHTML;
    }

    function escapeAttr(text) {
        return escapeHtml(text).replace(/"/g, '&quot;');
    }

    function parseListInput(value) {
        return (value || '')
            .split(/\r?\n|,|;/)
            .map(item => item.trim())
            .filter(Boolean);
    }

    function stringifyList(values) {
        return Array.isArray(values) ? values.join('\n') : '';
    }

    function buildRoleCommandPreview(role) {
        const args = [
            'gh',
            'copilot',
            '--',
            '-p',
            '"<prompt>"',
            '-s',
            '--no-ask-user',
            '--resume=<sessionId>',
            '--model',
            quoteCommandArg(role.model || 'gpt-5.4')
        ];

        if (role.allowAllTools) {
            args.push('--allow-all-tools');
        } else {
            (role.allowedTools || []).forEach(tool => {
                args.push('--allow-tool', quoteCommandArg(tool));
            });
        }

        if (role.allowAllPaths) {
            args.push('--allow-all-paths');
        } else {
            args.push('--add-dir', quoteCommandArg(role.workspacePath || '.'));
            (role.allowedPaths || []).forEach(p => {
                args.push('--add-dir', quoteCommandArg(p));
            });
        }

        if (role.allowAllUrls) {
            args.push('--allow-all-urls');
        } else {
            (role.allowedUrls || []).forEach(url => {
                args.push('--allow-url', quoteCommandArg(url));
            });
        }

        (role.deniedTools || []).forEach(tool => {
            args.push('--deny-tool', quoteCommandArg(tool));
        });

        return args.join(' ');
    }

    function updateRoleDictField(field, value) {
        const role = state.roles.find(item => item.roleId === state.selectedRoleId);
        if (!role) {
            return;
        }

        role[field] = parseDictInput(value);
    }

    function renderSettingsEditor() {
        const container = document.getElementById('settingsEditor');
        if (!container) {
            return;
        }

        const s = state.settings || {};

        container.innerHTML = `
            <div class="agent-detail-shell">
                <section class="agent-form-card">
                    <h3>🤖 Supervisor（调度员）配置</h3>
                    <p class="agent-helper-text">控制自动调度员的模型和行为。Supervisor 负责在每轮自动推进时决定如何分配任务。</p>
                    <label class="agent-label">Supervisor 模型</label>
                    <input class="agent-input" type="text" value="${escapeAttr(s.supervisorModel || 'gpt-5.4')}" oninput="AgentConsole.updateSettingsField('supervisorModel', this.value)">
                    <label class="agent-label">Supervisor 提示词前缀（可选，会插入到每次调度 prompt 开头）</label>
                    <textarea class="agent-textarea agent-textarea-lg" oninput="AgentConsole.updateSettingsField('supervisorPromptPrefix', this.value)">${escapeHtml(s.supervisorPromptPrefix || '')}</textarea>
                </section>

                <section class="agent-form-card">
                    <h3>🎯 角色全局默认值</h3>
                    <p class="agent-helper-text">新角色创建时的默认配置；角色自身配置优先于此处。</p>
                    <label class="agent-label">默认模型</label>
                    <input class="agent-input" type="text" value="${escapeAttr(s.defaultModel || 'gpt-5.4')}" oninput="AgentConsole.updateSettingsField('defaultModel', this.value)">
                    <label class="agent-label">默认工作目录（留空则自动检测 .sln 所在目录）</label>
                    <input class="agent-input" type="text" value="${escapeAttr(s.defaultWorkspaceRoot || '')}" oninput="AgentConsole.updateSettingsField('defaultWorkspaceRoot', this.value)">
                </section>

                <section class="agent-form-card">
                    <h3>🚀 Run 默认参数</h3>
                    <p class="agent-helper-text">创建新 Run 时的默认配置。</p>
                    <label class="agent-label">默认最大自动推进轮次</label>
                    <input class="agent-input" type="number" min="1" max="100" value="${s.defaultMaxAutoSteps ?? 6}" oninput="AgentConsole.updateSettingsField('defaultMaxAutoSteps', parseInt(this.value) || 6)">
                    <label class="agent-checkbox-row">
                        <input type="checkbox" ${s.defaultAutoPilotEnabled !== false ? 'checked' : ''} onchange="AgentConsole.updateSettingsField('defaultAutoPilotEnabled', this.checked)">
                        <span>默认启用自动驾驶（AutoPilot）</span>
                    </label>
                    <label class="agent-label">默认验证命令（留空则自动检测 dotnet build / npm build 等）</label>
                    <input class="agent-input" type="text" value="${escapeAttr(s.defaultVerificationCommand || '')}" oninput="AgentConsole.updateSettingsField('defaultVerificationCommand', this.value)">
                </section>

                <section class="agent-form-card">
                    <h3>🛡️ 安全与限制</h3>
                    <label class="agent-label">最大并发 Worker 数量</label>
                    <input class="agent-input" type="number" min="1" max="20" value="${s.maxConcurrentWorkers ?? 4}" oninput="AgentConsole.updateSettingsField('maxConcurrentWorkers', parseInt(this.value) || 4)">
                    <label class="agent-label">Worker 超时时间（分钟，超时后自动终止进程）</label>
                    <input class="agent-input" type="number" min="1" max="1440" value="${s.workerTimeoutMinutes ?? 30}" oninput="AgentConsole.updateSettingsField('workerTimeoutMinutes', parseInt(this.value) || 30)">
                    <label class="agent-label">输出缓冲区最大字符数</label>
                    <input class="agent-input" type="number" min="1000" max="100000" value="${s.outputBufferMaxChars ?? 12000}" oninput="AgentConsole.updateSettingsField('outputBufferMaxChars', parseInt(this.value) || 12000)">
                    <label class="agent-label">决策历史保留条数</label>
                    <input class="agent-input" type="number" min="10" max="500" value="${s.decisionHistoryLimit ?? 40}" oninput="AgentConsole.updateSettingsField('decisionHistoryLimit', parseInt(this.value) || 40)">
                </section>

                <section class="agent-form-card">
                    <h3>🌐 全局环境变量</h3>
                    <p class="agent-helper-text">每行一个 <code>KEY=VALUE</code>，将注入到所有 Worker 和 Supervisor 进程中。角色自身的环境变量会覆盖此处的同名变量。</p>
                    <textarea class="agent-textarea agent-textarea-lg" oninput="AgentConsole.updateSettingsField('environmentVariables', AgentConsole.parseDictInput(this.value))">${escapeHtml(stringifyDict(s.environmentVariables))}</textarea>
                </section>
            </div>
        `;
    }

    async function saveSettings() {
        try {
            const saved = await apiPut('/api/agent/settings', state.settings);
            state.settings = saved || {};
            renderSettingsEditor();
            notify('设置已保存', 'success');
        } catch (err) {
            console.error('Failed to save settings:', err);
            notify(err.message || '保存设置失败', 'error');
        }
    }

    function updateSettingsField(field, value) {
        state.settings = state.settings || {};
        state.settings[field] = value;
    }

    function parseDictInput(value) {
        const result = {};
        (value || '').split(/\r?\n/).forEach(line => {
            const trimmed = line.trim();
            if (!trimmed) {
                return;
            }
            const eqIdx = trimmed.indexOf('=');
            if (eqIdx > 0) {
                result[trimmed.substring(0, eqIdx).trim()] = trimmed.substring(eqIdx + 1).trim();
            }
        });
        return result;
    }

    function stringifyDict(dict) {
        if (!dict || typeof dict !== 'object') {
            return '';
        }
        return Object.entries(dict).map(([k, v]) => `${k}=${v}`).join('\n');
    }

    function quoteCommandArg(value) {
        const text = `${value ?? ''}`;
        if (!text) {
            return '""';
        }

        return /\s|["'&();]/.test(text)
            ? `"${text.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`
            : text;
    }

    return {
        init,
        switchView,
        selectRun,
        selectRole,
        startWorker,
        continueWorker,
        stopWorker,
        askSupervisor,
        autoStep,
        verifyRun,
        toggleAutopilot,
        addRole,
        saveRoles,
        removeRole,
        updateRoleField,
        updateRoleListField,
        updateRoleDictField,
        saveSettings,
        updateSettingsField,
        parseDictInput
    };
})();

window.AgentConsole = AgentConsole;

document.addEventListener('DOMContentLoaded', () => {
    AgentConsole.init();
});
