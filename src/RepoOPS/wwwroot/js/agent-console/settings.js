(function () {
    const AgentConsole = window.AgentConsole;

    const settingTabs = [
        { id: 'general', label: '常规' },
        { id: 'models', label: '模型' },
        { id: 'verification', label: '构建/检查' },
        { id: 'workspace', label: '工作区' },
        { id: 'orchestration', label: '调度' }
    ];

    function getSettings() {
        AgentConsole.state.settings ||= {};
        return AgentConsole.state.settings;
    }

    function numberValue(name, fallback) {
        const root = document.getElementById('settingsEditor');
        const input = root?.querySelector(`[name="${name}"]`);
        const parsed = Number.parseInt(input?.value || '', 10);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function checkedValue(name, fallback = false) {
        const root = document.getElementById('settingsEditor');
        const input = root?.querySelector(`[name="${name}"]`);
        return input ? !!input.checked : fallback;
    }

    function textValue(name, fallback = '') {
        const root = document.getElementById('settingsEditor');
        const input = root?.querySelector(`[name="${name}"]`);
        return input ? input.value.trim() : fallback;
    }

    function persistFromEditor() {
        const editor = document.getElementById('settingsEditor');
        if (!editor || !editor.querySelector('[data-settings-root]')) {
            return;
        }

        const settings = getSettings();
        settings.supervisorModel = textValue('supervisorModel', settings.supervisorModel || 'gpt-5.4') || 'gpt-5.4';
        settings.supervisorPromptPrefix = textValue('supervisorPromptPrefix', settings.supervisorPromptPrefix || '') || null;
        settings.roleProposalPromptPrefix = textValue('roleProposalPromptPrefix', settings.roleProposalPromptPrefix || '') || null;
        settings.defaultModel = textValue('defaultModel', settings.defaultModel || 'gpt-5.4') || 'gpt-5.4';
        settings.defaultMaxAutoSteps = numberValue('defaultMaxAutoSteps', settings.defaultMaxAutoSteps || 6);
        settings.defaultAutoPilotEnabled = checkedValue('defaultAutoPilotEnabled', settings.defaultAutoPilotEnabled);
        settings.maxConcurrentWorkers = numberValue('maxConcurrentWorkers', settings.maxConcurrentWorkers || 4);
        settings.workerTimeoutMinutes = numberValue('workerTimeoutMinutes', settings.workerTimeoutMinutes || 30);
        settings.allowWorkerPermissionRequests = checkedValue('allowWorkerPermissionRequests', settings.allowWorkerPermissionRequests ?? true);
        settings.enableYoloMode = checkedValue('enableYoloMode', settings.enableYoloMode ?? false);
        settings.defaultVerificationCommand = textValue('defaultVerificationCommand', settings.defaultVerificationCommand || '') || null;
        settings.outputBufferMaxChars = numberValue('outputBufferMaxChars', settings.outputBufferMaxChars || 12000);
        settings.decisionHistoryLimit = numberValue('decisionHistoryLimit', settings.decisionHistoryLimit || 40);
        settings.defaultWorkspaceRoot = textValue('defaultWorkspaceRoot', settings.defaultWorkspaceRoot || '') || null;
        settings.environmentVariables = AgentConsole.utils.linesToDictionary(textValue('environmentVariables', ''));
        settings.enableAttentionTracking = checkedValue('enableAttentionTracking', settings.enableAttentionTracking);
        settings.autoCreateDefaultLanes = checkedValue('autoCreateDefaultLanes', settings.autoCreateDefaultLanes);
        settings.enableCoordinatorSurface = checkedValue('enableCoordinatorSurface', settings.enableCoordinatorSurface);
        settings.enableVerificationSurface = checkedValue('enableVerificationSurface', settings.enableVerificationSurface);
        settings.autoAcknowledgeAttentionOnFocus = checkedValue('autoAcknowledgeAttentionOnFocus', settings.autoAcknowledgeAttentionOnFocus);
        settings.showCompletedSurfaces = checkedValue('showCompletedSurfaces', settings.showCompletedSurfaces);
        settings.suggestFocusOnAttention = checkedValue('suggestFocusOnAttention', settings.suggestFocusOnAttention);
        settings.maxAttentionEvents = numberValue('maxAttentionEvents', settings.maxAttentionEvents || 100);
        settings.defaultLayoutMode = textValue('defaultLayoutMode', settings.defaultLayoutMode || 'lanes') || 'lanes';
        settings.agentLaneName = textValue('agentLaneName', settings.agentLaneName || 'Agents') || 'Agents';
        settings.controlLaneName = textValue('controlLaneName', settings.controlLaneName || 'Coordinator') || 'Coordinator';
        settings.verificationLaneName = textValue('verificationLaneName', settings.verificationLaneName || 'Verification') || 'Verification';
        settings.defaultRunDetailTab = textValue('defaultRunDetailTab', settings.defaultRunDetailTab || 'workspace') || 'workspace';
        settings.defaultSettingsTab = textValue('defaultSettingsTab', settings.defaultSettingsTab || 'orchestration') || 'orchestration';
        AgentConsole.state.settings = settings;
    }

    function render() {
        const container = document.getElementById('settingsEditor');
        if (!container) {
            return;
        }

        const settings = getSettings();
        const activeTab = AgentConsole.state.activeSettingsTab || settings.defaultSettingsTab || 'orchestration';
        AgentConsole.state.activeSettingsTab = activeTab;

        container.innerHTML = `
            <div class="agent-detail-shell" data-settings-root>
                <div class="agent-hero-card">
                    <div>
                        <div class="agent-eyebrow">调度设置</div>
                        <h2>⚙️ 调度与执行参数</h2>
                        <p>这里把关键开关都摊开说清楚：先让规则透明，再决定哪些值得自动化。顺手也把英文按钮们请去学了中文。</p>
                    </div>
                    <div class="agent-hero-actions">
                        <button class="editor-btn-secondary" type="button" data-settings-action="reload">重新加载</button>
                        <button class="editor-btn-primary" type="button" data-settings-action="save">保存设置</button>
                    </div>
                </div>

                <div class="settings-tabs">
                    ${settingTabs.map(tab => `
                        <button class="settings-tab ${tab.id === activeTab ? 'active' : ''}" type="button" data-settings-tab="${tab.id}">${tab.label}</button>
                    `).join('')}
                </div>

                ${renderTabContent(activeTab, settings)}
            </div>
        `;
    }

    function renderTabContent(tab, settings) {
        switch (tab) {
            case 'general':
                return `
                    <div class="settings-section">
                        <div class="section-title-row"><h3>默认行为</h3></div>
                        <div class="settings-grid settings-grid-3">
                            <div>
                                <label class="agent-label">默认自动推进轮次</label>
                                <input class="agent-input" name="defaultMaxAutoSteps" type="number" min="1" value="${settings.defaultMaxAutoSteps ?? 6}">
                            </div>
                            <div>
                                <label class="agent-label">最大并发 Worker</label>
                                <input class="agent-input" name="maxConcurrentWorkers" type="number" min="1" value="${settings.maxConcurrentWorkers ?? 4}">
                            </div>
                            <div>
                                <label class="agent-label">Worker 超时（分钟）</label>
                                <input class="agent-input" name="workerTimeoutMinutes" type="number" min="1" value="${settings.workerTimeoutMinutes ?? 30}">
                            </div>
                            <div>
                                <label class="agent-label">输出缓冲最大字符</label>
                                <input class="agent-input" name="outputBufferMaxChars" type="number" min="1000" step="1000" value="${settings.outputBufferMaxChars ?? 12000}">
                            </div>
                            <div>
                                <label class="agent-label">决策历史保留条数</label>
                                <input class="agent-input" name="decisionHistoryLimit" type="number" min="10" value="${settings.decisionHistoryLimit ?? 40}">
                            </div>
                        </div>
                        <div class="pill-row">
                            <label class="agent-checkbox-row"><input name="defaultAutoPilotEnabled" type="checkbox" ${settings.defaultAutoPilotEnabled ? 'checked' : ''}> <span>新建 Run 默认开启自动推进</span></label>
                            <label class="agent-checkbox-row"><input name="allowWorkerPermissionRequests" type="checkbox" ${(settings.allowWorkerPermissionRequests ?? true) ? 'checked' : ''}> <span>执行时不强制禁用权限申请（仅外层环境支持交互审批时有效）</span></label>
                            <label class="agent-checkbox-row"><input name="enableYoloMode" type="checkbox" ${settings.enableYoloMode ? 'checked' : ''}> <span>⚠️ YOLO 模式（<code>--yolo</code>：一键开启全部权限，跳过所有工具/路径/URL 确认）</span></label>
                        </div>
                        <div class="agent-helper-text">注意：这不是 RepoOPS 自己的弹窗开关。当前通过后台 <code>gh</code> 子进程执行时，RepoOPS 本身没有内置授权弹窗；这里仅表示是否追加 <code>--no-ask-user</code>。只有外层运行环境本身支持交互审批时，才可能真的出现确认流程，否则仍会报"无法申请权限"。<br>YOLO 模式开启后，会追加 <code>--yolo</code> 代替上述所有单独权限标志，copilot 将跳过一切工具/文件/网络权限确认。<strong>仅建议在受信环境下测试时使用。</strong></div>
                    </div>
                `;
            case 'models':
                return `
                    <div class="settings-section">
                        <div class="section-title-row"><h3>模型与提示词</h3></div>
                        <div class="settings-grid settings-grid-2">
                            <div>
                                <label class="agent-label">Supervisor 模型</label>
                                <input class="agent-input" name="supervisorModel" type="text" value="${AgentConsole.utils.escapeHtml(settings.supervisorModel || 'gpt-5.4')}">
                            </div>
                            <div>
                                <label class="agent-label">Worker 默认模型</label>
                                <input class="agent-input" name="defaultModel" type="text" value="${AgentConsole.utils.escapeHtml(settings.defaultModel || 'gpt-5.4')}">
                            </div>
                        </div>
                        <label class="agent-label">Supervisor 提示词前缀</label>
                        <textarea class="agent-textarea agent-textarea-lg textarea-code" name="supervisorPromptPrefix" placeholder="可在这里塞固定调度规则、输出约束、团队协作标准">${AgentConsole.utils.escapeHtml(settings.supervisorPromptPrefix || '')}</textarea>
                        <label class="agent-label">角色建议提示词前缀</label>
                        <textarea class="agent-textarea textarea-code" name="roleProposalPromptPrefix" placeholder="控制 AI 建议角色时的风格、数量、命名约束，以及是否偏向让角色自行构建/测试">${AgentConsole.utils.escapeHtml(settings.roleProposalPromptPrefix || '')}</textarea>
                    </div>
                `;
            case 'verification':
                return `
                    <div class="settings-section">
                        <div class="section-title-row"><h3>补充构建/检查</h3></div>
                        <label class="agent-label">默认构建/检查命令</label>
                        <textarea class="agent-textarea textarea-code" name="defaultVerificationCommand" placeholder="例如 dotnet build .\\repoOPS.sln">${AgentConsole.utils.escapeHtml(settings.defaultVerificationCommand || '')}</textarea>
                        <div class="agent-helper-text">这里是兜底用的统一检查命令，不是要求你一定单独做一个“验证角色”。更推荐让执行角色自己编译、测试；这里负责最后补一刀确认。</div>
                    </div>
                `;
            case 'workspace':
                return `
                    <div class="settings-section">
                        <div class="section-title-row"><h3>工作区与环境</h3></div>
                        <label class="agent-label">默认仓库根目录</label>
                        <input class="agent-input" name="defaultWorkspaceRoot" type="text" value="${AgentConsole.utils.escapeHtml(settings.defaultWorkspaceRoot || '')}" placeholder="留空表示当前仓库根（.）；填写后作为默认执行根目录">
                        <div class="agent-helper-text">这里是 Run 的仓库根 / 执行根。留空就按当前仓库根 <code>.</code> 处理；后续 worker 和 coordinator 的 <code>gh</code> 都会从这个根目录启动，所有相对路径也都相对于这里计算。</div>
                        <label class="agent-label">全局环境变量（KEY=VALUE）</label>
                        <textarea class="agent-textarea agent-textarea-lg textarea-code" name="environmentVariables" placeholder="例如\nGH_DEBUG=api\nDOTNET_CLI_TELEMETRY_OPTOUT=1">${AgentConsole.utils.escapeHtml(AgentConsole.utils.dictionaryToLines(settings.environmentVariables))}</textarea>
                    </div>
                `;
            case 'orchestration':
            default:
                return `
                    <div class="settings-section">
                        <div class="section-title-row"><h3>协作界面 / 提醒事项 / 布局</h3></div>
                        <div class="pill-row">
                            <label class="agent-checkbox-row"><input name="enableAttentionTracking" type="checkbox" ${settings.enableAttentionTracking ? 'checked' : ''}> <span>启用 Attention 跟踪</span></label>
                            <label class="agent-checkbox-row"><input name="autoCreateDefaultLanes" type="checkbox" ${settings.autoCreateDefaultLanes ? 'checked' : ''}> <span>自动创建默认 Lane</span></label>
                            <label class="agent-checkbox-row"><input name="enableCoordinatorSurface" type="checkbox" ${settings.enableCoordinatorSurface ? 'checked' : ''}> <span>启用调度面板</span></label>
                            <label class="agent-checkbox-row"><input name="enableVerificationSurface" type="checkbox" ${settings.enableVerificationSurface ? 'checked' : ''}> <span>启用构建/检查面板</span></label>
                            <label class="agent-checkbox-row"><input name="autoAcknowledgeAttentionOnFocus" type="checkbox" ${settings.autoAcknowledgeAttentionOnFocus ? 'checked' : ''}> <span>聚焦时自动已读 Attention</span></label>
                            <label class="agent-checkbox-row"><input name="showCompletedSurfaces" type="checkbox" ${settings.showCompletedSurfaces ? 'checked' : ''}> <span>显示已完成面板</span></label>
                            <label class="agent-checkbox-row"><input name="suggestFocusOnAttention" type="checkbox" ${settings.suggestFocusOnAttention ? 'checked' : ''}> <span>根据 Attention 建议焦点</span></label>
                        </div>
                        <div class="settings-grid settings-grid-3">
                            <div>
                                <label class="agent-label">最大提醒数量</label>
                                <input class="agent-input" name="maxAttentionEvents" type="number" min="10" value="${settings.maxAttentionEvents ?? 100}">
                            </div>
                            <div>
                                <label class="agent-label">默认布局模式</label>
                                <input class="agent-input" name="defaultLayoutMode" type="text" value="${AgentConsole.utils.escapeHtml(settings.defaultLayoutMode || 'lanes')}">
                            </div>
                            <div>
                                <label class="agent-label">默认 Run 详情页签</label>
                                <input class="agent-input" name="defaultRunDetailTab" type="text" value="${AgentConsole.utils.escapeHtml(settings.defaultRunDetailTab || 'workspace')}">
                            </div>
                            <div>
                                <label class="agent-label">默认 Settings 页签</label>
                                <input class="agent-input" name="defaultSettingsTab" type="text" value="${AgentConsole.utils.escapeHtml(settings.defaultSettingsTab || 'orchestration')}">
                            </div>
                            <div>
                                <label class="agent-label">执行角色分区名称</label>
                                <input class="agent-input" name="agentLaneName" type="text" value="${AgentConsole.utils.escapeHtml(settings.agentLaneName || '执行角色')}">
                            </div>
                            <div>
                                <label class="agent-label">调度分区名称</label>
                                <input class="agent-input" name="controlLaneName" type="text" value="${AgentConsole.utils.escapeHtml(settings.controlLaneName || '调度台')}">
                            </div>
                            <div>
                                <label class="agent-label">构建检查分区名称</label>
                                <input class="agent-input" name="verificationLaneName" type="text" value="${AgentConsole.utils.escapeHtml(settings.verificationLaneName || '构建检查')}">
                            </div>
                        </div>
                    </div>
                `;
        }
    }

    async function save() {
        try {
            persistFromEditor();
            const saved = await AgentConsole.api.saveSettings(getSettings());
            AgentConsole.state.settings = saved;
            AgentConsole.state.activeSettingsTab = saved.defaultSettingsTab || AgentConsole.state.activeSettingsTab;
            render();
            AgentConsole.Runs?.renderSelectedRun();
            AgentConsole.Runs?.syncRunWorkspaceRootHint?.();
            AgentConsole.utils.showToast('设置已保存');
        } catch (error) {
            console.error(error);
            AgentConsole.utils.showToast(error.message || '保存设置失败', 'error');
        }
    }

    async function reload() {
        try {
            AgentConsole.state.settings = await AgentConsole.api.getSettings();
            AgentConsole.state.activeSettingsTab = AgentConsole.state.settings.defaultSettingsTab || AgentConsole.state.activeSettingsTab;
            render();
            AgentConsole.Runs?.syncRunWorkspaceRootHint?.();
            AgentConsole.utils.showToast('已重新加载设置');
        } catch (error) {
            console.error(error);
            AgentConsole.utils.showToast(error.message || '重新加载设置失败', 'error');
        }
    }

    function bindStaticEvents() {
        document.getElementById('btnSaveSettings')?.addEventListener('click', save);
        document.getElementById('settingsEditor')?.addEventListener('click', event => {
            const tab = event.target.closest('[data-settings-tab]')?.getAttribute('data-settings-tab');
            if (tab) {
                persistFromEditor();
                AgentConsole.state.activeSettingsTab = tab;
                render();
                return;
            }

            const action = event.target.closest('[data-settings-action]')?.getAttribute('data-settings-action');
            if (!action) {
                return;
            }

            if (action === 'save') {
                save();
            } else if (action === 'reload') {
                reload();
            }
        });
    }

    AgentConsole.Settings = {
        bindStaticEvents,
        persistFromEditor,
        render,
        save,
        reload
    };
})();
