(function () {
    const AgentConsole = window.AgentConsole;
    const Runs = AgentConsole.Runs;

    function collectSelectedRoles() {
        Runs.persistRoleProposalEditor();

        const proposal = AgentConsole.state.roleProposal;
        if (proposal) {
            const selectedExisting = (proposal.existingRoles || []).filter(item => item.selected).map(item => item.roleId);
            const selectedDrafts = (proposal.newRoles || []).filter(item => item.selected && item.role?.roleId);
            if (selectedExisting.length || selectedDrafts.length) {
                return {
                    roleIds: [...selectedExisting, ...selectedDrafts.map(item => item.role.roleId)],
                    draftRoles: selectedDrafts,
                    workspaceName: proposal.recommendedWorkspaceName || null
                };
            }
        }

        return {
            roleIds: Array.from(document.querySelectorAll('#runRoleOptions input[type="checkbox"]:checked')).map(input => input.value),
            draftRoles: [],
            workspaceName: null
        };
    }

    async function persistSelectedDraftRoles(draftRoles) {
        if (!draftRoles.length) {
            return;
        }

        const mergedRoles = [...AgentConsole.state.roles];
        let changed = false;

        draftRoles.forEach(draft => {
            const role = draft.role;
            if (!role?.roleId) {
                return;
            }

            if (mergedRoles.some(item => item.roleId === role.roleId)) {
                return;
            }

            mergedRoles.push({
                roleId: role.roleId,
                name: role.name || role.roleId,
                description: role.description || null,
                icon: role.icon || '🧩',
                promptTemplate: role.promptTemplate || 'Project goal: {{goal}}. Role: {{roleName}}. Finish with STATUS / SUMMARY / NEXT.',
                model: role.model || AgentConsole.state.settings?.defaultModel || 'gpt-5.4',
                allowAllTools: role.allowAllTools ?? true,
                allowAllPaths: role.allowAllPaths ?? false,
                allowAllUrls: role.allowAllUrls ?? false,
                workspacePath: role.workspacePath || '.',
                allowedUrls: role.allowedUrls || [],
                allowedTools: role.allowedTools || [],
                deniedTools: role.deniedTools || [],
                allowedPaths: role.allowedPaths || [],
                environmentVariables: role.environmentVariables || {}
            });
            changed = true;
        });

        if (!changed) {
            return;
        }

        const savedCatalog = await AgentConsole.api.saveRoles({
            settings: AgentConsole.state.settings || {},
            roles: mergedRoles
        });

        AgentConsole.state.roleCatalog = savedCatalog;
        AgentConsole.state.roles = Array.isArray(savedCatalog.roles) ? savedCatalog.roles : [];
        AgentConsole.state.settings = savedCatalog.settings || AgentConsole.state.settings;
        AgentConsole.Roles?.renderList();
        AgentConsole.Roles?.renderEditor();
        Runs.renderRoleOptions();
    }

    function setInlineError(title, message) {
        Runs.setRunFeedback({
            tone: 'error',
            title,
            message,
            detail: '先把这一步补齐，再点一次就好。',
            animate: false
        });
    }

    function downloadTextFile(filename, content) {
        const blob = new Blob([content], { type: 'text/markdown;charset=utf-8' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    }

    function safeFileName(value) {
        return String(value || 'run-summary')
            .trim()
            .replace(/[\\/:*?"<>|]+/g, '-')
            .replace(/\s+/g, '-')
            .replace(/-+/g, '-')
            .replace(/^-+|-+$/g, '')
            .toLowerCase() || 'run-summary';
    }

    function buildRunSummaryMarkdown(run, snapshot) {
        const surfaces = snapshot?.surfaces || [];
        const workerSurfaces = surfaces.filter(surface => !!surface.workerId);
        const attention = (snapshot?.attention || []).filter(item => !item.isResolved);
        const decisions = snapshot?.decisions || [];
        const verifications = snapshot?.verifications || [];
        const latestVerification = verifications[0] || run.lastVerification || null;

        const lines = [
            `# ${run.title || '未命名协作运行'}`,
            '',
            `- 目标：${run.goal || '未填写'}`,
            `- 当前轮次：第 ${run.roundNumber || 0} 轮`,
            `- 当前状态：${run.status || 'unknown'}`,
            `- 工作区：${run.workspaceRoot || '.'}`,
            `- 轮次记录文档：${run.roundHistoryDocumentPath || '将在进入首轮调度后创建'}`,
            `- 自动推进：${run.autoPilotEnabled ? '开启' : '关闭'} (${run.autoStepCount || 0}/${run.maxAutoSteps || 0})`,
            `- 角色进度：已完成 ${workerSurfaces.filter(surface => surface.status === 'completed').length}/${Math.max(workerSurfaces.length, 1)}，运行中 ${workerSurfaces.filter(surface => surface.status === 'running' || surface.status === 'queued').length}，失败 ${workerSurfaces.filter(surface => surface.status === 'failed').length}`,
            ''
        ];

        if (run.latestSummary) {
            lines.push('## 调度器最新判断', '', run.latestSummary, '');
        }

        lines.push('## 角色面板');
        workerSurfaces.forEach(surface => {
            lines.push(
                '',
                `### ${surface.displayName || surface.roleId || surface.surfaceId}`,
                `- 状态：${surface.status || 'unknown'}`,
                `- 汇报：${surface.lastReportedStatus || '—'}`,
                `- 下一步：${surface.lastNextStep || '—'}`,
                `- 工作目录：${surface.workspacePath || '—'}`,
                '',
                surface.lastSummary || '暂无摘要。'
            );
        });

        lines.push('', '## 构建/检查');
        if (latestVerification) {
            lines.push(
                '',
                `- 状态：${latestVerification.status || 'unknown'}`,
                `- 命令：${latestVerification.command || '<none>'}`,
                `- 退出码：${latestVerification.exitCode ?? '—'}`,
                `- 说明：${latestVerification.summary || '—'}`
            );
        } else {
            lines.push('', '- 暂无构建/检查记录');
        }

        lines.push('', '## 待处理提醒');
        if (attention.length) {
            attention.forEach(item => {
                lines.push('', `- [${item.level || 'info'}] ${item.title || item.kind}: ${item.message || ''}`);
            });
        } else {
            lines.push('', '- 当前没有待处理提醒');
        }

        lines.push('', '## 最近调度决策');
        if (decisions.length) {
            decisions.slice(0, 10).forEach(item => {
                lines.push('', `- ${item.createdAt || ''} · ${item.kind || 'note'} · ${item.summary || ''}`);
            });
        } else {
            lines.push('', '- 暂无调度决策');
        }

        return lines.join('\n');
    }

    async function suggestRoles() {
        const goal = document.getElementById('runGoalInput')?.value.trim() || '';
        const workspaceRoot = document.getElementById('runWorkspaceRootInput')?.value.trim() || null;
        let firstByteNotified = false;

        if (!goal) {
            setInlineError('还差一个总目标', '请先填写 Run 总目标，再让调度者提建议。');
            AgentConsole.utils.showToast('请先填写 Run 总目标，再让调度者提建议', 'error');
            return;
        }

        Runs.setBusyAction('suggest');
        Runs.setRunFeedback({
            tone: 'running',
            title: 'AI 正在分析角色方案…',
            message: '请求已发出，正在检查现有角色池并推演最合适的分工。',
            detail: '通常几十秒内会回来；仓库越大、模型越忙，等待越久一点。',
            pending: true,
            phases: [
                '已发送请求，等待模型接单。',
                '如果 AI 已经开始回字，这里会立刻切换提示。',
                '正在结合目标与现有角色池做复用判断。',
                '正在整理建议的职责拆分与 workspace 名称。',
                '快好了，返回后会直接显示在下面的提案卡片里。'
            ]
        });

        try {
            AgentConsole.state.roleProposal = await AgentConsole.api.proposeRoles({ goal, workspaceRoot }, {
                onResponseStart: () => {
                    Runs.updateRunFeedback({
                        title: '服务端已开始响应…',
                        message: '请求已经有回音了，正在等待 AI 返回首字节。',
                        phases: [
                            '服务端已经开始响应，正在等 AI 的首字节。',
                            '如果很快切到“AI 已开始返回内容…”，就说明不是报错，而是在继续传输。'
                        ],
                        phaseIndex: 0
                    });
                },
                onFirstByte: () => {
                    if (firstByteNotified) {
                        return;
                    }

                    firstByteNotified = true;
                    Runs.updateRunFeedback({
                        title: 'AI 已开始返回内容…',
                        message: '已经收到 AI 返回的首字节，正在等待完整角色方案。',
                        detail: '这说明不是卡死也不是静默报错，而是在继续传完整结果。',
                        phases: [
                            '已收到首字节，正在拼接完整 JSON。',
                            '正在整理返回的角色建议与 workspace 名称。',
                            '快结束了，完整结果一到就会直接渲染到下方。'
                        ],
                        phaseIndex: 0
                    });
                }
            });
            Runs.renderRoleProposalPanel();

            const existingCount = AgentConsole.state.roleProposal?.existingRoles?.filter(item => item.selected).length || 0;
            const newCount = AgentConsole.state.roleProposal?.newRoles?.filter(item => item.selected).length || 0;
            const title = newCount > 0 ? 'AI 角色方案已生成' : 'AI 已完成角色复核';
            const detail = newCount > 0
                ? `建议复用 ${existingCount} 个角色，并补充 ${newCount} 个新角色草稿。AI 会优先让执行角色自行构建/测试。`
                : '这次没有新增角色，说明现有角色池已经能扛住这项任务。';

            Runs.setRunFeedback({
                tone: 'success',
                title,
                message: AgentConsole.state.roleProposal?.summary || detail,
                detail,
                animate: true
            });
            AgentConsole.utils.showToast(newCount > 0 ? '已生成结构化角色提案' : 'AI 已检查角色池，这次无需新增角色');
        } catch (error) {
            console.error(error);
            Runs.setRunFeedback({
                tone: 'error',
                title: '生成角色提案失败',
                message: error.message || '生成角色提案失败',
                detail: '你可以稍后重试，或直接手动勾选角色创建 Run。'
            });
            AgentConsole.utils.showToast(error.message || '生成角色提案失败', 'error');
        } finally {
            Runs.clearBusyAction('suggest');
        }
    }

    async function createRun() {
        const title = document.getElementById('runTitleInput')?.value.trim() || '';
        const goal = document.getElementById('runGoalInput')?.value.trim() || '';
        const workspaceRoot = document.getElementById('runWorkspaceRootInput')?.value.trim() || null;
        const autoPilotEnabled = !!document.getElementById('runAutopilotInput')?.checked;
        const maxAutoSteps = Number.parseInt(document.getElementById('runMaxAutoStepsInput')?.value || '0', 10) || 0;
        const selection = collectSelectedRoles();
        const roleIds = selection.roleIds;

        if (!goal) {
            setInlineError('Run 还没目标', '请先填写 Run 总目标。');
            AgentConsole.utils.showToast('请先填写 Run 总目标', 'error');
            return;
        }

        if (!roleIds.length) {
            setInlineError('还没有角色上场', '至少选择一个角色，系统才知道派谁去干活。');
            AgentConsole.utils.showToast('至少选择一个角色', 'error');
            return;
        }

        Runs.setBusyAction('create');
        Runs.setRunFeedback({
            tone: 'running',
            title: '正在创建并启动 Run…',
            message: '请求已提交，接下来会初始化工作区、落盘配置并启动所选角色。',
            detail: '创建完成后，左侧会出现新 Run，右侧状态会逐步刷新。',
            pending: true,
            phases: [
                '正在校验角色与工作区参数。',
                '正在创建 Run 记录并准备工作区。',
                '正在启动首批角色会话。',
                '即将返回，新的 Run 会出现在左侧列表。'
            ]
        });

        try {
            await persistSelectedDraftRoles(selection.draftRoles);
            const run = await AgentConsole.api.createRun({
                title,
                goal,
                workspaceRoot,
                workspaceName: workspaceRoot ? null : selection.workspaceName,
                roleIds,
                autoStart: true,
                autoPilotEnabled,
                maxAutoSteps
            });
            AgentConsole.upsertRun(run);
            AgentConsole.state.roleProposal = null;
            Runs.renderRoleProposalPanel();
            Runs.renderRunsList();
            await Runs.selectRun(run.runId, { keepTab: false, silent: true });

            const workerCount = run.workers?.length || roleIds.length;
            Runs.setRunFeedback({
                tone: 'success',
                title: 'Run 已创建并启动',
                message: `已创建「${run.title || '未命名协作运行'}」，当前准备调度 ${workerCount} 个角色。`,
                detail: `${run.autoPilotEnabled ? '已开启自动推进。' : '当前未开启自动推进。'} 你现在可以重点关注“提醒事项”、“构建/检查”和各个面板的总结/下一步。`,
                animate: true
            });
            AgentConsole.utils.showToast('Run 已创建');
        } catch (error) {
            console.error(error);
            Runs.setRunFeedback({
                tone: 'error',
                title: '创建 Run 失败',
                message: error.message || '创建 Run 失败',
                detail: '参数没问题的话，多半是后端初始化工作区或启动角色时出了岔子。'
            });
            AgentConsole.utils.showToast(error.message || '创建 Run 失败', 'error');
        } finally {
            Runs.clearBusyAction('create');
        }
    }

    async function handleRunAction(action) {
        const run = Runs.getSelectedRun();
        if (!run) {
            return;
        }

        try {
            if (action === 'refresh') {
                await Runs.refreshSelectedRun();
                return;
            }

            if (action === 'ask-supervisor') {
                AgentConsole.state.activeRunDetailTab = 'workspace';
                Runs.renderSelectedRun();
                const extraInstruction = prompt('补充一点偏好或限制（可留空）：', '') || '';
                Runs.setRunFeedback({
                    tone: 'running',
                    title: '调度器正在返回建议…',
                    message: '这次不会只在最后给结论；下方 Coordinator 面板会实时显示它正在返回的内容。',
                    detail: '你现在可以直接盯住工作台里的调度面板，不必傻等整段结束。',
                    pending: true,
                    phases: [
                        '请求已发出，正在等待调度器开始回字。',
                        '一旦收到首段内容，Coordinator 面板会实时刷新。',
                        '返回完成后，会自动沉淀为正式调度结论。'
                    ]
                });
                await AgentConsole.api.askSupervisor(run.runId, extraInstruction);
                Runs.setRunFeedback({
                    tone: 'success',
                    title: '调度器已收到问题',
                    message: '稍等一会儿，新的总结或调度建议会体现在“调度决策”或调度面板里。',
                    detail: '如果这轮任务很小，返回通常会比较快。'
                });
            } else if (action === 'auto-step') {
                AgentConsole.state.activeRunDetailTab = 'workspace';
                Runs.renderSelectedRun();
                const extraInstruction = prompt('给本轮自动推进加一点限制或偏好（可留空）：', '') || '';
                Runs.setRunFeedback({
                    tone: 'running',
                    title: '自动推进已启动…',
                    message: '这轮调度的实时返回会直接显示在下方 Coordinator 面板。',
                    detail: '如果本轮先做检查，再生成计划，你会先看到状态切换，再看到调度文本流。',
                    pending: true,
                    phases: [
                        '正在准备本轮自动推进。',
                        '若启用了先验证，会先跑一轮构建/检查。',
                        '随后 Coordinator 会开始实时返回调度计划。'
                    ]
                });
                await AgentConsole.api.autoStep(run.runId, { extraInstruction, runVerificationFirst: true });
                Runs.setRunFeedback({
                    tone: 'success',
                    title: '自动推进已触发',
                    message: '系统会先做必要的检查/规划，再决定要继续哪个角色。',
                    detail: '接下来留意“工作台”、“提醒事项”和“构建/检查”这三块就行。'
                });
            } else if (action === 'verify') {
                const defaultCommand = AgentConsole.state.settings?.defaultVerificationCommand || '';
                const command = prompt('输入构建/检查命令（留空使用默认值）：', defaultCommand) ?? defaultCommand;
                await AgentConsole.api.verifyRun(run.runId, command);
                Runs.setRunFeedback({
                    tone: 'success',
                    title: '构建/检查已触发',
                    message: command ? `正在执行命令：${command}` : '正在执行默认构建/检查命令。',
                    detail: '稍后看“构建/检查”页签就能知道结果。'
                });
            } else if (action === 'open-folder') {
                const result = await AgentConsole.api.openRunFolder(run.runId);
                Runs.setRunFeedback({
                    tone: 'success',
                    title: '已打开项目目录',
                    message: '已经帮你打开当前 Run 的项目目录。',
                    detail: result?.path ? `目录位置：${result.path}` : '如果资源管理器没弹到前台，可以看任务栏。'
                });
            } else if (action === 'export-md') {
                const snapshot = Runs.getSelectedSnapshot() || await Runs.refreshSelectedRun({ silent: true });
                const markdown = buildRunSummaryMarkdown(run, snapshot || { surfaces: [], attention: [], decisions: [], verifications: [] });
                downloadTextFile(`${safeFileName(run.title || run.runId)}-summary.md`, markdown);
                Runs.setRunFeedback({
                    tone: 'success',
                    title: 'Markdown 摘要已导出',
                    message: '已生成一份可读摘要，适合发给别人看、贴到 issue，或者自己留档。',
                    detail: '如果你想补更多细节，可以先刷新一次，再重新导出。'
                });
            } else if (action === 'toggle-autopilot') {
                await AgentConsole.api.setAutopilot(run.runId, !run.autoPilotEnabled);
            } else if (action === 'pause') {
                await AgentConsole.api.pauseRun(run.runId);
            } else if (action === 'resume') {
                await AgentConsole.api.resumeRun(run.runId);
            } else if (action === 'complete') {
                await AgentConsole.api.completeRun(run.runId);
            } else if (action === 'archive') {
                if (!confirm('确认归档这个 Run 吗？')) {
                    return;
                }
                await AgentConsole.api.archiveRun(run.runId);
            } else if (action === 'ack-all-attention') {
                const snapshot = await AgentConsole.api.acknowledgeAllAttention(run.runId);
                AgentConsole.storeSnapshot(snapshot);
            }

            await Runs.refreshSelectedRun({ silent: true });
        } catch (error) {
            console.error(error);
            AgentConsole.utils.showToast(error.message || '执行 Run 动作失败', 'error');
        }
    }

    async function handleWorkerAction(action, workerId) {
        const run = Runs.getSelectedRun();
        if (!run || !workerId) {
            return;
        }

        try {
            if (action === 'start') {
                const promptValue = prompt('可选：给这个角色一条启动说明。留空则使用角色模板。', '') || '';
                await AgentConsole.api.startWorker(run.runId, workerId, promptValue);
            } else if (action === 'continue') {
                const promptValue = prompt('可选：补一句继续说明。留空则使用默认继续提示。', '') || '';
                await AgentConsole.api.continueWorker(run.runId, workerId, promptValue);
            } else if (action === 'stop') {
                await AgentConsole.api.stopWorker(run.runId, workerId);
            }

            await Runs.refreshSelectedRun({ silent: true });
        } catch (error) {
            console.error(error);
            AgentConsole.utils.showToast(error.message || '执行 Worker 动作失败', 'error');
        }
    }

    async function handleSurfaceAction(action, surfaceId) {
        const run = Runs.getSelectedRun();
        if (!run || !surfaceId) {
            return;
        }

        try {
            if (action === 'focus') {
                const snapshot = await AgentConsole.api.focusSurface(run.runId, surfaceId, true);
                AgentConsole.storeSnapshot(snapshot);
                Runs.renderSelectedRun();
                Runs.renderRunsList();
            }
        } catch (error) {
            console.error(error);
            AgentConsole.utils.showToast(error.message || '执行 Surface 动作失败', 'error');
        }
    }

    async function handleAttentionAction(action, attentionId) {
        const run = Runs.getSelectedRun();
        if (!run || !attentionId) {
            return;
        }

        try {
            let snapshot = null;
            if (action === 'ack') {
                snapshot = await AgentConsole.api.acknowledgeAttention(run.runId, attentionId);
            } else if (action === 'resolve') {
                snapshot = await AgentConsole.api.resolveAttention(run.runId, attentionId);
            }

            if (snapshot) {
                AgentConsole.storeSnapshot(snapshot);
                Runs.renderSelectedRun();
                Runs.renderRunsList();
            }
        } catch (error) {
            console.error(error);
            AgentConsole.utils.showToast(error.message || '执行 Attention 动作失败', 'error');
        }
    }

    function bindStaticEvents() {
        document.getElementById('btnCreateRun')?.addEventListener('click', createRun);
        document.getElementById('btnSuggestRoles')?.addEventListener('click', suggestRoles);
        document.getElementById('runWorkspaceRootInput')?.addEventListener('input', () => Runs.syncRunWorkspaceRootHint?.());

        document.getElementById('runsList')?.addEventListener('click', event => {
            const runId = event.target.closest('[data-run-select]')?.getAttribute('data-run-select');
            if (runId) {
                Runs.selectRun(runId, { keepTab: true });
            }
        });

        document.getElementById('runDetail')?.addEventListener('click', event => {
            const runTab = event.target.closest('[data-run-tab]')?.getAttribute('data-run-tab');
            if (runTab) {
                AgentConsole.state.activeRunDetailTab = runTab;
                Runs.renderSelectedRun();
                return;
            }

            const runAction = event.target.closest('[data-run-action]')?.getAttribute('data-run-action');
            if (runAction) {
                handleRunAction(runAction);
                return;
            }

            const workerButton = event.target.closest('[data-worker-action]');
            if (workerButton) {
                handleWorkerAction(workerButton.getAttribute('data-worker-action'), workerButton.getAttribute('data-worker-id'));
                return;
            }

            const surfaceButton = event.target.closest('[data-surface-action]');
            if (surfaceButton) {
                handleSurfaceAction(surfaceButton.getAttribute('data-surface-action'), surfaceButton.getAttribute('data-surface-id'));
                return;
            }

            const attentionButton = event.target.closest('[data-attention-action]');
            if (attentionButton) {
                handleAttentionAction(attentionButton.getAttribute('data-attention-action'), attentionButton.getAttribute('data-attention-id'));
            }
        });

        Runs.renderActionButtons();
        Runs.renderRunFeedback();
    }

    Object.assign(Runs, {
        bindStaticEvents
    });
})();
