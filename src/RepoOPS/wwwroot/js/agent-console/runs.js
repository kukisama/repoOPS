(function () {
    const AgentConsole = window.AgentConsole;

    const runTabs = [
        { id: 'workspace', label: '工作台' },
        { id: 'round', label: '轮次' },
        { id: 'attention', label: '提醒事项' },
        { id: 'decisions', label: '调度决策' },
        { id: 'verification', label: '构建/检查' },
        { id: 'round-history', label: '轮次记录' },
        { id: 'snapshot', label: '快照' }
    ];

    const feedbackState = {
        busyAction: null,
        feedback: null,
        progressTimer: null,
        typewriterTimer: null
    };

    const embeddedTerminalState = {
        terminals: new Map()
    };

    const renderState = {
        selectedRunTimer: null
    };

    function getSelectedRun() {
        return AgentConsole.state.runs.find(run => run.runId === AgentConsole.state.selectedRunId) || null;
    }

    function getSelectedSnapshot() {
        return AgentConsole.state.selectedRunId
            ? AgentConsole.state.snapshots.get(AgentConsole.state.selectedRunId) || null
            : null;
    }

    function clearProgressTimer() {
        if (feedbackState.progressTimer) {
            window.clearInterval(feedbackState.progressTimer);
            feedbackState.progressTimer = null;
        }
    }

    function clearTypewriterTimer() {
        if (feedbackState.typewriterTimer) {
            window.clearTimeout(feedbackState.typewriterTimer);
            feedbackState.typewriterTimer = null;
        }
    }

    function applyButtonState(buttonId, isBusy, idleText, busyText) {
        const button = document.getElementById(buttonId);
        if (!button) {
            return;
        }

        button.disabled = isBusy;
        button.textContent = isBusy ? busyText : idleText;
        button.classList.toggle('is-busy', isBusy);
    }

    function applyInputState(elementId, isBusy) {
        const element = document.getElementById(elementId);
        if (!element) {
            return;
        }

        element.disabled = isBusy;
        element.classList.toggle('is-busy', isBusy);
    }

    function applyContainerBusyState(selector, isBusy) {
        document.querySelectorAll(selector).forEach(element => {
            if ('disabled' in element) {
                element.disabled = isBusy;
            }
            element.classList.toggle('is-busy', isBusy);
        });
    }

    function renderActionButtons() {
        const isSuggestBusy = feedbackState.busyAction === 'suggest';
        const isCreateBusy = feedbackState.busyAction === 'create';
        const isAnyBusy = !!feedbackState.busyAction;

        applyButtonState('btnSuggestRoles', isAnyBusy, 'AI 建议角色', isSuggestBusy ? 'AI 分析中…' : '请稍候…');
        applyButtonState('btnCreateRun', isAnyBusy, '创建并启动', isSuggestBusy ? '等待分析完成…' : '正在创建…');
        applyInputState('runAutopilotInput', isAnyBusy);
        applyInputState('runMaxAutoStepsInput', isAnyBusy);
        applyInputState('runTitleInput', isCreateBusy);
        applyInputState('runGoalInput', isCreateBusy);
        applyInputState('runWorkspaceRootInput', isCreateBusy);
        applyContainerBusyState('#runRoleOptions input, #runRoleProposalPanel input, #runRoleProposalPanel textarea', isAnyBusy);
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

    function animateText(element, text, speed = 16) {
        clearTypewriterTimer();

        if (!element) {
            return;
        }

        const content = text == null ? '' : String(text);
        if (!content) {
            element.textContent = '';
            return;
        }

        element.textContent = '';
        let index = 0;
        const chunkSize = content.length > 120 ? 2 : 1;

        const step = () => {
            index = Math.min(content.length, index + chunkSize);
            element.textContent = content.slice(0, index);
            if (index < content.length) {
                feedbackState.typewriterTimer = window.setTimeout(step, speed);
            } else {
                feedbackState.typewriterTimer = null;
            }
        };

        step();
    }

    function getCurrentFeedbackDetail() {
        const feedback = feedbackState.feedback;
        if (!feedback) {
            return '';
        }

        if (feedback.pending && Array.isArray(feedback.phases) && feedback.phases.length) {
            return feedback.phases[feedback.phaseIndex % feedback.phases.length];
        }

        return feedback.detail || '';
    }

    function renderRunFeedback() {
        const container = document.getElementById('runFeedbackPanel');
        if (!container) {
            return;
        }

        const feedback = feedbackState.feedback;
        if (!feedback) {
            container.innerHTML = '';
            container.hidden = true;
            return;
        }

        const elapsedSeconds = feedback.pending && feedback.startedAt
            ? Math.max(0, Math.floor((Date.now() - feedback.startedAt) / 1000))
            : null;
        const detail = getCurrentFeedbackDetail();

        container.hidden = false;
        container.innerHTML = `
            <div class="agent-feedback-card ${AgentConsole.utils.escapeHtml(feedback.tone || 'neutral')} ${feedback.pending ? 'pending' : ''}">
                <div class="agent-feedback-header">
                    <div class="agent-feedback-title-row">
                        ${feedback.pending ? '<span class="agent-feedback-spinner" aria-hidden="true"></span>' : ''}
                        <strong>${AgentConsole.utils.escapeHtml(feedback.title || '处理中')}</strong>
                    </div>
                    ${elapsedSeconds !== null ? `<span class="agent-feedback-meta">已等待 ${elapsedSeconds} 秒</span>` : ''}
                </div>
                <div class="agent-feedback-message" data-run-feedback-message></div>
                ${detail ? `<div class="agent-feedback-detail">${AgentConsole.utils.escapeHtml(detail)}</div>` : ''}
            </div>
        `;

        const messageElement = container.querySelector('[data-run-feedback-message]');
        if (!messageElement) {
            return;
        }

        if (feedback.animate) {
            animateText(messageElement, feedback.message || '', 12);
        } else {
            messageElement.textContent = feedback.message || '';
        }
    }

    function setRunFeedback(options) {
        clearProgressTimer();
        clearTypewriterTimer();

        feedbackState.feedback = {
            tone: options?.tone || 'neutral',
            title: options?.title || '',
            message: options?.message || '',
            detail: options?.detail || '',
            pending: !!options?.pending,
            phases: Array.isArray(options?.phases) ? options.phases : [],
            phaseIndex: 0,
            startedAt: options?.pending ? Date.now() : null,
            animate: !!options?.animate
        };

        renderRunFeedback();

        if (feedbackState.feedback.pending && feedbackState.feedback.phases.length > 1) {
            feedbackState.progressTimer = window.setInterval(() => {
                if (!feedbackState.feedback?.pending) {
                    return;
                }

                feedbackState.feedback.phaseIndex = (feedbackState.feedback.phaseIndex + 1) % feedbackState.feedback.phases.length;
                renderRunFeedback();
            }, 2200);
        }
    }

    function clearRunFeedback() {
        clearProgressTimer();
        clearTypewriterTimer();
        feedbackState.feedback = null;
        renderRunFeedback();
    }

    function disposeEmbeddedTerminals() {
        embeddedTerminalState.terminals.forEach(info => {
            try {
                info.fitAddon?.dispose?.();
            } catch {
                // Ignore terminal cleanup issues.
            }

            try {
                info.terminal?.dispose?.();
            } catch {
                // Ignore terminal cleanup issues.
            }
        });

        embeddedTerminalState.terminals.clear();
    }

    function renderEmbeddedTerminals(previews) {
        disposeEmbeddedTerminals();

        if (!Array.isArray(previews) || !previews.length) {
            return;
        }

        previews.forEach(preview => {
            const host = document.querySelector(`[data-agent-terminal-id="${preview.id}"]`);
            if (!host) {
                return;
            }

            host.innerHTML = '';

            if (!window.Terminal) {
                host.textContent = preview.output || '';
                return;
            }

            const terminal = new window.Terminal({
                theme: window.Theme?.getTerminalTheme?.() || {
                    background: '#0f172a',
                    foreground: '#d6e2ff'
                },
                fontFamily: 'Consolas, "Courier New", monospace',
                fontSize: 12,
                lineHeight: 1.25,
                scrollback: 4000,
                cursorBlink: false,
                disableStdin: true,
                convertEol: true
            });

            let fitAddon = null;
            if (window.FitAddon?.FitAddon) {
                fitAddon = new window.FitAddon.FitAddon();
                terminal.loadAddon(fitAddon);
            }

            terminal.open(host);
            terminal.write(preview.output || '');
            fitAddon?.fit?.();

            embeddedTerminalState.terminals.set(preview.id, { terminal, fitAddon });
        });
    }

    function updateRunFeedback(patch) {
        if (!feedbackState.feedback) {
            return;
        }

        feedbackState.feedback = {
            ...feedbackState.feedback,
            ...patch
        };

        if (patch && Object.prototype.hasOwnProperty.call(patch, 'phaseIndex') && feedbackState.feedback.pending && feedbackState.feedback.startedAt == null) {
            feedbackState.feedback.startedAt = Date.now();
        }

        renderRunFeedback();
    }

    function scheduleSelectedRunRender(delay = 120) {
        if (renderState.selectedRunTimer) {
            return;
        }

        renderState.selectedRunTimer = window.setTimeout(() => {
            renderState.selectedRunTimer = null;
            Runs.renderSelectedRun();
        }, Math.max(0, delay));
    }

    window.AgentConsole.Runs = Object.assign(window.AgentConsole.Runs || {}, {
        runTabs,
        getSelectedRun,
        getSelectedSnapshot,
        renderActionButtons,
        setBusyAction,
        clearBusyAction,
        animateText,
        renderRunFeedback,
        setRunFeedback,
        clearRunFeedback,
        updateRunFeedback,
        scheduleSelectedRunRender,
        renderEmbeddedTerminals,
        disposeEmbeddedTerminals
    });
})();
