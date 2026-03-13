(function () {
    const AgentConsole = window.AgentConsole;
    const Assistant = AgentConsole.Assistant;

    let dragRoundId = null;

    function collectPayload() {
        return {
            title: document.getElementById('assistantTitleInput')?.value.trim() || '',
            goal: document.getElementById('assistantGoalInput')?.value.trim() || '',
            workspaceRoot: document.getElementById('assistantWorkspaceRootInput')?.value.trim() || null,
            initialRoundCount: Number.parseInt(document.getElementById('assistantInitialRoundsInput')?.value || '3', 10) || 3,
            planningBatchSize: Number.parseInt(document.getElementById('assistantBatchSizeInput')?.value || '3', 10) || 3,
            maxRounds: Number.parseInt(document.getElementById('assistantMaxRoundsInput')?.value || '9', 10) || 9,
            fullAutoEnabled: !!document.getElementById('assistantFullAutoInput')?.checked,
            selectedRoleIds: []
        };
    }

    async function generatePlan() {
        const payload = collectPayload();
        const streamId = Assistant.generateStreamId?.() || `assistant-plan-${Date.now()}`;
        let firstByteNotified = false;
        if (!payload.goal) {
            Assistant.setFeedback({
                tone: 'error',
                title: '还差一个总目标',
                message: '请先告诉 AI 助手这次到底要解决什么问题。',
                detail: '没有目标，它就只能开始哲学讨论了。'
            });
            AgentConsole.utils.showToast('请先填写 AI 助手总目标', 'error');
            return;
        }

        payload.clientStreamId = streamId;
        Assistant.setBusyAction('generate');
        Assistant.startGenerationStream?.(streamId);
        Assistant.setFeedback({
            tone: 'running',
            title: 'AI 助手正在设计方案…',
            message: '它会先摸底问题，再给出当前轮和后续预测轮次。',
            detail: '这次会把实时返回尽量直接展示出来，不再让页面装哑巴。'
        });

        try {
            const plan = await AgentConsole.api.generateAssistantPlan(payload, {
                onResponseStart: () => {
                    Assistant.setFeedback({
                        tone: 'running',
                        title: '服务端已开始响应…',
                        message: '请求已经到达后端，正在等 AI 助手开始回字。',
                        detail: '如果模型已经开始流式输出，下面会出现实时预览。'
                    });
                },
                onFirstByte: () => {
                    if (firstByteNotified) {
                        return;
                    }
                    firstByteNotified = true;
                    Assistant.setFeedback({
                        tone: 'running',
                        title: 'AI 助手已开始返回内容…',
                        message: '已经收到首字节，正在拼装完整方案。',
                        detail: '实时预览可能先是零碎 JSON，这是正常现象。'
                    });
                }
            });

            Assistant.stopGenerationStream?.(streamId);
            AgentConsole.upsertAssistantPlan(plan);
            AgentConsole.state.selectedAssistantPlanId = plan.planId;
            Assistant.renderPlansList();
            Assistant.renderSelectedPlan();
            Assistant.setFeedback({
                tone: 'success',
                title: '方案已生成',
                message: plan.summary || 'AI 助手已经给出一份首批轮次方案。',
                detail: '你现在可以拖动轮次顺序、删除某轮，再决定是否按它创建 Run。'
            });
            AgentConsole.utils.showToast('AI 助手方案已生成');
        } catch (error) {
            console.error(error);
            Assistant.stopGenerationStream?.(streamId);
            Assistant.setFeedback({
                tone: 'error',
                title: '生成方案失败',
                message: error.message || '生成 AI 助手方案失败',
                detail: '先看看目标描述是否过于空泛，或者稍后再试一次。'
            });
            AgentConsole.utils.showToast(error.message || '生成 AI 助手方案失败', 'error');
        } finally {
            Assistant.clearBusyAction('generate');
        }
    }

    function reorderRounds(plan, fromRoundId, toRoundId) {
        const rounds = [...(plan.rounds || [])];
        const fromIndex = rounds.findIndex(item => item.roundId === fromRoundId);
        const toIndex = rounds.findIndex(item => item.roundId === toRoundId);
        if (fromIndex < 0 || toIndex < 0 || fromIndex === toIndex) {
            return plan;
        }

        const [item] = rounds.splice(fromIndex, 1);
        rounds.splice(toIndex, 0, item);
        plan.rounds = rounds.map((round, index) => ({
            ...round,
            roundNumber: index + 1
        }));
        return plan;
    }

    async function savePlan(plan) {
        Assistant.setBusyAction('save');
        try {
            const saved = await AgentConsole.api.saveAssistantPlan(plan);
            AgentConsole.upsertAssistantPlan(saved);
            AgentConsole.state.selectedAssistantPlanId = saved.planId;
            Assistant.renderPlansList();
            Assistant.renderSelectedPlan();
            Assistant.setFeedback({
                tone: 'success',
                title: '方案已保存',
                message: '轮次顺序和删除结果已经落盘。',
                detail: '后续按方案创建 Run 时，会使用这份最新结构。'
            });
        } catch (error) {
            console.error(error);
            Assistant.setFeedback({
                tone: 'error',
                title: '保存方案失败',
                message: error.message || '保存 AI 助手方案失败',
                detail: '请稍后再试，或者先重新生成一个方案。'
            });
            AgentConsole.utils.showToast(error.message || '保存 AI 助手方案失败', 'error');
        } finally {
            Assistant.clearBusyAction('save');
        }
    }

    async function createRunFromPlan() {
        const plan = Assistant.getSelectedPlan();
        if (!plan) {
            AgentConsole.utils.showToast('请先选择一个 AI 助手方案', 'error');
            return;
        }

        Assistant.setBusyAction('create-run');
        Assistant.setFeedback({
            tone: 'running',
            title: '正在按方案创建 Run…',
            message: '会把 AI 助手方案绑定到新的协作运行，再让超管自己判断是否自动推进。',
            detail: '这一步完成后，会自动切到“协作运行”去看细节。'
        });

        try {
            const run = await AgentConsole.api.createRunFromAssistantPlan(plan.planId, { autoStart: true });
            AgentConsole.upsertRun(run);
            await AgentConsole.refreshAll();
            AgentConsole.state.selectedRunId = run.runId;
            document.querySelectorAll('.workspace-tab').forEach(button => {
                button.classList.toggle('active', button.getAttribute('data-view') === 'runs');
            });
            document.querySelectorAll('.workspace-view').forEach(panel => {
                panel.classList.toggle('active', panel.id === 'view-runs');
            });
            AgentConsole.Runs?.selectRun?.(run.runId, { keepTab: false, silent: true });
            Assistant.setFeedback({
                tone: 'success',
                title: 'Run 已创建',
                message: 'AI 助手方案已经绑定到新的协作运行。',
                detail: '接下来你会在协作运行页看到超管摘要、skill 路径和自动推进结果。'
            });
            AgentConsole.utils.showToast('已按 AI 助手方案创建 Run');
        } catch (error) {
            console.error(error);
            Assistant.setFeedback({
                tone: 'error',
                title: '创建 Run 失败',
                message: error.message || '按 AI 助手方案创建 Run 失败',
                detail: '通常是工作区初始化、角色匹配或后端生成 prompt 时出了问题。'
            });
            AgentConsole.utils.showToast(error.message || '按 AI 助手方案创建 Run 失败', 'error');
        } finally {
            Assistant.clearBusyAction('create-run');
        }
    }

    function handleRoundMove(direction, roundId) {
        const plan = Assistant.getSelectedPlan();
        if (!plan) {
            return;
        }

        const rounds = [...(plan.rounds || [])];
        const index = rounds.findIndex(round => round.roundId === roundId);
        if (index < 0) {
            return;
        }

        const targetIndex = direction === 'up' ? index - 1 : index + 1;
        if (targetIndex < 0 || targetIndex >= rounds.length) {
            return;
        }

        const updated = reorderRounds(structuredClone(plan), roundId, rounds[targetIndex].roundId);
        AgentConsole.upsertAssistantPlan(updated);
        AgentConsole.state.selectedAssistantPlanId = updated.planId;
        Assistant.renderPlansList();
        Assistant.renderSelectedPlan();
        savePlan(updated);
    }

    function handleRoundDelete(roundId) {
        const plan = Assistant.getSelectedPlan();
        if (!plan) {
            return;
        }

        if (!confirm('确认删除这一轮吗？删除后会重新编号。')) {
            return;
        }

        const updated = structuredClone(plan);
        updated.rounds = (updated.rounds || [])
            .filter(round => round.roundId !== roundId)
            .map((round, index) => ({ ...round, roundNumber: index + 1 }));
        AgentConsole.upsertAssistantPlan(updated);
        Assistant.renderPlansList();
        Assistant.renderSelectedPlan();
        savePlan(updated);
    }

    function bindStaticEvents() {
        document.getElementById('btnGenerateAssistantPlan')?.addEventListener('click', generatePlan);
        document.getElementById('btnCreateRunFromAssistantPlan')?.addEventListener('click', createRunFromPlan);

        document.getElementById('assistantPlansList')?.addEventListener('click', event => {
            const planId = event.target.closest('[data-assistant-plan-select]')?.getAttribute('data-assistant-plan-select');
            if (!planId) {
                return;
            }

            AgentConsole.state.selectedAssistantPlanId = planId;
            Assistant.renderPlansList();
            Assistant.renderSelectedPlan();
            Assistant.renderActionButtons?.();
        });

        document.getElementById('assistantPlanDetail')?.addEventListener('click', event => {
            if (event.target.id === 'assistantSavePlanInline') {
                const plan = Assistant.getSelectedPlan();
                if (plan) {
                    savePlan(plan);
                }
                return;
            }

            if (event.target.id === 'assistantCreateRunInline') {
                createRunFromPlan();
                return;
            }

            const moveButton = event.target.closest('[data-assistant-round-move]');
            if (moveButton) {
                handleRoundMove(moveButton.getAttribute('data-assistant-round-move'), moveButton.getAttribute('data-round-id'));
                return;
            }

            const deleteButton = event.target.closest('[data-assistant-round-delete]');
            if (deleteButton) {
                handleRoundDelete(deleteButton.getAttribute('data-assistant-round-delete'));
            }
        });

        document.getElementById('assistantPlanDetail')?.addEventListener('dragstart', event => {
            const card = event.target.closest('[data-round-id]');
            if (!card) {
                return;
            }

            dragRoundId = card.getAttribute('data-round-id');
            card.classList.add('dragging');
        });

        document.getElementById('assistantPlanDetail')?.addEventListener('dragend', event => {
            const card = event.target.closest('[data-round-id]');
            if (card) {
                card.classList.remove('dragging');
            }
            dragRoundId = null;
        });

        document.getElementById('assistantPlanDetail')?.addEventListener('dragover', event => {
            const target = event.target.closest('[data-round-id]');
            if (!target || !dragRoundId) {
                return;
            }
            event.preventDefault();
        });

        document.getElementById('assistantPlanDetail')?.addEventListener('drop', event => {
            const target = event.target.closest('[data-round-id]');
            if (!target || !dragRoundId) {
                return;
            }
            event.preventDefault();
            const targetRoundId = target.getAttribute('data-round-id');
            const plan = Assistant.getSelectedPlan();
            if (!plan || !targetRoundId || targetRoundId === dragRoundId) {
                return;
            }

            const updated = reorderRounds(structuredClone(plan), dragRoundId, targetRoundId);
            AgentConsole.upsertAssistantPlan(updated);
            AgentConsole.state.selectedAssistantPlanId = updated.planId;
            Assistant.renderPlansList();
            Assistant.renderSelectedPlan();
            savePlan(updated);
        });

        Assistant.renderActionButtons?.();
        Assistant.renderFeedback?.();
    }

    window.AgentConsole.Assistant = Object.assign(window.AgentConsole.Assistant || {}, {
        bindStaticEvents
    });
})();
