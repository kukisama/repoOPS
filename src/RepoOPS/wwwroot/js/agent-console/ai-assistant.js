(function () {
    const AgentConsole = window.AgentConsole;

    const feedbackState = {
        busyAction: null,
        feedback: null,
        generationStreamId: null,
        generationStream: null
    };

    function generateStreamId() {
        const randomSuffix = Math.random().toString(36).slice(2, 10);
        return `assistant-plan-${Date.now()}-${randomSuffix}`;
    }

    function getSelectedPlan() {
        return AgentConsole.state.assistantPlans.find(plan => plan.planId === AgentConsole.state.selectedAssistantPlanId) || null;
    }

    function setBusyAction(action) {
        feedbackState.busyAction = action;
        renderActionButtons();
    }

    function clearBusyAction(action) {
        if (!action || feedbackState.busyAction === action) {
            feedbackState.busyAction = null;
        }

        renderActionButtons();
    }

    function renderActionButtons() {
        const isBusy = !!feedbackState.busyAction;
        const generateButton = document.getElementById('btnGenerateAssistantPlan');
        const createRunButton = document.getElementById('btnCreateRunFromAssistantPlan');
        if (generateButton) {
            generateButton.disabled = isBusy;
            generateButton.textContent = feedbackState.busyAction === 'generate' ? 'AI 生成中…' : '生成 AI 方案';
            generateButton.classList.toggle('is-busy', isBusy);
        }

        if (createRunButton) {
            createRunButton.disabled = isBusy || !getSelectedPlan();
            createRunButton.textContent = feedbackState.busyAction === 'create-run' ? '正在创建…' : '按方案创建 Run';
            createRunButton.classList.toggle('is-busy', isBusy);
        }
    }

    function setFeedback(options) {
        feedbackState.feedback = {
            tone: options?.tone || 'neutral',
            title: options?.title || '',
            message: options?.message || '',
            detail: options?.detail || ''
        };
        renderFeedback();
    }

    function clearFeedback() {
        feedbackState.feedback = null;
        renderFeedback();
    }

    function startGenerationStream(streamId) {
        feedbackState.generationStreamId = streamId || null;
        feedbackState.generationStream = streamId
            ? {
                streamId,
                title: 'AI 助手正在设计方案…',
                preview: '',
                commandPreview: '',
                updatedAt: new Date().toISOString(),
                status: 'running',
                error: null,
                active: true
            }
            : null;
        renderFeedback();
    }

    function stopGenerationStream(streamId) {
        if (streamId && feedbackState.generationStreamId && feedbackState.generationStreamId !== streamId) {
            return;
        }

        const activeStreamId = feedbackState.generationStreamId;
        feedbackState.generationStreamId = null;
        feedbackState.generationStream = null;
        if (activeStreamId) {
            AgentConsole.state.supervisorStreams?.delete?.(activeStreamId);
        }
        renderFeedback();
    }

    function handleSupervisorStreamEvent(eventName, runId, payload) {
        if (!runId || !feedbackState.generationStreamId || runId !== feedbackState.generationStreamId) {
            return;
        }

        const existing = feedbackState.generationStream || { streamId: runId };
        feedbackState.generationStream = {
            ...existing,
            streamId: runId,
            title: payload?.title || existing.title || 'AI 助手正在返回内容…',
            preview: payload?.preview || existing.preview || '',
            commandPreview: payload?.commandPreview || existing.commandPreview || '',
            updatedAt: payload?.updatedAt || new Date().toISOString(),
            status: payload?.status || existing.status || 'running',
            error: payload?.error || existing.error || null,
            active: eventName !== 'completed' && payload?.status !== 'failed'
        };

        renderFeedback();
    }

    function renderFeedback() {
        const container = document.getElementById('assistantFeedbackPanel');
        if (!container) {
            return;
        }

        const feedback = feedbackState.feedback;
        if (!feedback) {
            container.hidden = true;
            container.innerHTML = '';
            return;
        }

        const generationStream = feedbackState.generationStream;
        const preview = generationStream?.preview || '';
        const previewTitle = generationStream?.active ? '实时返回预览' : '最后返回预览';
        const meta = [];
        if (generationStream?.title) {
            meta.push(generationStream.title);
        }
        if (generationStream?.updatedAt) {
            meta.push(`更新于 ${AgentConsole.utils.formatDateTime(generationStream.updatedAt)}`);
        }

        container.hidden = false;
        container.innerHTML = `
            <div class="agent-feedback-card ${AgentConsole.utils.escapeHtml(feedback.tone || 'neutral')}">
                <div class="agent-feedback-header">
                    <div class="agent-feedback-title-row">
                        ${(feedback.tone === 'running' || generationStream?.active) ? '<span class="agent-feedback-spinner" aria-hidden="true"></span>' : ''}
                        <strong>${AgentConsole.utils.escapeHtml(feedback.title || 'AI 助手')}</strong>
                    </div>
                </div>
                <div class="agent-feedback-message">${AgentConsole.utils.escapeHtml(feedback.message || '')}</div>
                ${feedback.detail ? `<div class="agent-feedback-detail">${AgentConsole.utils.escapeHtml(feedback.detail)}</div>` : ''}
                ${meta.length ? `<div class="agent-feedback-meta">${AgentConsole.utils.escapeHtml(meta.join(' · '))}</div>` : ''}
                ${preview ? `
                    <div class="agent-feedback-preview-shell">
                        <div class="agent-feedback-preview-title">${AgentConsole.utils.escapeHtml(previewTitle)}</div>
                        <pre class="agent-feedback-preview">${AgentConsole.utils.escapeHtml(preview)}</pre>
                    </div>
                ` : ''}
                ${generationStream?.error ? `<div class="agent-feedback-detail">${AgentConsole.utils.escapeHtml(generationStream.error)}</div>` : ''}
            </div>
        `;
    }

    window.AgentConsole.Assistant = Object.assign(window.AgentConsole.Assistant || {}, {
        generateStreamId,
        getSelectedPlan,
        setBusyAction,
        clearBusyAction,
        renderActionButtons,
        setFeedback,
        clearFeedback,
        renderFeedback,
        startGenerationStream,
        stopGenerationStream,
        handleSupervisorStreamEvent
    });
})();
