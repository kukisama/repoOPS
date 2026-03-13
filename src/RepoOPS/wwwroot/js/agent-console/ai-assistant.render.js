(function () {
    const AgentConsole = window.AgentConsole;
    const Assistant = AgentConsole.Assistant;

    function renderPlansList() {
        const container = document.getElementById('assistantPlansList');
        if (!container) {
            return;
        }

        const plans = AgentConsole.state.assistantPlans || [];
        if (!plans.length) {
            container.innerHTML = '<div class="workspace-empty-inline">还没有 AI 助手方案。先生成一份，看看超管到底打算怎么排兵布阵。</div>';
            Assistant.renderActionButtons?.();
            return;
        }

        container.innerHTML = plans.map(plan => {
            const active = plan.planId === AgentConsole.state.selectedAssistantPlanId;
            const rounds = plan.rounds?.length || 0;
            return `
                <button class="agent-list-item ${active ? 'active' : ''}" type="button" data-assistant-plan-select="${AgentConsole.utils.escapeHtml(plan.planId)}">
                    <div class="agent-list-item-title">${AgentConsole.utils.escapeHtml(plan.title || '未命名方案')}</div>
                    <div class="agent-meta-row">
                        <span class="agent-badge ${AgentConsole.utils.statusTone(plan.status || 'draft')}">${AgentConsole.utils.escapeHtml(plan.status || 'draft')}</span>
                        <span>${AgentConsole.utils.escapeHtml(AgentConsole.utils.formatRelative(plan.updatedAt || plan.createdAt))}</span>
                    </div>
                    <div class="agent-list-item-subtitle">${AgentConsole.utils.escapeHtml(AgentConsole.utils.trimText(plan.goal || '无目标描述', 100))}</div>
                    <div class="run-mini-metrics">
                        <span>首批轮次 ${rounds}</span>
                        <span>批次 ${plan.planningBatchSize || 3}</span>
                        <span>上限 ${plan.maxRounds || 9}</span>
                    </div>
                </button>
            `;
        }).join('');

        Assistant.renderActionButtons?.();
    }

    function renderSharingList(items) {
        const values = Array.isArray(items) ? items.filter(Boolean) : [];
        if (!values.length) {
            return '<div class="agent-helper-text">暂无额外情报共享规则。</div>';
        }

        return `<ul class="assistant-bullet-list">${values.map(item => `<li>${AgentConsole.utils.escapeHtml(item)}</li>`).join('')}</ul>`;
    }

    function renderRoundRoles(round) {
        const roles = Array.isArray(round.roles) ? round.roles : [];
        if (!roles.length) {
            return '<div class="agent-helper-text">这一轮还没分配角色。</div>';
        }

        return roles.map(role => `
            <div class="assistant-role-row">
                <div class="assistant-role-head">
                    <div class="assistant-role-head-main">
                        <strong>${AgentConsole.utils.escapeHtml(role.roleName || role.roleId)}</strong>
                        <span>${AgentConsole.utils.escapeHtml(role.roleId || '')}</span>
                    </div>
                    <div class="assistant-role-badges">
                        <span class="assistant-role-badge ${role.canWriteCode ? 'is-writer' : 'is-md-only'}">${role.canWriteCode ? 'writer' : 'md-only'}</span>
                        <span class="assistant-role-badge">${AgentConsole.utils.escapeHtml(role.outputKind || 'md')}</span>
                    </div>
                </div>
                <div class="assistant-role-body">
                    <div><strong>职责：</strong>${AgentConsole.utils.escapeHtml(role.responsibility || '—')}</div>
                    <div><strong>输入：</strong>${AgentConsole.utils.escapeHtml((role.inputArtifacts || []).join('，') || '—')}</div>
                    <div><strong>输出：</strong>${AgentConsole.utils.escapeHtml(role.outputArtifact || '—')}</div>
                    ${role.collaborationNotes ? `<div><strong>共享：</strong>${AgentConsole.utils.escapeHtml(role.collaborationNotes)}</div>` : ''}
                </div>
            </div>
        `).join('');
    }

    function renderRounds(plan) {
        const rounds = Array.isArray(plan?.rounds) ? [...plan.rounds].sort((a, b) => (a.roundNumber || 0) - (b.roundNumber || 0)) : [];
        if (!rounds.length) {
            return '<div class="workspace-empty-state">这份方案还没有轮次。可以重新生成，或者稍后补充。</div>';
        }

        return `
            <div class="assistant-round-grid">
                ${rounds.map((round, index) => {
                    const isPredicted = index > 0;
                    const canMoveUp = index > 0;
                    const canMoveDown = index < rounds.length - 1;
                    const maxActiveRoles = Number.isFinite(round.maxActiveRoles) ? round.maxActiveRoles : 3;
                    const maxWriters = Number.isFinite(round.maxWriters) ? round.maxWriters : 1;
                    return `
                        <article class="assistant-round-card" draggable="true" data-round-id="${AgentConsole.utils.escapeHtml(round.roundId)}">
                            <div class="assistant-round-card-header">
                                <div>
                                    <div class="agent-list-item-title">Round ${String(round.roundNumber || index + 1).padStart(2, '0')} · ${AgentConsole.utils.escapeHtml(round.title || '未命名轮次')}</div>
                                    <div class="agent-meta-row">
                                        <span class="agent-badge ${isPredicted ? 'neutral' : 'running'}">${isPredicted ? '预测' : '当前优先'}</span>
                                        <span>${AgentConsole.utils.escapeHtml(round.executionMode || 'sequential')}</span>
                                        <span>${round.requiresCodeChanges ? '允许改代码' : '以分析/交付为主'}</span>
                                        <span>${round.requiresVerification ? '建议验证' : '暂不强制验证'}</span>
                                        <span>最多角色 ${maxActiveRoles}</span>
                                        <span>最多写入 ${maxWriters}</span>
                                    </div>
                                </div>
                                <div class="surface-card-actions">
                                    <button class="editor-btn-secondary" type="button" data-assistant-round-move="up" data-round-id="${AgentConsole.utils.escapeHtml(round.roundId)}" ${canMoveUp ? '' : 'disabled'}>上移</button>
                                    <button class="editor-btn-secondary" type="button" data-assistant-round-move="down" data-round-id="${AgentConsole.utils.escapeHtml(round.roundId)}" ${canMoveDown ? '' : 'disabled'}>下移</button>
                                    <button class="editor-btn-secondary" type="button" data-assistant-round-delete="${AgentConsole.utils.escapeHtml(round.roundId)}">删除</button>
                                </div>
                            </div>
                            <div class="assistant-round-block">
                                <div><strong>目标：</strong>${AgentConsole.utils.escapeHtml(round.objective || '—')}</div>
                                <div><strong>完成条件：</strong>${AgentConsole.utils.escapeHtml(round.completionCriteria || '—')}</div>
                                <div><strong>交接：</strong>${AgentConsole.utils.escapeHtml(round.handoffNotes || '—')}</div>
                                <div><strong>交付物：</strong>${AgentConsole.utils.escapeHtml((round.deliverables || []).join('，') || '—')}</div>
                            </div>
                            <div class="assistant-round-roles">
                                ${renderRoundRoles(round)}
                            </div>
                        </article>
                    `;
                }).join('')}
            </div>
        `;
    }

    function renderSelectedPlan() {
        const container = document.getElementById('assistantPlanDetail');
        if (!container) {
            return;
        }

        const plan = Assistant.getSelectedPlan?.();
        if (!plan) {
            container.innerHTML = '<div class="workspace-empty-state">先从左侧选择一个方案，或者直接生成新的 AI 助手方案。</div>';
            Assistant.renderActionButtons?.();
            return;
        }

        container.innerHTML = `
            <div class="agent-detail-shell">
                <div class="agent-hero-card">
                    <div>
                        <div class="agent-eyebrow">AI 助手方案</div>
                        <h2>${AgentConsole.utils.escapeHtml(plan.title || '未命名方案')}</h2>
                        <p>${AgentConsole.utils.escapeHtml(plan.goal || '未填写目标')}</p>
                        <div class="agent-helper-text">策略摘要：${AgentConsole.utils.escapeHtml(plan.strategySummary || plan.summary || '—')}</div>
                        <div class="agent-helper-text">全自动：${plan.fullAutoEnabled ? '开启' : '关闭'} · 每批 ${plan.planningBatchSize || 3} 轮 · 最多 ${plan.maxRounds || 9} 轮 · 首批展示 ${plan.initialRoundCount || 3} 轮</div>
                    </div>
                    <div class="agent-hero-actions">
                        <button class="editor-btn-primary" type="button" id="assistantSavePlanInline">保存方案</button>
                        <button class="editor-btn-secondary" type="button" id="assistantCreateRunInline">按方案创建 Run</button>
                    </div>
                </div>

                <section class="assistant-plan-summary-grid">
                    <div class="settings-section">
                        <div class="section-title-row"><h3>情报共享协议</h3></div>
                        ${renderSharingList(plan.sharingProtocol)}
                    </div>
                    <div class="settings-section">
                        <div class="section-title-row"><h3>沉淀 skill</h3></div>
                        ${renderSharingList(plan.skillDirectives)}
                        ${plan.skillFilePath ? `<div class="agent-helper-text">skill 文件：<code>${AgentConsole.utils.escapeHtml(plan.skillFilePath)}</code></div>` : ''}
                    </div>
                </section>

                <section class="settings-section">
                    <div class="section-title-row"><h3>当前轮与后续预测</h3></div>
                    <div class="agent-helper-text">默认只把第一轮视为当前优先轮，后两轮是预测。每轮结束后，超管应滚动设计下一轮，而不是把 9 轮一口气写死。</div>
                    ${renderRounds(plan)}
                </section>
            </div>
        `;

        Assistant.renderActionButtons?.();
    }

    window.AgentConsole.Assistant = Object.assign(window.AgentConsole.Assistant || {}, {
        renderPlansList,
        renderSelectedPlan
    });
})();
