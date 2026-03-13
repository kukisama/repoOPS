(function () {
    const AgentConsole = window.AgentConsole;
    const Runs = AgentConsole.Runs;

    const statusLabels = {
        draft: '草稿',
        running: '运行中',
        queued: '排队中',
        verifying: '检查中',
        orchestrating: '调度中',
        review: '待复核',
        'needs-human': '待人工处理',
        'needs-attention': '待处理',
        paused: '已暂停',
        completed: '已完成',
        archived: '已归档',
        failed: '失败',
        error: '错误',
        idle: '空闲',
        stopped: '已停止',
        passed: '通过',
        success: '成功',
        blocked: '阻塞',
        skipped: '未执行'
    };

    const attentionLevelLabels = {
        stable: '稳定',
        neutral: '正常',
        info: '信息',
        warning: '需关注',
        error: '异常'
    };

    const laneKindLabels = {
        workspace: '工作台',
        coordinator: '调度台',
        verification: '构建/检查'
    };

    const surfaceTypeLabels = {
        verification: '构建/检查面板',
        coordinator: '调度面板'
    };

    const defaultWorkspaceInputPlaceholder = '留空则使用 EXE 根目录并自动创建英文任务目录；手动填写时这里就是硬边界目录';

    const runActionHints = {
        'auto-step': '让调度器基于当前进度继续安排下一轮动作，适合“先往前推一步”。',
        'ask-supervisor': '不直接动手，只让调度器先给建议，适合你想先听听系统判断。',
        'verify': '补做一次统一的构建/测试检查，用来兜底确认当前成果是否能站住。',
        'open-folder': '直接打开这条 Run 当前所在的项目目录，方便你去看真实文件、日志和产物。',
        'export-md': '导出一份可读的 Markdown 摘要，方便发给别人看或留档。',
        'toggle-autopilot': '切换自动推进模式；开启后，系统会在合适时机继续往下跑。',
        pause: '先把这条 Run 的自动协作节奏按住，适合你想人工介入一会儿。',
        resume: '恢复这条 Run 的协作节奏，让系统重新接着推进。',
        complete: '当你确认目标基本达成时，手动把这条 Run 标记为完成。',
        archive: '把已收尾的 Run 收起来，避免列表越来越像旧货市场。',
        refresh: '重新拉取最新状态，适合你怀疑页面还没来得及更新的时候。',
        'ack-all-attention': '把当前所有提醒标成已读，适合你已经看过一轮但还没逐条处理。'
    };

    const workerActionHints = {
        start: '按当前角色模板启动一次新会话，适合第一次开工。',
        continue: '沿用已有上下文继续干活，适合接着上一轮往下做。',
        stop: '停止这个角色的当前会话，适合需要人工接管或重来时。'
    };

    const surfaceActionHints = {
        focus: '把注意力聚焦到这个面板，适合你想盯住某个角色当前进展。'
    };

    const attentionActionHints = {
        ack: '标记已读，表示你已经看过这条提醒。',
        resolve: '标记已处理，表示这条提醒已经不用再挂着了。'
    };

    function formatStatusLabel(status) {
        const normalized = String(status || '').trim().toLowerCase();
        return statusLabels[normalized] || status || '未知';
    }

    function formatLaneKind(kind) {
        const normalized = String(kind || '').trim().toLowerCase();
        return laneKindLabels[normalized] || kind || '工作区';
    }

    function formatSurfaceType(type) {
        const normalized = String(type || '').trim().toLowerCase();
        return surfaceTypeLabels[normalized] || type || '执行面板';
    }

    function formatAttentionLevel(level) {
        const normalized = String(level || '').trim().toLowerCase();
        return attentionLevelLabels[normalized] || level || '正常';
    }

    function formatMultilineText(value) {
        return AgentConsole.utils.escapeHtml(value || '').replace(/\r?\n/g, '<br>');
    }

    function formatPreText(value) {
        return AgentConsole.utils.escapeHtml(value || '');
    }

    function tailText(value, maxLength = 320) {
        const text = String(value || '').trim();
        if (!text) {
            return '';
        }

        return text.length <= maxLength ? text : `…${text.slice(-maxLength)}`;
    }

    function mergeLiveCoordinatorSurface(snapshot) {
        const runId = snapshot?.run?.runId;
        const live = runId ? AgentConsole.state.supervisorStreams?.get(runId) : null;
        if (!live || !(live.active || live.output)) {
            return snapshot;
        }

        const surfaces = Array.isArray(snapshot.surfaces) ? [...snapshot.surfaces] : [];
        const coordinatorIndex = surfaces.findIndex(surface => surface.type === 'coordinator');
        const merged = coordinatorIndex >= 0
            ? { ...surfaces[coordinatorIndex] }
            : {
                surfaceId: 'surface:coordinator',
                runId,
                laneId: 'lane_control',
                type: 'coordinator',
                displayName: 'Coordinator',
                status: 'running'
            };

        merged.status = live.active ? 'running' : (live.status || merged.status || 'idle');
        merged.currentTask = live.title || merged.currentTask;
        merged.commandPreview = live.commandPreview || merged.commandPreview;
        merged.lastReportedStatus = live.active ? 'streaming' : (live.status || merged.lastReportedStatus);
        merged.lastOutputPreview = live.output || merged.lastOutputPreview;
        merged.lastAttentionMessage = live.error || merged.lastAttentionMessage;
        merged.lastActivityAt = live.updatedAt || merged.lastActivityAt;
        merged.updatedAt = live.updatedAt || merged.updatedAt;

        if (coordinatorIndex >= 0) {
            surfaces[coordinatorIndex] = merged;
        } else {
            surfaces.push(merged);
        }

        return {
            ...snapshot,
            surfaces
        };
    }

    function extractJsonObject(text) {
        const raw = String(text || '').trim();
        if (!raw) {
            return null;
        }

        const start = raw.indexOf('{');
        const end = raw.lastIndexOf('}');
        if (start < 0 || end <= start) {
            return null;
        }

        return raw.slice(start, end + 1);
    }

    function tryParseCoordinatorJson(value) {
        const json = extractJsonObject(value);
        if (!json) {
            return null;
        }

        try {
            const parsed = JSON.parse(json);
            return parsed && typeof parsed === 'object' ? parsed : null;
        } catch {
            return null;
        }
    }

    function tryParseStatusSummaryNext(value) {
        const raw = String(value || '').trim();
        if (!raw) {
            return null;
        }

        const lines = raw.split(/\r?\n/);
        const sections = [];
        let current = null;

        lines.forEach(line => {
            const match = line.match(/^\s*(STATUS|SUMMARY|NEXT)\s*[:：-]?\s*(.*)$/i);
            if (match) {
                current = {
                    key: match[1].toUpperCase(),
                    value: (match[2] || '').trim()
                };
                sections.push(current);
                return;
            }

            if (current) {
                current.value = current.value ? `${current.value}\n${line}` : line;
            }
        });

        return sections.length ? sections : null;
    }

    function renderCoordinatorBulletList(items) {
        const values = (items || []).filter(Boolean);
        if (!values.length) {
            return '';
        }

        return `<ul>${values.map(item => `<li>${AgentConsole.utils.escapeHtml(item)}</li>`).join('')}</ul>`;
    }

    function renderCoordinatorPlanSummary(summary) {
        const parsedJson = tryParseCoordinatorJson(summary);
        if (parsedJson && (parsedJson.run_status || parsedJson.next_round_plan || parsedJson.decision || parsedJson.observed_state)) {
            const runStatus = parsedJson.run_status || {};
            const implementation = runStatus.implementation_summary || {};
            const currentRecheck = runStatus.evidence?.current_recheck || null;
            const observations = Array.isArray(runStatus.observations) ? runStatus.observations : [];
            const nextRoundPlan = parsedJson.next_round_plan || {};
            const dispatch = Array.isArray(nextRoundPlan.dispatch) ? nextRoundPlan.dispatch : [];
            const blockingItems = Array.isArray(nextRoundPlan.blocking_items) ? nextRoundPlan.blocking_items : [];
            const priorities = Array.isArray(nextRoundPlan.priority) ? nextRoundPlan.priority : [];
            const decision = parsedJson.decision || {};
            const observedState = parsedJson.observed_state || {};
            const rolePlans = Array.isArray(nextRoundPlan.roles) ? nextRoundPlan.roles : [];
            const actionType = parsedJson.next_action_type || nextRoundPlan.action_type || decision.next_action || '';
            const goal = runStatus.goal || nextRoundPlan.objective || decision.focus || observedState.current_round_goal || '已返回结构化判断';
            const workspace = runStatus.workspace || observedState.workspace_root || parsedJson.workspace || '';
            const reason = decision.reason || currentRecheck?.reason || currentRecheck?.result || '';
            const missingArtifacts = Array.isArray(observedState.missing_required_artifacts) ? observedState.missing_required_artifacts : [];
            const afterRound = Array.isArray(parsedJson.after_round_1_if_completed) ? parsedJson.after_round_1_if_completed : [];

            return `
                <section class="agent-status-callout warning">
                    <div class="agent-status-line">
                        <strong>调度结论</strong>
                        <div>${AgentConsole.utils.escapeHtml(runStatus.state || actionType || '已返回结构化判断')}</div>
                    </div>
                    <div class="agent-status-line">
                        <strong>当前目标</strong>
                        <div>${AgentConsole.utils.escapeHtml(goal)}</div>
                    </div>
                    ${workspace ? `
                        <div class="agent-status-line">
                            <strong>工作区</strong>
                            <div>${AgentConsole.utils.escapeHtml(workspace)}</div>
                        </div>
                    ` : ''}
                    ${currentRecheck ? `
                        <div class="agent-status-line">
                            <strong>这轮为什么还卡着</strong>
                            <div>${AgentConsole.utils.escapeHtml(currentRecheck.reason || currentRecheck.result || '需要人工确认')}</div>
                        </div>
                    ` : ''}
                    ${reason ? `
                        <div class="agent-status-line">
                            <strong>为什么这么安排</strong>
                            <div>${AgentConsole.utils.escapeHtml(reason)}</div>
                        </div>
                    ` : ''}
                </section>
                ${(implementation.core_module || implementation.cli_entry || implementation.tests || implementation.docs) ? `
                    <div class="surface-aux-list">
                        <div><strong>当前已识别的实现：</strong></div>
                        ${implementation.core_module ? `<div>核心模块：<code>${AgentConsole.utils.escapeHtml(implementation.core_module)}</code></div>` : ''}
                        ${implementation.cli_entry ? `<div>命令入口：<code>${AgentConsole.utils.escapeHtml(implementation.cli_entry)}</code></div>` : ''}
                        ${implementation.tests ? `<div>测试文件：<code>${AgentConsole.utils.escapeHtml(implementation.tests)}</code></div>` : ''}
                        ${implementation.docs ? `<div>说明文档：<code>${AgentConsole.utils.escapeHtml(implementation.docs)}</code></div>` : ''}
                    </div>
                ` : ''}
                ${observations.length ? `
                    <div class="surface-aux-list">
                        <div><strong>Coordinator 观察到的问题</strong></div>
                        ${renderCoordinatorBulletList(observations.map(item => item.detail || item.type || ''))}
                    </div>
                ` : ''}
                ${blockingItems.length ? `
                    <div class="surface-aux-list">
                        <div><strong>当前阻塞项</strong></div>
                        ${renderCoordinatorBulletList(blockingItems.map(item => item.detail || item.id || ''))}
                    </div>
                ` : ''}
                ${missingArtifacts.length ? `
                    <div class="surface-aux-list">
                        <div><strong>当前缺的交付物</strong></div>
                        ${renderCoordinatorBulletList(missingArtifacts)}
                    </div>
                ` : ''}
                ${(nextRoundPlan.objective || dispatch.length || priorities.length) ? `
                    <div class="surface-aux-list">
                        ${nextRoundPlan.objective ? `<div><strong>建议目标：</strong>${AgentConsole.utils.escapeHtml(nextRoundPlan.objective)}</div>` : ''}
                        ${priorities.length ? `<div><strong>优先级：</strong>${AgentConsole.utils.escapeHtml(priorities.join(' → '))}</div>` : ''}
                        ${dispatch.length ? `<div><strong>建议下一步：</strong>${renderCoordinatorBulletList(dispatch.slice(0, 4).map(item => `步骤 ${item.order || '?'} · ${item.agent_role || 'agent'}：${item.task || '未说明'}`))}</div>` : ''}
                    </div>
                ` : ''}
                ${rolePlans.length ? `
                    <div class="surface-aux-list">
                        <div><strong>已拆给各角色的工作</strong></div>
                        ${renderCoordinatorBulletList(rolePlans.slice(0, 6).map(role => {
                            const tasks = Array.isArray(role.tasks) ? role.tasks.filter(Boolean) : [];
                            const outputs = Array.isArray(role.output_artifacts) ? role.output_artifacts.filter(Boolean) : [];
                            const taskText = tasks.length ? tasks.join('；') : '未写明任务';
                            const outputText = outputs.length ? `；预期产物：${outputs.join('、')}` : '';
                            return `${role.role || role.agent_role || 'agent'}：${taskText}${outputText}`;
                        }))}
                    </div>
                ` : ''}
                ${afterRound.length ? `
                    <div class="surface-aux-list">
                        <div><strong>后续轮次预告</strong></div>
                        ${renderCoordinatorBulletList(afterRound)}
                    </div>
                ` : ''}
            `;
        }

        const statusSections = tryParseStatusSummaryNext(summary);
        if (statusSections) {
            return `
                <section class="agent-status-callout neutral">
                    ${statusSections.map(section => `
                        <div class="agent-status-line">
                            <strong>${AgentConsole.utils.escapeHtml(section.key)}</strong>
                            <div>${formatMultilineText(section.value)}</div>
                        </div>
                    `).join('')}
                </section>
            `;
        }

        return `
            <section class="agent-status-callout neutral">
                <div class="agent-status-line">
                    <strong>调度器返回</strong>
                    <div>${formatMultilineText(summary)}</div>
                </div>
            </section>
        `;
    }

    function renderCoordinatorLiveSummary(surface) {
        const preview = tailText(surface.lastOutputPreview || '', 360);
        const latestKnownSummary = surface.lastSummary && surface.lastSummary !== surface.lastOutputPreview
            ? `
                <div class="agent-status-line">
                    <strong>上一轮已落库结论</strong>
                    <div>${formatMultilineText(surface.lastSummary)}</div>
                </div>
            `
            : '';

        return `
            <section class="agent-status-callout running">
                <div class="agent-status-line">
                    <strong>执行状态</strong>
                    <div>${AgentConsole.utils.escapeHtml(formatStatusLabel(surface.status || 'running'))} · ${AgentConsole.utils.escapeHtml(surface.lastReportedStatus || '流式返回中')}</div>
                </div>
                <div class="agent-status-line">
                    <strong>调度器正在实时返回</strong>
                    <div>${AgentConsole.utils.escapeHtml(surface.currentTask || '已发起调度请求，下面会持续刷新返回内容。')}</div>
                </div>
                <div class="agent-status-line">
                    <strong>当前已返回</strong>
                    <div>${formatMultilineText(preview || '已经连上流，但暂时还没看到可显示的文本。')}</div>
                </div>
                ${latestKnownSummary}
            </section>
        `;
    }

    function normalizeTerminalId(value) {
        return String(value || 'terminal')
            .toLowerCase()
            .replace(/[^a-z0-9_-]+/g, '-')
            .replace(/^-+|-+$/g, '') || 'terminal';
    }

    function getSurfaceToneClass(surface) {
        const tone = AgentConsole.utils.statusTone(surface.status);
        return `status-${tone}`;
    }

    function getVerificationState(record) {
        if (!record) {
            return {
                tone: 'neutral',
                title: '尚未执行',
                detail: '还没有触发过补充构建/检查。'
            };
        }

        if (String(record.status || '').trim().toLowerCase() === 'skipped' || record.command === '<none>') {
            return {
                tone: 'warning',
                title: '这次没有真正执行',
                detail: '当前环境里没有默认构建/检查命令，所以系统记录了一次“未执行”，不是命令跑失败。'
            };
        }

        if (record.passed) {
            return {
                tone: 'success',
                title: '构建/检查已执行并通过',
                detail: record.exitCode == null ? '命令执行成功。' : `命令执行成功，退出码 ${record.exitCode}。`
            };
        }

        return {
            tone: 'error',
            title: record.exitCode == null || record.exitCode < 0 ? '执行时就出错了' : '命令已执行，但结果失败',
            detail: record.exitCode == null || record.exitCode < 0
                ? '这说明构建/检查命令在启动或运行阶段出了异常。'
                : `命令确实跑了，但以失败退出，退出码 ${record.exitCode}。`
        };
    }

    function renderRawOutputPanel(title, output, terminalId, outputPreviews) {
        if (!output) {
            return '';
        }

        const normalizedId = normalizeTerminalId(terminalId);
        outputPreviews.push({ id: normalizedId, output });

        return `
            <section class="agent-terminal-shell">
                <div class="agent-terminal-toolbar">
                    <strong>${AgentConsole.utils.escapeHtml(title)}</strong>
                    <span>原生终端视图 · 保留换行与 ANSI 风格</span>
                </div>
                <div class="agent-terminal-host" data-agent-terminal-id="${normalizedId}"></div>
            </section>
        `;
    }

    function renderCommandPreviewBlock(commandPreview, workspacePath) {
        if (!commandPreview && !workspacePath) {
            return '';
        }

        return `
            <section class="agent-command-shell">
                <div class="agent-command-toolbar">
                    <strong>执行命令</strong>
                    <span>${AgentConsole.utils.escapeHtml(workspacePath ? `工作目录：${workspacePath}` : '可复制回放')}</span>
                </div>
                <pre class="agent-command-pre">${formatPreText(commandPreview || '尚未生成命令预览')}</pre>
            </section>
        `;
    }

    function parseAllowedDirectoriesFromCommand(commandPreview) {
        const command = String(commandPreview || '');
        if (!command) {
            return [];
        }

        const results = [];
        const seen = new Set();
        const regex = /--add-dir\s+(?:"([^"]+)"|'([^']+)'|([^\s|]+))/gi;
        let match;

        while ((match = regex.exec(command)) !== null) {
            const value = (match[1] || match[2] || match[3] || '').trim();
            if (!value) {
                continue;
            }

            const normalized = value.toLowerCase();
            if (seen.has(normalized)) {
                continue;
            }

            seen.add(normalized);
            results.push(value);
        }

        return results;
    }

    function renderAllowedDirectoriesBlock(title, directories, helperText) {
        const items = (directories || []).filter(Boolean);
        if (!items.length) {
            return '';
        }

        return `
            <section class="agent-status-callout neutral">
                <div class="agent-status-line">
                    <strong>${AgentConsole.utils.escapeHtml(title)}</strong>
                    <div>${AgentConsole.utils.escapeHtml(helperText || '这些目录会作为 `--add-dir` 传给当前命令。')}</div>
                </div>
                <div class="surface-aux-list">
                    ${items.map(item => `<div><code>${AgentConsole.utils.escapeHtml(item)}</code></div>`).join('')}
                </div>
            </section>
        `;
    }

    function getSurfaceTabLabel(surface) {
        if (!surface) {
            return '面板';
        }

        if (surface.type === 'coordinator') {
            return '超管';
        }

        if (surface.type === 'verification') {
            return '验证';
        }

        return surface.displayName || formatSurfaceType(surface.type);
    }

    function sortSurfaceTabs(surfaces) {
        const priority = surface => {
            if (surface.type === 'coordinator') {
                return 0;
            }

            if (surface.workerId) {
                return 1;
            }

            if (surface.type === 'verification') {
                return 2;
            }

            return 3;
        };

        return [...(surfaces || [])].sort((left, right) => {
            const priorityDiff = priority(left) - priority(right);
            if (priorityDiff !== 0) {
                return priorityDiff;
            }

            return String(getSurfaceTabLabel(left)).localeCompare(String(getSurfaceTabLabel(right)), 'zh-CN');
        });
    }

    function resolveActiveSurface(snapshot) {
        const surfaces = sortSurfaceTabs(snapshot.surfaces || []);
        if (!surfaces.length) {
            return { surfaces: [], activeSurface: null };
        }

        const activeSurface = surfaces.find(surface => surface.surfaceId === snapshot.run?.activeSurfaceId) || surfaces[0];
        return { surfaces, activeSurface };
    }

    function renderSurfaceTabs(snapshot) {
        const { surfaces, activeSurface } = resolveActiveSurface(snapshot);
        if (!surfaces.length) {
            return '';
        }

        return `
            <div class="surface-tabs-bar" role="tablist" aria-label="执行面板切换">
                ${surfaces.map(surface => {
                    const isActive = activeSurface?.surfaceId === surface.surfaceId;
                    const tone = AgentConsole.utils.statusTone(surface.status);
                    const isWorkingRole = !!surface.workerId && (surface.status === 'running' || surface.status === 'queued');
                    const unread = surface.unreadCount > 0 ? `<span class="surface-tab-unread">${surface.unreadCount}</span>` : '';
                    return `
                        <button class="surface-tab ${isActive ? 'active' : ''} ${isWorkingRole ? 'is-working' : ''}" type="button" data-surface-action="focus" data-surface-id="${AgentConsole.utils.escapeHtml(surface.surfaceId)}" role="tab" aria-selected="${isActive ? 'true' : 'false'}">
                            <span class="surface-tab-dot ${tone}"></span>
                            <span>${AgentConsole.utils.escapeHtml(getSurfaceTabLabel(surface))}</span>
                            ${unread}
                        </button>
                    `;
                }).join('')}
            </div>
        `;
    }

    function renderStructuredSurfaceSummary(surface) {
        const coordinatorReportedStatus = String(surface.lastReportedStatus || '').trim().toLowerCase();
        if (surface.type === 'coordinator' && (coordinatorReportedStatus === 'streaming' || (surface.status === 'running' && surface.lastOutputPreview))) {
            return renderCoordinatorLiveSummary(surface);
        }

        if (surface.type === 'coordinator' && surface.lastSummary) {
            return renderCoordinatorPlanSummary(surface.lastSummary);
        }

        const items = [];
        if (surface.lastReportedStatus) {
            items.push({ label: '状态', value: formatStatusLabel(surface.lastReportedStatus) });
        }
        if (surface.lastSummary) {
            items.push({ label: '摘要', value: surface.lastSummary });
        }
        if (surface.lastNextStep) {
            items.push({ label: '下一步', value: surface.lastNextStep });
        }

        if (!items.length) {
            return '';
        }

        return `
            <section class="agent-status-callout ${surface.lastReportedStatus ? AgentConsole.utils.statusTone(surface.lastReportedStatus) : 'neutral'}">
                ${items.map(item => `
                    <div class="agent-status-line">
                        <strong>${AgentConsole.utils.escapeHtml(item.label)}</strong>
                        <div>${formatMultilineText(item.value)}</div>
                    </div>
                `).join('')}
            </section>
        `;
    }

    function renderRunProgressOverview(run, snapshot) {
        const workerSurfaces = (snapshot.surfaces || []).filter(surface => !!surface.workerId);
        const hasCoordinatorSurface = (snapshot.surfaces || []).some(surface => surface.type === 'coordinator');
        const totalWorkers = workerSurfaces.length;
        const completedWorkers = workerSurfaces.filter(surface => surface.status === 'completed').length;
        const activeWorkers = workerSurfaces.filter(surface => surface.status === 'running' || surface.status === 'queued').length;
        const blockedWorkers = workerSurfaces.filter(surface => {
            const reported = String(surface.lastReportedStatus || '').trim().toLowerCase();
            return reported === 'needs-human' || reported === 'blocked' || reported === 'needs-attention';
        }).length;
        const failedWorkers = workerSurfaces.filter(surface => surface.status === 'failed').length;
        const progressPercent = totalWorkers > 0 ? Math.round((completedWorkers / totalWorkers) * 100) : 0;
        const latestVerification = (snapshot.verifications || [])[0] || run.lastVerification || null;
        const verificationState = getVerificationState(latestVerification);

        return `
            <section class="run-progress-overview">
                <div class="run-progress-header">
                    <div>
                        <h3>整体进度</h3>
                        <div class="agent-helper-text">这不是“看起来差不多”，而是把当前推进到哪一步直接摊开给你看。</div>
                    </div>
                    <span class="agent-badge ${AgentConsole.utils.statusTone(run.status)}">${AgentConsole.utils.escapeHtml(formatStatusLabel(run.status))}</span>
                </div>
                <div class="run-progress-bar">
                    <div class="run-progress-bar-fill" style="width: ${progressPercent}%;"></div>
                </div>
                <div class="run-progress-meta">已完成 ${completedWorkers}/${Math.max(totalWorkers, 1)} 个执行角色 · 运行中 ${activeWorkers} · 卡住 ${blockedWorkers} · 失败 ${failedWorkers}</div>
                <div class="run-progress-chips">
                    <span class="run-progress-chip success">角色完成 ${completedWorkers}</span>
                    <span class="run-progress-chip running">正在处理 ${activeWorkers}</span>
                    <span class="run-progress-chip warning">待拍板 ${snapshot.summary?.needsAttention || 0}</span>
                    <span class="run-progress-chip ${verificationState.tone}">${AgentConsole.utils.escapeHtml(verificationState.title)}</span>
                </div>
                ${!hasCoordinatorSurface && run.latestSummary ? `<div class="agent-status-callout neutral"><div class="agent-status-line"><strong>调度器最新判断</strong><div>${formatMultilineText(run.latestSummary)}</div></div></div>` : ''}
            </section>
        `;
    }

    function deriveRunRoundState(run, snapshot) {
        const surfaces = snapshot?.surfaces || [];
        const workerSurfaces = surfaces.filter(surface => !!surface.workerId);
        const workingWorkers = workerSurfaces.filter(surface => surface.status === 'running' || surface.status === 'queued');
        const restingWorkers = workerSurfaces.filter(surface => !workingWorkers.some(item => item.surfaceId === surface.surfaceId));
        const verificationSurface = surfaces.find(surface => surface.type === 'verification');
        const coordinatorSurface = surfaces.find(surface => surface.type === 'coordinator');
        const activeSurface = surfaces.find(surface => surface.surfaceId === run.activeSurfaceId)
            || workingWorkers[0]
            || coordinatorSurface
            || verificationSurface
            || workerSurfaces[0]
            || null;

        let phase = '休息态';
        let tone = 'neutral';

        if (workingWorkers.length > 0) {
            phase = '工作态';
            tone = 'running';
        } else if (verificationSurface && (verificationSurface.status === 'running' || verificationSurface.status === 'verifying')) {
            phase = '检查态';
            tone = 'warning';
        } else if (coordinatorSurface && coordinatorSurface.status === 'running') {
            phase = '调度态';
            tone = 'running';
        } else if (run.status === 'needs-human' || run.status === 'needs-attention') {
            phase = '待拍板';
            tone = 'warning';
        } else if (run.status === 'completed') {
            phase = '已完成';
            tone = 'success';
        }

        return {
            roundNumber: run.roundNumber || 0,
            phase,
            tone,
            activeSurface,
            workingWorkers,
            restingWorkers
        };
    }

    function renderRoleStateChips(items, tone) {
        const list = (items || []).filter(Boolean);
        if (!list.length) {
            return '<div class="agent-helper-text">暂无</div>';
        }

        return `
            <div class="run-role-chip-list">
                ${list.map(item => `
                    <span class="run-role-chip ${tone}">
                        ${AgentConsole.utils.escapeHtml(item.displayName || item.roleId || item.surfaceId || 'unknown')}
                        <small>${AgentConsole.utils.escapeHtml(formatStatusLabel(item.status || 'idle'))}</small>
                    </span>
                `).join('')}
            </div>
        `;
    }

    function renderAssistantExecutionBadges(surface) {
        if (!surface?.assistantAssignedRoundNumber && !surface?.assistantRoleMode && !surface?.assistantOutputKind) {
            return '';
        }

        const badges = [];

        if (surface.assistantAssignedRoundNumber) {
            badges.push(`<span class="agent-badge neutral">第 ${AgentConsole.utils.escapeHtml(String(surface.assistantAssignedRoundNumber))} 轮</span>`);
        }

        if (surface.assistantRoleMode === 'writer') {
            badges.push('<span class="agent-badge success">唯一 writer</span>');
        } else if (surface.assistantRoleMode === 'md-only') {
            badges.push('<span class="agent-badge warning">仅 Markdown</span>');
        }

        if (surface.assistantOutputKind) {
            badges.push(`<span class="agent-badge neutral">输出：${AgentConsole.utils.escapeHtml(surface.assistantOutputKind)}</span>`);
        }

        return badges.length
            ? `<div class="agent-meta-row">${badges.join('')}</div>`
            : '';
    }

    function renderAssistantArtifactChecklist(round, assistantArtifacts) {
        const items = (assistantArtifacts || []).filter(item => item.roundId === round.roundId || item.roundNumber === round.roundNumber);
        if (!items.length) {
            return '<div class="agent-helper-text">这轮还没有登记到交付物状态。</div>';
        }

        return `
            <div class="assistant-round-roles">
                ${items.map(item => `
                    <div class="assistant-role-row">
                        <div class="assistant-role-head">
                            <div class="assistant-role-head-main">
                                <strong>${AgentConsole.utils.escapeHtml(item.artifactName || '未命名工件')}</strong>
                                <span>${AgentConsole.utils.escapeHtml(item.roleName ? `${item.roleName} 输出` : '轮次交付物')}</span>
                            </div>
                            <div class="assistant-role-badges">
                                <span class="assistant-role-badge ${item.exists ? 'is-writer' : 'is-md-only'}">${item.exists ? '已生成' : '待生成'}</span>
                            </div>
                        </div>
                        <div class="assistant-role-body">
                            <div><strong>路径：</strong><code>${AgentConsole.utils.escapeHtml(item.artifactPath || item.artifactName || '—')}</code></div>
                        </div>
                    </div>
                `).join('')}
            </div>
        `;
    }

    function renderAssistantRoundTimeline(plan, assistantArtifacts, activeRoundNumber, compact = false) {
        const rounds = Array.isArray(plan?.rounds) ? [...plan.rounds].sort((a, b) => (a.roundNumber || 0) - (b.roundNumber || 0)) : [];
        if (!rounds.length) {
            return '';
        }

        return `
            <section class="settings-section">
                <div class="section-title-row"><h3>${compact ? '方案轮次与交付物' : '后续轮次与交付物状态'}</h3></div>
                <div class="agent-helper-text">这里直接展示当前轮、后续轮，以及每轮交付物到底只是计划中，还是已经真的落盘。</div>
                <div class="assistant-round-grid">
                    ${rounds.map(round => {
                        const isActive = activeRoundNumber === round.roundNumber;
                        const isUpcoming = activeRoundNumber && round.roundNumber > activeRoundNumber;
                        return `
                            <article class="assistant-round-card">
                                <div class="assistant-round-card-header">
                                    <div>
                                        <div class="agent-list-item-title">第 ${AgentConsole.utils.escapeHtml(String(round.roundNumber || '?'))} 轮 · ${AgentConsole.utils.escapeHtml(round.title || '未命名轮次')}</div>
                                        <div class="agent-meta-row">
                                            <span class="agent-badge ${isActive ? 'running' : isUpcoming ? 'neutral' : 'success'}">${isActive ? '当前轮' : isUpcoming ? '后续轮' : '已过轮'}</span>
                                            <span>${AgentConsole.utils.escapeHtml(round.executionMode || 'sequential')}</span>
                                        </div>
                                    </div>
                                </div>
                                <div class="assistant-round-block">
                                    <div><strong>目标：</strong>${AgentConsole.utils.escapeHtml(round.objective || '—')}</div>
                                    <div><strong>完成条件：</strong>${AgentConsole.utils.escapeHtml(round.completionCriteria || '—')}</div>
                                    <div><strong>交接：</strong>${AgentConsole.utils.escapeHtml(round.handoffNotes || '—')}</div>
                                    <div><strong>计划交付物：</strong>${AgentConsole.utils.escapeHtml((round.deliverables || []).join('，') || '—')}</div>
                                </div>
                                ${renderAssistantArtifactChecklist(round, assistantArtifacts)}
                            </article>
                        `;
                    }).join('')}
                </div>
            </section>
        `;
    }

    function renderRoundStatusPanel(run, snapshot) {
        const state = deriveRunRoundState(run, snapshot);
        const documentPath = run.roundHistoryDocumentPath || '将在首轮调度后创建';
        const assistantRoundTitle = run.assistantActiveRoundTitle || '未绑定 AI 助手轮次';
        const assistantObjective = run.assistantActiveRoundObjective || '当前轮次还没有额外目标说明。';
        const assistantPlan = snapshot?.assistantPlan || null;
        const assistantArtifacts = Array.isArray(snapshot?.assistantArtifacts) ? snapshot.assistantArtifacts : [];
        const writerSurface = (snapshot?.surfaces || []).find(surface => surface.workerId && surface.workerId === run.assistantActiveWriterWorkerId);
        const writerLabel = writerSurface?.displayName || run.assistantActiveWriterRoleId || '尚未指定';

        return `
            <section class="run-round-overview run-round-overview-inline">
                <div class="run-round-header">
                    <div>
                        <h3>轮次与角色状态</h3>
                        <div class="agent-helper-text">当前轮次、工作态与角色分工都收在这里。</div>
                    </div>
                    <div class="run-round-pills">
                        <span class="agent-badge ${state.tone}">${AgentConsole.utils.escapeHtml(state.phase)}</span>
                        <span class="agent-badge neutral">${AgentConsole.utils.escapeHtml(state.roundNumber > 0 ? `第 ${state.roundNumber} 轮` : '尚未进入轮次')}</span>
                    </div>
                </div>
                <div class="run-round-meta-grid">
                    <div class="run-round-meta-card">
                        <strong>当前聚焦</strong>
                        <div>${AgentConsole.utils.escapeHtml(state.activeSurface?.displayName || '暂无')}</div>
                    </div>
                    <div class="run-round-meta-card">
                        <strong>AI 当前轮</strong>
                        <div>${AgentConsole.utils.escapeHtml(run.assistantActiveRoundNumber ? `第 ${run.assistantActiveRoundNumber} 轮 · ${assistantRoundTitle}` : assistantRoundTitle)}</div>
                    </div>
                    <div class="run-round-meta-card">
                        <strong>轮次文档</strong>
                        <div><code>${AgentConsole.utils.escapeHtml(documentPath)}</code></div>
                    </div>
                    <div class="run-round-meta-card">
                        <strong>本轮唯一 writer</strong>
                        <div>${AgentConsole.utils.escapeHtml(writerLabel)}</div>
                    </div>
                </div>
                ${run.assistantActiveRoundNumber ? `
                    <div class="agent-status-callout neutral">
                        <div class="agent-status-line">
                            <strong>本轮目标</strong>
                            <div>${AgentConsole.utils.escapeHtml(assistantObjective)}</div>
                        </div>
                    </div>
                ` : ''}
                <div class="run-role-state-grid">
                    <div class="run-role-state-group">
                        <div class="run-role-state-title">谁在工作</div>
                        ${renderRoleStateChips(state.workingWorkers, 'working')}
                    </div>
                    <div class="run-role-state-group">
                        <div class="run-role-state-title">谁在休息</div>
                        ${renderRoleStateChips(state.restingWorkers, 'resting')}
                    </div>
                </div>
            </section>
            ${assistantPlan ? renderAssistantRoundTimeline(assistantPlan, assistantArtifacts, run.assistantActiveRoundNumber || 0, false) : ''}
        `;
    }

    function renderRoundHistoryTab(snapshot) {
        const run = snapshot.run || {};
        const content = String(snapshot.roundHistoryContent || '').trim();
        const documentPath = run.roundHistoryDocumentPath || '将在首轮调度后创建';

        return `
            <div class="settings-section">
                <div class="section-title-row">
                    <h3>轮次记录</h3>
                </div>
                <div class="agent-helper-text">文档位置：<code>${AgentConsole.utils.escapeHtml(documentPath)}</code></div>
                ${content
                    ? `<pre class="agent-log-pre json-block round-history-block">${AgentConsole.utils.escapeHtml(content)}</pre>`
                    : '<div class="workspace-empty-state">这条 run 还没有轮次记录内容。等进入问调度器或自动推进后，这里就会自动出现。</div>'}
            </div>
        `;
    }

    function renderHumanActionPanel(run, snapshot) {
        const unresolvedAttention = (snapshot.attention || []).filter(item => !item.isResolved);
        const focusSurface = snapshot.surfaces?.find(surface => surface.surfaceId === run.focusSuggestionSurfaceId)
            || snapshot.surfaces?.find(surface => {
                const reported = String(surface.lastReportedStatus || '').trim().toLowerCase();
                return reported === 'needs-human' || reported === 'blocked' || reported === 'needs-attention';
            })
            || null;

        if (!(run.status === 'needs-human' || run.status === 'needs-attention' || unresolvedAttention.length)) {
            return '';
        }

        const topAttention = unresolvedAttention[0] || null;
        const actionItems = [
            focusSurface ? `先点“聚焦查看”，把注意力拉到 ${focusSurface.displayName || formatSurfaceType(focusSurface.type)}。` : '先看最近一条提醒或构建检查结果，确认系统卡在哪。',
            focusSurface?.lastNextStep
                ? `如果你认可当前建议，优先按这条“下一步”处理：${focusSurface.lastNextStep}`
                : '如果你只是想让系统自己继续推进，可以补一句限制后点“自动推进”。',
            '如果你不同意当前路线，点“问调度器”，直接补充你的约束或新判断。'
        ];

        if (!AgentConsole.state.settings?.defaultVerificationCommand?.trim()) {
            actionItems.push('如果只是卡在补充构建/检查，可以先去“调度设置”填一个默认命令。');
        }

        return `
            <section class="agent-human-panel">
                <div class="agent-human-panel-title">现在需要你怎么配合</div>
                <div class="agent-human-panel-body">
                    ${topAttention ? `<div class="agent-human-panel-summary"><strong>当前最值得先看：</strong>${AgentConsole.utils.escapeHtml(topAttention.title || topAttention.kind)} · ${AgentConsole.utils.escapeHtml(topAttention.message || '')}</div>` : ''}
                    ${focusSurface?.lastSummary ? `<div class="agent-human-panel-summary"><strong>角色当前说明：</strong>${AgentConsole.utils.escapeHtml(focusSurface.lastSummary)}</div>` : ''}
                    <ol>
                        ${actionItems.map(item => `<li>${AgentConsole.utils.escapeHtml(item)}</li>`).join('')}
                    </ol>
                </div>
            </section>
        `;
    }

    function actionTitle(hints, key, fallback) {
        return AgentConsole.utils.escapeHtml(hints[key] || fallback || '');
    }

    function renderRoleOptions() {
        const container = document.getElementById('runRoleOptions');
        if (!container) {
            return;
        }

        if (!AgentConsole.state.roles.length) {
            container.innerHTML = '<div class="workspace-empty-inline">请先去“角色库”创建角色，协作运行才能知道派谁上场。</div>';
            renderRoleProposalPanel();
            return;
        }

        container.innerHTML = AgentConsole.state.roles.map((role, index) => `
            <label class="agent-checkbox-row">
                <input type="checkbox" value="${AgentConsole.utils.escapeHtml(role.roleId)}" ${index < 2 ? 'checked' : ''}>
                <span>${AgentConsole.utils.escapeHtml(role.icon || '🎭')} ${AgentConsole.utils.escapeHtml(role.name || role.roleId)}</span>
            </label>
        `).join('');

        renderRoleProposalPanel();
        syncRunWorkspaceRootHint();
    }

    function syncRunWorkspaceRootHint() {
        const input = document.getElementById('runWorkspaceRootInput');
        const hint = document.getElementById('runWorkspaceRootResolvedHint');
        if (!input || !hint) {
            return;
        }

        const configuredDefault = AgentConsole.state.settings?.defaultWorkspaceRoot?.trim() || '';
        const manualValue = input.value.trim();

        input.placeholder = configuredDefault
            ? `留空则使用默认根目录：${configuredDefault}`
            : defaultWorkspaceInputPlaceholder;

        if (manualValue) {
            hint.textContent = `当前将使用手动工作区根目录：${manualValue}`;
            return;
        }

        if (configuredDefault) {
            hint.textContent = `当前默认根目录：${configuredDefault}；留空创建 Run 时会从这里启动，并在其下自动创建任务目录。`;
            return;
        }

        hint.textContent = '当前默认根目录：未设置；留空时将退回到应用 EXE 根目录。';
    }

    function renderRoleProposalPanel() {
        const container = document.getElementById('runRoleProposalPanel');
        if (!container) {
            return;
        }

        const proposal = AgentConsole.state.roleProposal;
        if (!proposal) {
            container.innerHTML = '';
            return;
        }

        const existingRoles = Array.isArray(proposal.existingRoles) ? proposal.existingRoles : [];
        const newRoles = Array.isArray(proposal.newRoles) ? proposal.newRoles : [];

        container.innerHTML = `
            <section class="agent-proposal-card" data-role-proposal-root>
                <div class="section-title-row">
                    <h3>AI 角色建议</h3>
                    <span class="agent-badge neutral">${AgentConsole.utils.escapeHtml(proposal.recommendedWorkspaceName || 'task')}</span>
                </div>
                <div class="agent-helper-text">AI 会优先复用现有角色，并尽量让执行角色自行完成构建/测试；只有确有必要时，才建议补一个专门的构建检查角色。</div>
                ${proposal.summary ? '<div class="agent-helper-text agent-proposal-summary" data-proposal-summary></div>' : ''}
                ${existingRoles.length ? `
                    <div class="agent-label">建议复用的现有角色</div>
                    <div class="agent-checklist">
                        ${existingRoles.map((item, index) => `
                            <label class="agent-checkbox-row agent-checkbox-stack">
                                <input type="checkbox" data-proposal-existing-index="${index}" ${item.selected ? 'checked' : ''}>
                                <span>
                                    <strong>${AgentConsole.utils.escapeHtml(item.name || item.roleId)}</strong>
                                    <small>${AgentConsole.utils.escapeHtml(item.reason || item.description || '')}</small>
                                </span>
                            </label>
                        `).join('')}
                    </div>
                ` : ''}
                ${newRoles.length ? `
                    <div class="agent-label">建议新增并可持久化的角色</div>
                    <div class="agent-proposal-drafts">
                        ${newRoles.map((draft, index) => renderDraftRoleEditor(draft, index)).join('')}
                    </div>
                ` : '<div class="agent-helper-text">这次没有建议新增角色，说明现有角色池大概率已经扛得住。</div>'}
            </section>
        `;

        const summaryElement = container.querySelector('[data-proposal-summary]');
        if (summaryElement) {
            Runs.animateText(summaryElement, proposal.summary || '', 14);
        }
    }

    function renderDraftRoleEditor(draft, index) {
        const role = draft.role || {};
        return `
            <article class="agent-proposal-draft" data-proposal-draft-index="${index}">
                <label class="agent-checkbox-row agent-checkbox-stack">
                    <input type="checkbox" data-proposal-draft-selected ${draft.selected ? 'checked' : ''}>
                    <span>
                        <strong>${AgentConsole.utils.escapeHtml(role.name || role.roleId || `Draft ${index + 1}`)}</strong>
                        <small>${AgentConsole.utils.escapeHtml(draft.reason || role.description || '')}</small>
                    </span>
                </label>
                <div class="settings-grid settings-grid-2 compact-grid">
                    <div>
                        <label class="agent-label">角色 ID</label>
                        <input class="agent-input" data-proposal-field="roleId" type="text" value="${AgentConsole.utils.escapeHtml(role.roleId || '')}">
                    </div>
                    <div>
                        <label class="agent-label">显示名称</label>
                        <input class="agent-input" data-proposal-field="name" type="text" value="${AgentConsole.utils.escapeHtml(role.name || '')}">
                    </div>
                </div>
                <label class="agent-label">职责说明</label>
                <textarea class="agent-textarea" data-proposal-field="description">${AgentConsole.utils.escapeHtml(role.description || '')}</textarea>
                <label class="agent-label">工作目录</label>
                <input class="agent-input" data-proposal-field="workspacePath" type="text" value="${AgentConsole.utils.escapeHtml(role.workspacePath || '.')}">
                <label class="agent-label">提示词模板</label>
                <textarea class="agent-textarea textarea-code" data-proposal-field="promptTemplate">${AgentConsole.utils.escapeHtml(role.promptTemplate || '')}</textarea>
            </article>
        `;
    }

    function persistRoleProposalEditor() {
        const root = document.querySelector('[data-role-proposal-root]');
        const proposal = AgentConsole.state.roleProposal;
        if (!root || !proposal) {
            return;
        }

        proposal.existingRoles = (proposal.existingRoles || []).map((item, index) => ({
            ...item,
            selected: !!root.querySelector(`[data-proposal-existing-index="${index}"]`)?.checked
        }));

        proposal.newRoles = (proposal.newRoles || []).map((draft, index) => {
            const card = root.querySelector(`[data-proposal-draft-index="${index}"]`);
            if (!card) {
                return draft;
            }

            return {
                ...draft,
                selected: !!card.querySelector('[data-proposal-draft-selected]')?.checked,
                role: {
                    ...(draft.role || {}),
                    roleId: card.querySelector('[data-proposal-field="roleId"]')?.value?.trim() || draft.role?.roleId || '',
                    name: card.querySelector('[data-proposal-field="name"]')?.value?.trim() || draft.role?.name || '',
                    description: card.querySelector('[data-proposal-field="description"]')?.value?.trim() || draft.role?.description || null,
                    workspacePath: card.querySelector('[data-proposal-field="workspacePath"]')?.value?.trim() || draft.role?.workspacePath || '.',
                    promptTemplate: card.querySelector('[data-proposal-field="promptTemplate"]')?.value || draft.role?.promptTemplate || ''
                }
            };
        });
    }

    function renderRunsList() {
        const container = document.getElementById('runsList');
        if (!container) {
            return;
        }

        if (!AgentConsole.state.runs.length) {
            container.innerHTML = '<div class="workspace-empty-inline">还没有协作运行。先在左侧填好目标，再点“创建并启动”，让调度器正式开工。</div>';
            return;
        }

        container.innerHTML = AgentConsole.state.runs.map(run => {
            const snapshot = AgentConsole.state.snapshots.get(run.runId);
            const summary = snapshot?.summary;
            const unreadAttention = summary?.unreadAttention || 0;
            const active = run.runId === AgentConsole.state.selectedRunId;
            const badgeTone = AgentConsole.utils.statusTone(run.status);
            return `
                <button class="agent-list-item ${active ? 'active' : ''}" type="button" data-run-select="${AgentConsole.utils.escapeHtml(run.runId)}">
                    <div class="agent-list-item-title">${AgentConsole.utils.escapeHtml(run.title || '未命名协作运行')}</div>
                    <div class="agent-meta-row">
                        <span class="agent-badge ${badgeTone}">${AgentConsole.utils.escapeHtml(formatStatusLabel(run.status || 'draft'))}</span>
                        <span>${AgentConsole.utils.escapeHtml(AgentConsole.utils.formatRelative(run.updatedAt || run.createdAt))}</span>
                    </div>
                    <div class="agent-list-item-subtitle">${AgentConsole.utils.escapeHtml(AgentConsole.utils.trimText(run.goal || '无目标描述', 100))}</div>
                    <div class="run-mini-metrics">
                        <span>角色 ${run.workers?.length || 0}</span>
                        <span>未读提醒 ${unreadAttention}</span>
                        <span>自动推进 ${run.autoStepCount || 0}/${run.maxAutoSteps || 0}</span>
                    </div>
                </button>
            `;
        }).join('');
    }

    async function selectRun(runId, options = {}) {
        AgentConsole.state.selectedRunId = runId;
        if (!options.keepTab) {
            AgentConsole.state.activeRunDetailTab = AgentConsole.state.settings?.defaultRunDetailTab || 'workspace';
        }
        renderRunsList();
        await refreshSelectedRun({ silent: options.silent });
    }

    async function refreshSelectedRun(options = {}) {
        const runId = AgentConsole.state.selectedRunId;
        if (!runId) {
            renderSelectedRun();
            return null;
        }

        try {
            const snapshot = await AgentConsole.api.getRunSnapshot(runId);
            AgentConsole.storeSnapshot(snapshot);
            renderRunsList();
            renderSelectedRun();
            return snapshot;
        } catch (error) {
            console.error(error);
            if (!options.silent) {
                AgentConsole.utils.showToast(error.message || '刷新 Run 失败', 'error');
            }
            renderSelectedRun();
            return null;
        }
    }

    function renderSelectedRun() {
        const container = document.getElementById('runDetail');
        if (!container) {
            return;
        }

        const run = Runs.getSelectedRun();
        const snapshot = Runs.getSelectedSnapshot();
        if (!run) {
            Runs.renderEmbeddedTerminals([]);
            container.innerHTML = '<div class="workspace-empty-state">先从左侧选择一条协作运行，或者新建一个任务，让角色们开始协同工作。</div>';
            return;
        }

        const outputPreviews = [];

        const effectiveSnapshot = mergeLiveCoordinatorSurface(snapshot || {
            run,
            lanes: [],
            surfaces: [],
            attention: [],
            decisions: run.decisions || [],
            verifications: run.verificationHistory || (run.lastVerification ? [run.lastVerification] : []),
            summary: {
                runningSurfaces: 0,
                queuedSurfaces: 0,
                completedSurfaces: 0,
                failedSurfaces: 0,
                needsAttention: 0,
                unreadAttention: 0,
                resolvedAttention: 0
            }
        });

        const activeTab = AgentConsole.state.activeRunDetailTab || AgentConsole.state.settings?.defaultRunDetailTab || 'workspace';
        AgentConsole.state.activeRunDetailTab = activeTab;

        container.innerHTML = `
            <div class="agent-detail-shell">
                <div class="agent-hero-card">
                    <div>
                        <div class="agent-eyebrow">协作工作区</div>
                        <h2>${AgentConsole.utils.escapeHtml(run.title || '未命名协作运行')}</h2>
                        <p>${AgentConsole.utils.escapeHtml(run.goal || '未填写目标')}</p>
                        <div class="agent-helper-text">工作区：${AgentConsole.utils.escapeHtml(run.workspaceRoot || '.')}</div>
                        <div class="agent-helper-text">执行根：${AgentConsole.utils.escapeHtml(run.executionRoot || run.workspaceRoot || '.')} · 目录名：${AgentConsole.utils.escapeHtml(run.workspaceName || '(manual-root)')} · 模式：${run.usesManualWorkspaceRoot ? '手动边界' : '自动初始化'}</div>
                    </div>
                    <div class="agent-hero-actions">
                        <button class="editor-btn-primary" type="button" data-run-action="auto-step" title="${actionTitle(runActionHints, 'auto-step')}">自动推进</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="open-folder" title="${actionTitle(runActionHints, 'open-folder')}">打开项目目录</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="ask-supervisor" title="${actionTitle(runActionHints, 'ask-supervisor')}">问调度器</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="verify" title="${actionTitle(runActionHints, 'verify')}">构建/检查</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="toggle-autopilot" title="${actionTitle(runActionHints, 'toggle-autopilot')}">${run.autoPilotEnabled ? '关闭自动推进' : '开启自动推进'}</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="export-md" title="${actionTitle(runActionHints, 'export-md')}">导出摘要 MD</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="pause" title="${actionTitle(runActionHints, 'pause')}">暂停</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="resume" title="${actionTitle(runActionHints, 'resume')}">恢复</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="complete" title="${actionTitle(runActionHints, 'complete')}">标记完成</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="archive" title="${actionTitle(runActionHints, 'archive')}">归档</button>
                        <button class="editor-btn-secondary" type="button" data-run-action="refresh" title="${actionTitle(runActionHints, 'refresh')}">刷新</button>
                    </div>
                </div>

                ${renderAllowedDirectoriesBlock('Run 级额外放行目录', run.additionalAllowedDirectories, '这些目录来自目标/提示词中的显式路径，会并入后续 CLI 的 `--add-dir`。')}
                ${renderAssistantPlanPanel(run, effectiveSnapshot)}

                ${renderRunGuidance(run, effectiveSnapshot)}
                ${renderHumanActionPanel(run, effectiveSnapshot)}
                ${renderRunProgressOverview(run, effectiveSnapshot)}

                <div class="run-summary-grid">
                    ${renderSummaryCard('运行中', effectiveSnapshot.summary.runningSurfaces)}
                    ${renderSummaryCard('排队中', effectiveSnapshot.summary.queuedSurfaces)}
                    ${renderSummaryCard('已完成', effectiveSnapshot.summary.completedSurfaces)}
                    ${renderSummaryCard('失败', effectiveSnapshot.summary.failedSurfaces)}
                    ${renderSummaryCard('待处理提醒', effectiveSnapshot.summary.needsAttention)}
                    ${renderSummaryCard('未读提醒', effectiveSnapshot.summary.unreadAttention)}
                </div>

                <div class="settings-tabs">
                    ${Runs.runTabs.map(tab => {
                        const extra = tab.id === 'attention' && effectiveSnapshot.summary.unreadAttention > 0
                            ? ` <span class="inline-badge">${effectiveSnapshot.summary.unreadAttention}</span>`
                            : '';
                        return `<button class="settings-tab ${tab.id === activeTab ? 'active' : ''}" type="button" data-run-tab="${tab.id}">${tab.label}${extra}</button>`;
                    }).join('')}
                </div>

                ${renderRunTab(effectiveSnapshot, activeTab, outputPreviews)}
            </div>
        `;

        Runs.renderEmbeddedTerminals(outputPreviews);
    }

    function renderRunGuidance(run, snapshot) {
        const needsAttention = (snapshot.summary?.needsAttention || 0) > 0;
        const unreadAttention = (snapshot.summary?.unreadAttention || 0) > 0;
        const hasVerificationCommand = !!AgentConsole.state.settings?.defaultVerificationCommand?.trim();
        const items = [];
        let tone = 'info';
        let title = '';

        if (run.status === 'needs-human') {
            tone = 'warning';
            title = '当前需要你拍一下板，系统在等一个小裁决';
            if (needsAttention || unreadAttention) {
                items.push('先打开“提醒事项”，看看调度台或构建检查抛出的提示。');
            }
            items.push('如果你想先听系统建议，点“问调度器”。');
            items.push('如果准备继续跑下一轮，可以补一句限定后点“自动推进”。');
            if (!hasVerificationCommand) {
                items.push('当前还没配置默认构建/检查命令；可以去“调度设置”配置，或点“构建/检查”时临时输入。');
            }
        } else if (run.status === 'review') {
            tone = 'success';
            title = '当前已进入复核阶段，离收尾不远了';
            items.push('优先检查各个面板的总结与下一步，确认是不是只差一次兜底检查。');
            items.push('如果代码已基本完成，下一步通常是补做一次构建/测试检查。');
            items.push('检查通过后可标记完成；如果还有疑问，再问一次调度器。');
        } else if (run.status === 'draft') {
            tone = 'info';
            title = '协作运行刚创建好，马上可以开始调度';
            items.push('点击“自动推进”让调度器自动安排下一轮动作。');
            items.push('如果你想先看建议，也可以点“问调度器”。');
        }

        if (!items.length) {
            return '';
        }

        return `
            <section class="agent-guidance-banner ${tone}">
                <div class="agent-guidance-title">${AgentConsole.utils.escapeHtml(title)}</div>
                <ul>
                    ${items.map(item => `<li>${AgentConsole.utils.escapeHtml(item)}</li>`).join('')}
                </ul>
            </section>
        `;
    }

    function renderAssistantPlanPanel(run, snapshot) {
        const assistantPlan = snapshot?.assistantPlan || null;
        const assistantArtifacts = Array.isArray(snapshot?.assistantArtifacts) ? snapshot.assistantArtifacts : [];
        if (!run.assistantPlanId && !run.assistantPlanSummary && !run.assistantSkillSummary && !assistantPlan) {
            return '';
        }

        return `
            <section class="settings-section">
                <div class="section-title-row"><h3>AI 助手方案</h3></div>
                <section class="agent-status-callout neutral">
                    <div class="agent-status-line">
                        <strong>AI 助手已接管轮次设计</strong>
                        <div>${AgentConsole.utils.escapeHtml(run.assistantPlanSummary || assistantPlan?.summary || '当前 Run 绑定了一份 AI 助手方案，超管会优先遵守该方案滚动推进。')}</div>
                    </div>
                    <div class="surface-aux-list">
                        ${run.assistantPlanId ? `<div><strong>方案 ID：</strong><code>${AgentConsole.utils.escapeHtml(run.assistantPlanId)}</code></div>` : ''}
                        ${run.assistantPlanningBatchSize ? `<div><strong>批次策略：</strong>每批 ${AgentConsole.utils.escapeHtml(run.assistantPlanningBatchSize)} 轮，最多 ${AgentConsole.utils.escapeHtml(run.assistantMaxRounds || run.maxAutoSteps || 0)} 轮</div>` : ''}
                        <div><strong>推进模式：</strong>${run.assistantFullAuto ? '全自动，除硬阻断外默认由超管自己判断是否继续' : '半自动，建议先看方案后再推进'}</div>
                        ${run.assistantActiveRoundNumber ? `<div><strong>当前执行：</strong>第 ${AgentConsole.utils.escapeHtml(run.assistantActiveRoundNumber)} 轮 · ${AgentConsole.utils.escapeHtml(run.assistantActiveRoundTitle || '未命名轮次')}</div>` : ''}
                        ${run.assistantActiveWriterRoleId ? `<div><strong>本轮写入权：</strong>${AgentConsole.utils.escapeHtml(run.assistantActiveWriterRoleId)}</div>` : ''}
                        ${run.assistantActiveRoundObjective ? `<div><strong>本轮目标：</strong>${AgentConsole.utils.escapeHtml(run.assistantActiveRoundObjective)}</div>` : ''}
                        ${run.assistantSkillSummary ? `<div><strong>skill 摘要：</strong>${AgentConsole.utils.escapeHtml(run.assistantSkillSummary)}</div>` : ''}
                        ${run.assistantSkillFilePath ? `<div><strong>skill 文件：</strong><code>${AgentConsole.utils.escapeHtml(run.assistantSkillFilePath)}</code></div>` : ''}
                    </div>
                </section>
                ${assistantPlan ? renderAssistantRoundTimeline(assistantPlan, assistantArtifacts, run.assistantActiveRoundNumber || 0, true) : ''}
            </section>
        `;
    }

    function renderSummaryCard(label, value) {
        return `
            <div class="run-summary-card">
                <div class="run-summary-card-value">${value ?? 0}</div>
                <div class="run-summary-card-label">${AgentConsole.utils.escapeHtml(label)}</div>
            </div>
        `;
    }
    function renderRunTab(snapshot, activeTab, outputPreviews) {
        switch (activeTab) {
            case 'round':
                return renderRoundStatusPanel(snapshot.run || {}, snapshot);
            case 'attention':
                return renderAttentionTab(snapshot);
            case 'decisions':
                return renderDecisionsTab(snapshot);
            case 'verification':
                return renderVerificationTab(snapshot, outputPreviews);
            case 'round-history':
                return renderRoundHistoryTab(snapshot);
            case 'snapshot':
                return renderSnapshotTab(snapshot);
            case 'workspace':
            default:
                return renderWorkspaceTab(snapshot, outputPreviews);
        }
    }

    function renderWorkspaceTab(snapshot, outputPreviews) {
        if (!snapshot.surfaces?.length) {
            return '<div class="workspace-empty-state">这条协作运行暂时还没有面板，像个刚搭好的控制室，灯都还没亮。</div>';
        }

        const { activeSurface } = resolveActiveSurface(snapshot);
        if (!activeSurface) {
            return '<div class="workspace-empty-state">当前没有可展示的执行面板。</div>';
        }

        return `
            <div class="surface-workspace-shell">
                ${renderSurfaceTabs(snapshot)}
                <div class="surface-workspace-panel">
                    ${renderSurfaceCard(snapshot, activeSurface, outputPreviews)}
                </div>
            </div>
        `;
    }

    function renderSurfaceCard(snapshot, surface, outputPreviews) {
        const run = snapshot.run;
        const isActive = run.activeSurfaceId === surface.surfaceId;
        const isSuggested = run.focusSuggestionSurfaceId === surface.surfaceId && !isActive;
        const tone = AgentConsole.utils.statusTone(surface.status);
        const worker = run.workers?.find(item => item.workerId === surface.workerId);
        const icon = worker?.icon || (surface.type === 'verification' ? '🧪' : surface.type === 'coordinator' ? '🧭' : '🤖');
        const allowedDirectories = parseAllowedDirectoriesFromCommand(surface.commandPreview);

        const workerActions = surface.workerId ? `
            <div class="surface-card-actions">
                <button class="editor-btn-secondary" type="button" data-worker-action="start" data-worker-id="${AgentConsole.utils.escapeHtml(surface.workerId)}" title="${actionTitle(workerActionHints, 'start')}">启动</button>
                <button class="editor-btn-secondary" type="button" data-worker-action="continue" data-worker-id="${AgentConsole.utils.escapeHtml(surface.workerId)}" title="${actionTitle(workerActionHints, 'continue')}">继续</button>
                <button class="editor-btn-secondary" type="button" data-worker-action="stop" data-worker-id="${AgentConsole.utils.escapeHtml(surface.workerId)}" title="${actionTitle(workerActionHints, 'stop')}">停止</button>
            </div>
        ` : '';

        return `
            <article class="surface-card ${getSurfaceToneClass(surface)} ${isActive ? 'active' : ''} ${isSuggested ? 'suggested' : ''}">
                <div class="surface-card-header">
                    <div>
                        <div class="agent-list-item-title">${AgentConsole.utils.escapeHtml(icon)} ${AgentConsole.utils.escapeHtml(surface.displayName || formatSurfaceType(surface.type))}</div>
                        <div class="agent-meta-row">
                            <span class="agent-badge ${tone}">${AgentConsole.utils.escapeHtml(formatStatusLabel(surface.status || 'idle'))}</span>
                            <span>${AgentConsole.utils.escapeHtml(AgentConsole.utils.formatRelative(surface.updatedAt || surface.lastActivityAt))}</span>
                        </div>
                    </div>
                    <div class="surface-card-actions">
                        <button class="editor-btn-secondary" type="button" data-surface-action="focus" data-surface-id="${AgentConsole.utils.escapeHtml(surface.surfaceId)}" title="${actionTitle(surfaceActionHints, 'focus')}">聚焦查看</button>
                    </div>
                </div>

                <div class="surface-section">
                    <div class="agent-helper-text">${AgentConsole.utils.escapeHtml(surface.currentTask || surface.workspacePath || '没有附加任务说明')}</div>
                    ${renderAssistantExecutionBadges(surface)}
                    ${renderStructuredSurfaceSummary(surface)}
                </div>

                ${renderAllowedDirectoriesBlock('本次生效目录授权', allowedDirectories, '下面这些目录是从当前命令预览里解析出的 `--add-dir`。')}

                ${renderCommandPreviewBlock(surface.commandPreview, surface.workspacePath)}

                <div class="surface-aux-list">
                    ${surface.assistantAssignedRoundTitle ? `<div><strong>AI 轮次：</strong>第 ${AgentConsole.utils.escapeHtml(surface.assistantAssignedRoundNumber || '?')} 轮 · ${AgentConsole.utils.escapeHtml(surface.assistantAssignedRoundTitle)}</div>` : ''}
                    ${surface.assistantRoundObjective ? `<div><strong>轮次目标：</strong>${AgentConsole.utils.escapeHtml(surface.assistantRoundObjective)}</div>` : ''}
                    ${surface.assistantRoleMode ? `<div><strong>执行模式：</strong>${AgentConsole.utils.escapeHtml(surface.assistantRoleMode === 'writer' ? '唯一 writer，可写代码' : 'md-only，仅输出 Markdown')}</div>` : ''}
                    ${surface.lastReportedStatus ? `<div><strong>状态汇报：</strong>${AgentConsole.utils.escapeHtml(surface.lastReportedStatus)}</div>` : ''}
                    ${surface.lastNextStep ? `<div><strong>下一步：</strong>${AgentConsole.utils.escapeHtml(surface.lastNextStep)}</div>` : ''}
                    ${surface.workspacePath ? `<div><strong>工作目录：</strong><code>${AgentConsole.utils.escapeHtml(surface.workspacePath)}</code></div>` : ''}
                    ${surface.lastAttentionMessage ? `<div><strong>提醒：</strong>${AgentConsole.utils.escapeHtml(surface.lastAttentionMessage)}</div>` : ''}
                </div>

                ${renderRawOutputPanel('原始终端输出', surface.lastOutputPreview, `surface-${surface.surfaceId}`, outputPreviews)}
                ${workerActions}
            </article>
        `;
    }

    function renderAttentionTab(snapshot) {
        const items = snapshot.attention || [];
        return `
            <div class="stack-list">
                <div class="inline-actions">
                    <button class="editor-btn-secondary" type="button" data-run-action="ack-all-attention" title="${actionTitle(runActionHints, 'ack-all-attention')}">全部设为已读</button>
                    <div class="agent-helper-text">未解决提醒：${items.filter(item => !item.isResolved).length}</div>
                </div>
                ${items.length
                    ? items.map(item => `
                        <article class="attention-card ${item.isRead ? '' : 'unread'}">
                            <div class="surface-card-header">
                                <div>
                                    <div class="agent-list-item-title">${AgentConsole.utils.escapeHtml(item.title || item.kind)}</div>
                                    <div class="agent-meta-row">
                                        <span class="agent-badge ${AgentConsole.utils.attentionTone(item.level)}">${AgentConsole.utils.escapeHtml(item.level || 'neutral')}</span>
                                        <span>${AgentConsole.utils.escapeHtml(AgentConsole.utils.formatDateTime(item.createdAt))}</span>
                                    </div>
                                </div>
                                <div class="surface-card-actions">
                                    ${item.surfaceId ? `<button class="editor-btn-secondary" type="button" data-surface-action="focus" data-surface-id="${AgentConsole.utils.escapeHtml(item.surfaceId)}" title="${actionTitle(surfaceActionHints, 'focus')}">定位到面板</button>` : ''}
                                    ${item.isResolved ? '' : `<button class="editor-btn-secondary" type="button" data-attention-action="ack" data-attention-id="${AgentConsole.utils.escapeHtml(item.eventId)}" title="${actionTitle(attentionActionHints, 'ack')}">设为已读</button>`}
                                    ${item.isResolved ? '' : `<button class="editor-btn-secondary" type="button" data-attention-action="resolve" data-attention-id="${AgentConsole.utils.escapeHtml(item.eventId)}" title="${actionTitle(attentionActionHints, 'resolve')}">标记已处理</button>`}
                                </div>
                            </div>
                            <div class="decision-summary">${AgentConsole.utils.escapeHtml(item.message || '')}</div>
                        </article>
                    `).join('')
                    : '<div class="workspace-empty-state">当前没有需要处理的提醒事项。世界和平，至少这一页是。</div>'}
            </div>
        `;
    }

    function renderDecisionsTab(snapshot) {
        const decisions = snapshot.decisions || [];
        return `
            <div class="decision-list">
                ${decisions.length
                    ? decisions.map(item => `
                        <article class="decision-card">
                            <div class="agent-meta-row">
                                <span class="agent-badge neutral">${AgentConsole.utils.escapeHtml(item.kind || 'note')}</span>
                                <span>${AgentConsole.utils.escapeHtml(AgentConsole.utils.formatDateTime(item.createdAt))}</span>
                            </div>
                            <div class="decision-summary">${AgentConsole.utils.escapeHtml(item.summary || '')}</div>
                        </article>
                    `).join('')
                    : '<div class="workspace-empty-state">还没有调度决策记录。</div>'}
            </div>
        `;
    }

    function renderVerificationTab(snapshot, outputPreviews) {
        const records = snapshot.verifications || [];
        const defaultCommand = AgentConsole.state.settings?.defaultVerificationCommand || '';
        return `
            <div class="stack-list">
                <div class="inline-actions">
                    <button class="editor-btn-primary" type="button" data-run-action="verify" title="${actionTitle(runActionHints, 'verify')}">立即执行一次</button>
                    <div class="agent-helper-text">默认命令：${AgentConsole.utils.escapeHtml(defaultCommand || '未设置')}</div>
                </div>
                ${defaultCommand
                    ? ''
                    : '<div class="workspace-empty-inline">当前还没有默认构建/检查命令。你可以点上面的“立即执行一次”临时输入，也可以去“调度设置”里配置默认命令。</div>'}
                ${records.length
                    ? records.map(record => `
                        <article class="decision-card verification-card ${getVerificationState(record).tone}">
                            <div class="agent-meta-row">
                                <span class="agent-badge ${AgentConsole.utils.statusTone(record.passed ? 'passed' : record.status)}">${AgentConsole.utils.escapeHtml(formatStatusLabel(record.status || (record.passed ? 'passed' : 'idle')))}</span>
                                <span>${AgentConsole.utils.escapeHtml(AgentConsole.utils.formatDateTime(record.completedAt || record.startedAt))}</span>
                            </div>
                            <div class="agent-status-callout ${getVerificationState(record).tone}">
                                <div class="agent-status-line">
                                    <strong>${AgentConsole.utils.escapeHtml(getVerificationState(record).title)}</strong>
                                    <div>${AgentConsole.utils.escapeHtml(getVerificationState(record).detail)}</div>
                                </div>
                            </div>
                            ${record.command ? `<div><strong>执行命令：</strong><code>${AgentConsole.utils.escapeHtml(record.command)}</code></div>` : ''}
                            ${record.summary ? `<div class="decision-summary">${formatMultilineText(record.summary)}</div>` : ''}
                            ${renderRawOutputPanel('构建/检查终端输出', record.outputPreview, `verification-${record.verificationId}`, outputPreviews)}
                        </article>
                    `).join('')
                    : '<div class="workspace-empty-state">还没有构建/检查记录。</div>'}
            </div>
        `;
    }

    function renderSnapshotTab(snapshot) {
        return `
            <div class="settings-section">
                <div class="section-title-row"><h3>运行快照 JSON</h3></div>
                <pre class="agent-log-pre json-block">${AgentConsole.utils.escapeHtml(JSON.stringify(snapshot, null, 2))}</pre>
            </div>
        `;
    }

    Object.assign(Runs, {
        renderRoleOptions,
        renderRoleProposalPanel,
        persistRoleProposalEditor,
        syncRunWorkspaceRootHint,
        renderRunsList,
        selectRun,
        renderSelectedRun,
        refreshSelectedRun
    });
})();
