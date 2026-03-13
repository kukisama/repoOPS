(function () {
    const state = {
        roleCatalog: null,
        roles: [],
        settings: null,
        runs: [],
        assistantPlans: [],
        selectedAssistantPlanId: null,
        roleProposal: null,
        selectedRunId: null,
        selectedRoleId: null,
        activeRunDetailTab: 'workspace',
        activeSettingsTab: 'orchestration',
        snapshots: new Map(),
        supervisorStreams: new Map(),
        initialized: false,
        hubRegistered: false
    };

    function escapeHtml(value) {
        const div = document.createElement('div');
        div.textContent = value == null ? '' : String(value);
        return div.innerHTML;
    }

    function formatDateTime(value) {
        if (!value) {
            return '—';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return '—';
        }

        return date.toLocaleString('zh-CN', {
            year: 'numeric',
            month: '2-digit',
            day: '2-digit',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        });
    }

    function formatRelative(value) {
        if (!value) {
            return '—';
        }

        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return '—';
        }

        const diffMs = date.getTime() - Date.now();
        const diffMinutes = Math.round(diffMs / 60000);
        if (Math.abs(diffMinutes) < 1) {
            return '刚刚';
        }

        if (Math.abs(diffMinutes) < 60) {
            return `${Math.abs(diffMinutes)} 分钟${diffMinutes < 0 ? '前' : '后'}`;
        }

        const diffHours = Math.round(diffMinutes / 60);
        if (Math.abs(diffHours) < 24) {
            return `${Math.abs(diffHours)} 小时${diffHours < 0 ? '前' : '后'}`;
        }

        const diffDays = Math.round(diffHours / 24);
        return `${Math.abs(diffDays)} 天${diffDays < 0 ? '前' : '后'}`;
    }

    function trimText(value, maxLength = 220) {
        if (!value) {
            return '';
        }

        const text = String(value).trim();
        return text.length <= maxLength ? text : `${text.slice(0, maxLength)}…`;
    }

    function toMultilineList(values) {
        return Array.isArray(values) ? values.join('\n') : '';
    }

    function fromMultilineList(value) {
        return String(value || '')
            .split(/\r?\n/)
            .map(item => item.trim())
            .filter(Boolean);
    }

    function dictionaryToLines(dictionary) {
        return Object.entries(dictionary || {})
            .map(([key, value]) => `${key}=${value}`)
            .join('\n');
    }

    function linesToDictionary(value) {
        const result = {};
        String(value || '')
            .split(/\r?\n/)
            .map(item => item.trim())
            .filter(Boolean)
            .forEach(line => {
                const index = line.indexOf('=');
                if (index <= 0) {
                    return;
                }

                const key = line.slice(0, index).trim();
                const entryValue = line.slice(index + 1).trim();
                if (key) {
                    result[key] = entryValue;
                }
            });

        return result;
    }

    function statusTone(status) {
        switch (String(status || '').trim().toLowerCase()) {
            case 'running':
            case 'verifying':
            case 'orchestrating':
                return 'running';
            case 'completed':
            case 'success':
            case 'passed':
                return 'success';
            case 'failed':
            case 'error':
                return 'error';
            case 'needs-human':
            case 'needs-attention':
            case 'blocked':
            case 'review':
                return 'warning';
            default:
                return 'neutral';
        }
    }

    function attentionTone(level) {
        switch (String(level || '').trim().toLowerCase()) {
            case 'error':
                return 'error';
            case 'warning':
                return 'warning';
            case 'running':
                return 'running';
            default:
                return 'neutral';
        }
    }

    function showToast(message, type = 'success') {
        const toast = document.createElement('div');
        toast.className = `toast toast-${type}`;
        toast.textContent = message;
        document.body.appendChild(toast);
        requestAnimationFrame(() => toast.classList.add('toast-visible'));
        setTimeout(() => {
            toast.classList.remove('toast-visible');
            setTimeout(() => toast.remove(), 260);
        }, 2600);
    }

    async function readResponsePayload(response, contentType, hooks = {}) {
        const { onFirstByte } = hooks;

        if (!response.body || typeof onFirstByte !== 'function') {
            return contentType.includes('application/json') ? await response.json() : await response.text();
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let raw = '';
        let firstByteSeen = false;

        while (true) {
            const { done, value } = await reader.read();
            if (done) {
                break;
            }

            if (!firstByteSeen && value?.length) {
                firstByteSeen = true;
                try {
                    onFirstByte();
                } catch {
                    // Ignore callback failures so payload parsing can continue.
                }
            }

            raw += decoder.decode(value, { stream: true });
        }

        raw += decoder.decode();

        if (contentType.includes('application/json')) {
            return raw ? JSON.parse(raw) : null;
        }

        return raw;
    }

    async function requestJson(url, options = {}) {
        const { onResponseStart, onFirstByte, ...fetchOptions } = options;
        const response = await fetch(url, fetchOptions);
        if (typeof onResponseStart === 'function') {
            try {
                onResponseStart(response);
            } catch {
                // Ignore callback failures so request handling can continue.
            }
        }

        const contentType = response.headers.get('content-type') || '';
        const payload = await readResponsePayload(response, contentType, { onFirstByte });

        if (!response.ok) {
            let errorMessage = '请求失败';
            if (payload && typeof payload === 'object') {
                errorMessage = payload.error || payload.title || payload.detail || JSON.stringify(payload);
            } else if (payload) {
                errorMessage = payload;
            }

            throw new Error(errorMessage);
        }

        return payload;
    }

    const api = {
        getRoles() {
            return requestJson('/api/agent/roles');
        },
        proposeRoles(payload, callbacks) {
            return requestJson('/api/agent/roles/propose', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
                onResponseStart: callbacks?.onResponseStart,
                onFirstByte: callbacks?.onFirstByte
            });
        },
        saveRoles(payload) {
            return requestJson('/api/agent/roles', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
        },
        getSettings() {
            return requestJson('/api/agent/settings');
        },
        saveSettings(payload) {
            return requestJson('/api/agent/settings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
        },
        getRuns() {
            return requestJson('/api/agent/runs');
        },
        getAssistantPlans() {
            return requestJson('/api/ai-assistant/plans');
        },
        getAssistantPlan(planId) {
            return requestJson(`/api/ai-assistant/plans/${encodeURIComponent(planId)}`);
        },
        generateAssistantPlan(payload, callbacks) {
            return requestJson('/api/ai-assistant/plans/generate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload),
                onResponseStart: callbacks?.onResponseStart,
                onFirstByte: callbacks?.onFirstByte
            });
        },
        saveAssistantPlan(plan) {
            return requestJson(`/api/ai-assistant/plans/${encodeURIComponent(plan.planId)}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(plan)
            });
        },
        createRunFromAssistantPlan(planId, payload) {
            return requestJson(`/api/ai-assistant/plans/${encodeURIComponent(planId)}/create-run`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload || {})
            });
        },
        getRun(runId) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}`);
        },
        createRun(payload) {
            return requestJson('/api/agent/runs', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
        },
        getRunSnapshot(runId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/snapshot`);
        },
        getAttention(runId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/attention`);
        },
        startWorker(runId, workerId, prompt) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/workers/${encodeURIComponent(workerId)}/start`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ prompt: prompt || null })
            });
        },
        continueWorker(runId, workerId, prompt) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/workers/${encodeURIComponent(workerId)}/continue`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ prompt: prompt || null })
            });
        },
        stopWorker(runId, workerId) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/workers/${encodeURIComponent(workerId)}/stop`, {
                method: 'POST'
            });
        },
        askSupervisor(runId, extraInstruction) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/supervisor`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ extraInstruction: extraInstruction || null })
            });
        },
        autoStep(runId, payload) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/auto-step`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
        },
        verifyRun(runId, command) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/verify`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ command: command || null })
            });
        },
        openRunFolder(runId) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/open-folder`, {
                method: 'POST'
            });
        },
        setAutopilot(runId, enabled) {
            return requestJson(`/api/agent/runs/${encodeURIComponent(runId)}/autopilot/${enabled}`, {
                method: 'POST'
            });
        },
        pauseRun(runId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/pause`, { method: 'POST' });
        },
        resumeRun(runId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/resume`, { method: 'POST' });
        },
        completeRun(runId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/complete`, { method: 'POST' });
        },
        archiveRun(runId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/archive`, { method: 'POST' });
        },
        focusSurface(runId, surfaceId, acknowledgeRelatedAttention = true) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/surfaces/${encodeURIComponent(surfaceId)}/focus-intent`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ acknowledgeRelatedAttention })
            });
        },
        acknowledgeAttention(runId, eventId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/attention/${encodeURIComponent(eventId)}/ack`, {
                method: 'POST'
            });
        },
        acknowledgeAllAttention(runId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/attention/ack-all`, {
                method: 'POST'
            });
        },
        resolveAttention(runId, eventId) {
            return requestJson(`/api/orchestration/runs/${encodeURIComponent(runId)}/attention/${encodeURIComponent(eventId)}/resolve`, {
                method: 'POST'
            });
        }
    };

    function sortRuns() {
        state.runs = [...state.runs].sort((left, right) => {
            const rightTime = new Date(right.updatedAt || right.createdAt || 0).getTime();
            const leftTime = new Date(left.updatedAt || left.createdAt || 0).getTime();
            return rightTime - leftTime;
        });
    }

    function sortAssistantPlans() {
        state.assistantPlans = [...state.assistantPlans].sort((left, right) => {
            const rightTime = new Date(right.updatedAt || right.createdAt || 0).getTime();
            const leftTime = new Date(left.updatedAt || left.createdAt || 0).getTime();
            return rightTime - leftTime;
        });
    }

    function upsertRun(run) {
        if (!run || !run.runId) {
            return;
        }

        const index = state.runs.findIndex(item => item.runId === run.runId);
        if (index >= 0) {
            state.runs.splice(index, 1, run);
        } else {
            state.runs.push(run);
        }

        sortRuns();
    }

    function storeSnapshot(snapshot) {
        if (!snapshot || !snapshot.run || !snapshot.run.runId) {
            return;
        }

        state.snapshots.set(snapshot.run.runId, snapshot);
        upsertRun(snapshot.run);
    }

    function upsertAssistantPlan(plan) {
        if (!plan || !plan.planId) {
            return;
        }

        const index = state.assistantPlans.findIndex(item => item.planId === plan.planId);
        if (index >= 0) {
            state.assistantPlans.splice(index, 1, plan);
        } else {
            state.assistantPlans.push(plan);
        }

        sortAssistantPlans();
    }

    function bindWorkspaceTabs() {
        const buttons = document.querySelectorAll('.workspace-tab');
        buttons.forEach(button => {
            button.addEventListener('click', () => {
                const view = button.getAttribute('data-view');
                if (!view) {
                    return;
                }

                buttons.forEach(item => item.classList.toggle('active', item === button));
                document.querySelectorAll('.workspace-view').forEach(panel => {
                    panel.classList.toggle('active', panel.id === `view-${view}`);
                });
            });
        });
    }

    function registerHubEvents() {
        if (state.hubRegistered) {
            return;
        }

        if (typeof connection === 'undefined' || !connection || typeof connection.on !== 'function') {
            setTimeout(registerHubEvents, 600);
            return;
        }

        state.hubRegistered = true;

        connection.on('RunUpdated', run => {
            upsertRun(run);
            window.AgentConsole?.Runs?.renderRunsList();
        });

        connection.on('RunSnapshotUpdated', snapshot => {
            storeSnapshot(snapshot);
            const live = state.supervisorStreams.get(snapshot?.run?.runId);
            if (live && !live.active) {
                state.supervisorStreams.delete(snapshot.run.runId);
            }
            window.AgentConsole?.Runs?.renderRunsList();
            if (snapshot?.run?.runId === state.selectedRunId) {
                window.AgentConsole?.Runs?.renderSelectedRun();
            }
        });

        connection.on('SupervisorStreamStarted', (runId, payload) => {
            state.supervisorStreams.set(runId, {
                active: true,
                title: payload?.title || '调度器正在返回内容',
                commandPreview: payload?.commandPreview || '',
                output: payload?.preview || '',
                updatedAt: payload?.updatedAt || new Date().toISOString(),
                status: payload?.status || 'running',
                error: null
            });

            window.AgentConsole?.Assistant?.handleSupervisorStreamEvent?.('started', runId, payload);

            if (runId === state.selectedRunId) {
                window.AgentConsole?.Runs?.scheduleSelectedRunRender?.(0);
            }
        });

        connection.on('SupervisorStreamChunk', (runId, payload) => {
            const existing = state.supervisorStreams.get(runId) || {};
            state.supervisorStreams.set(runId, {
                ...existing,
                active: true,
                title: payload?.title || existing.title || '调度器正在返回内容',
                commandPreview: payload?.commandPreview || existing.commandPreview || '',
                output: payload?.preview || existing.output || '',
                updatedAt: payload?.updatedAt || new Date().toISOString(),
                status: payload?.status || 'running',
                error: existing.error || null
            });

            window.AgentConsole?.Assistant?.handleSupervisorStreamEvent?.('chunk', runId, payload);

            if (runId === state.selectedRunId) {
                window.AgentConsole?.Runs?.scheduleSelectedRunRender?.(80);
            }
        });

        connection.on('SupervisorStreamCompleted', (runId, payload) => {
            const existing = state.supervisorStreams.get(runId) || {};
            state.supervisorStreams.set(runId, {
                ...existing,
                active: false,
                title: payload?.title || existing.title || '调度器返回完成',
                commandPreview: payload?.commandPreview || existing.commandPreview || '',
                output: payload?.preview || existing.output || '',
                updatedAt: payload?.updatedAt || new Date().toISOString(),
                status: payload?.status || 'completed',
                error: payload?.error || null
            });

            window.AgentConsole?.Assistant?.handleSupervisorStreamEvent?.('completed', runId, payload);

            if (runId === state.selectedRunId) {
                window.AgentConsole?.Runs?.scheduleSelectedRunRender?.(0);
            }
        });

        connection.on('AgentWorkerStarted', runId => {
            if (runId === state.selectedRunId) {
                window.AgentConsole?.Runs?.refreshSelectedRun({ silent: true });
            }
        });

        connection.on('AgentWorkerCompleted', runId => {
            if (runId === state.selectedRunId) {
                window.AgentConsole?.Runs?.refreshSelectedRun({ silent: true });
            }
        });

        connection.on('VerificationCompleted', runId => {
            if (runId === state.selectedRunId) {
                window.AgentConsole?.Runs?.refreshSelectedRun({ silent: true });
            }
        });
    }

    async function loadInitialData() {
        const catalog = await api.getRoles();
        state.roleCatalog = catalog;
        state.roles = Array.isArray(catalog.roles) ? catalog.roles : [];
        state.settings = catalog.settings || await api.getSettings();
        state.activeSettingsTab = state.settings?.defaultSettingsTab || 'orchestration';
        state.activeRunDetailTab = state.settings?.defaultRunDetailTab || 'workspace';
        state.runs = await api.getRuns();
        state.assistantPlans = await api.getAssistantPlans();
        sortAssistantPlans();
        sortRuns();

        if (!state.selectedRoleId && state.roles.length > 0) {
            state.selectedRoleId = state.roles[0].roleId;
        }

        window.AgentConsole?.Roles?.renderList();
        window.AgentConsole?.Roles?.renderEditor();
        window.AgentConsole?.Settings?.render();
        window.AgentConsole?.Runs?.renderRoleOptions();
        window.AgentConsole?.Runs?.syncRunWorkspaceRootHint?.();
        window.AgentConsole?.Runs?.renderRunsList();
        window.AgentConsole?.Assistant?.renderPlansList?.();
        window.AgentConsole?.Assistant?.renderSelectedPlan?.();

        if (!state.selectedRunId && state.runs.length > 0) {
            await window.AgentConsole?.Runs?.selectRun(state.runs[0].runId, { keepTab: true, silent: true });
        } else {
            window.AgentConsole?.Runs?.renderSelectedRun();
        }

        if (!state.selectedAssistantPlanId && state.assistantPlans.length > 0) {
            state.selectedAssistantPlanId = state.assistantPlans[0].planId;
        }
        window.AgentConsole?.Assistant?.renderPlansList?.();
        window.AgentConsole?.Assistant?.renderSelectedPlan?.();
    }

    async function refreshAll() {
        await loadInitialData();
    }

    async function init() {
        if (state.initialized) {
            return;
        }

        state.initialized = true;
        bindWorkspaceTabs();
        registerHubEvents();
        window.AgentConsole?.Runs?.bindStaticEvents();
        window.AgentConsole?.Assistant?.bindStaticEvents?.();
        window.AgentConsole?.Roles?.bindStaticEvents();
        window.AgentConsole?.Settings?.bindStaticEvents();

        try {
            await loadInitialData();
        } catch (error) {
            console.error(error);
            showToast(error.message || '初始化控制台失败', 'error');
        }
    }

    window.AgentConsole = {
        state,
        api,
        utils: {
            escapeHtml,
            formatDateTime,
            formatRelative,
            trimText,
            toMultilineList,
            fromMultilineList,
            dictionaryToLines,
            linesToDictionary,
            statusTone,
            attentionTone,
            showToast
        },
        upsertRun,
        storeSnapshot,
        upsertAssistantPlan,
        refreshAll,
        init
    };
})();
