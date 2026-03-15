(function () {
    const AgentConsole = window.AgentConsole;
    const V3_ROLE_HINTS = {
        helmsman: 'AI助手V3 主线角色：像项目经理 / 产品负责人一样先给出完整整体计划，阶段数必须在 1~4 之间，但由 AI 自己判断；之后定义当前阶段、验收、边界与任务卡。除非有明确硬约束，否则不要替子线程把技术方案写死。',
        pathfinder: 'AI助手V3 子线角色：知道总目标、整体计划、当前阶段和红线，在任务卡约束内自主决定实现与 UI/UX 落地，并把本轮主动微调如实汇报给主线裁决。',
        'redteam-wingman': 'AI助手V3 助攻角色：只在用户勾选时一次性出手，强化插嘴里的风险、边界和反例提醒，供主线参考，但不改写用户原话，更不常驻参与每轮。'
    };

    function getV3RoleHint(roleId) {
        return V3_ROLE_HINTS[String(roleId || '').trim().toLowerCase()] || null;
    }

    function getSelectedRole() {
        return AgentConsole.state.roles.find(role => role.roleId === AgentConsole.state.selectedRoleId) || null;
    }

    function persistCurrentRoleEditor() {
        const editor = document.getElementById('roleEditor');
        if (!editor) {
            return;
        }

        const root = editor.querySelector('[data-role-editor-root]');
        if (!root) {
            return;
        }

        const roleId = root.getAttribute('data-role-id');
        const role = AgentConsole.state.roles.find(item => item.roleId === roleId);
        if (!role) {
            return;
        }

        role.roleId = root.querySelector('[name="roleId"]')?.value.trim() || role.roleId;
        role.name = root.querySelector('[name="name"]')?.value.trim() || '';
        role.description = root.querySelector('[name="description"]')?.value.trim() || null;
        role.icon = root.querySelector('[name="icon"]')?.value.trim() || null;
        role.model = root.querySelector('[name="model"]')?.value.trim() || AgentConsole.state.settings?.defaultModel || 'gpt-5.4';
        role.workspacePath = root.querySelector('[name="workspacePath"]')?.value.trim() || '.';
        role.promptTemplate = root.querySelector('[name="promptTemplate"]')?.value || '';
        role.allowAllTools = !!root.querySelector('[name="allowAllTools"]')?.checked;
        role.allowAllPaths = !!root.querySelector('[name="allowAllPaths"]')?.checked;
        role.allowAllUrls = !!root.querySelector('[name="allowAllUrls"]')?.checked;
        role.allowedTools = AgentConsole.utils.fromMultilineList(root.querySelector('[name="allowedTools"]')?.value || '');
        role.deniedTools = AgentConsole.utils.fromMultilineList(root.querySelector('[name="deniedTools"]')?.value || '');
        role.allowedPaths = AgentConsole.utils.fromMultilineList(root.querySelector('[name="allowedPaths"]')?.value || '');
        role.allowedUrls = AgentConsole.utils.fromMultilineList(root.querySelector('[name="allowedUrls"]')?.value || '');
        role.environmentVariables = AgentConsole.utils.linesToDictionary(root.querySelector('[name="environmentVariables"]')?.value || '');

        AgentConsole.state.selectedRoleId = role.roleId;
    }

    function renderList() {
        const container = document.getElementById('rolesList');
        if (!container) {
            return;
        }

        if (!AgentConsole.state.roles.length) {
            container.innerHTML = '<div class="workspace-empty-inline">还没有角色，先点左上角 ➕。</div>';
            return;
        }

        container.innerHTML = AgentConsole.state.roles.map(role => {
            const isActive = role.roleId === AgentConsole.state.selectedRoleId;
            return `
                <button class="agent-list-item ${isActive ? 'active' : ''}" type="button" data-role-select="${AgentConsole.utils.escapeHtml(role.roleId)}">
                    <div class="agent-list-item-title">${AgentConsole.utils.escapeHtml(role.icon || '🎭')} ${AgentConsole.utils.escapeHtml(role.name || role.roleId)}</div>
                    <div class="agent-list-item-subtitle">${AgentConsole.utils.escapeHtml(AgentConsole.utils.trimText(role.description || role.promptTemplate || '未填写说明', 96))}</div>
                </button>
            `;
        }).join('');
    }

    function renderEditor() {
        const container = document.getElementById('roleEditor');
        if (!container) {
            return;
        }

        const role = getSelectedRole();
        if (!role) {
            container.innerHTML = '<div class="workspace-empty-state">选择一个角色开始编辑。</div>';
            return;
        }

        const v3RoleHint = getV3RoleHint(role.roleId);

        container.innerHTML = `
            <div class="agent-detail-shell" data-role-editor-root data-role-id="${AgentConsole.utils.escapeHtml(role.roleId)}">
                <div class="agent-hero-card">
                    <div>
                        <div class="agent-eyebrow">Role Profile</div>
                        <h2>${AgentConsole.utils.escapeHtml(role.icon || '🎭')} ${AgentConsole.utils.escapeHtml(role.name || role.roleId)}</h2>
                        <p>${AgentConsole.utils.escapeHtml(role.description || '这里定义角色职责、工作路径、权限边界和提示词模板。')}</p>
                        ${v3RoleHint ? `<div class="agent-helper-text" style="margin-top:10px;">${AgentConsole.utils.escapeHtml(v3RoleHint)}</div>` : ''}
                    </div>
                    <div class="agent-hero-actions">
                        <button class="editor-btn-secondary" type="button" data-role-action="duplicate">复制角色</button>
                        <button class="editor-btn-secondary" type="button" data-role-action="delete">删除角色</button>
                    </div>
                </div>

                <div class="settings-section">
                    <div class="section-title-row"><h3>基础信息</h3></div>
                    <div class="settings-grid settings-grid-2">
                        <div>
                            <label class="agent-label">Role ID</label>
                            <input class="agent-input" name="roleId" type="text" value="${AgentConsole.utils.escapeHtml(role.roleId)}">
                        </div>
                        <div>
                            <label class="agent-label">显示名称</label>
                            <input class="agent-input" name="name" type="text" value="${AgentConsole.utils.escapeHtml(role.name || '')}">
                        </div>
                        <div>
                            <label class="agent-label">图标</label>
                            <input class="agent-input" name="icon" type="text" value="${AgentConsole.utils.escapeHtml(role.icon || '')}" placeholder="例如 🤖 / 🧪 / 📝">
                        </div>
                        <div>
                            <label class="agent-label">模型</label>
                            <input class="agent-input" name="model" type="text" value="${AgentConsole.utils.escapeHtml(role.model || AgentConsole.state.settings?.defaultModel || 'gpt-5.4')}">
                        </div>
                    </div>
                    <label class="agent-label">角色说明</label>
                    <textarea class="agent-textarea" name="description" placeholder="这个角色在协作里负责什么？">${AgentConsole.utils.escapeHtml(role.description || '')}</textarea>
                </div>

                <div class="settings-section">
                    <div class="section-title-row"><h3>工作上下文</h3></div>
                    <label class="agent-label">工作路径</label>
                    <input class="agent-input" name="workspacePath" type="text" value="${AgentConsole.utils.escapeHtml(role.workspacePath || '.')}" placeholder="留空表示 .">
                    <div class="agent-helper-text">相对于 Run 仓库根的相对路径；留空就表示 <code>.</code>。注意：<code>copilot</code> 实际总是从 Run 仓库根启动，这里定义的是该角色默认关注/作用的子目录范围。</div>
                    <label class="agent-label">Prompt 模板</label>
                    <textarea class="agent-textarea agent-textarea-lg textarea-code" name="promptTemplate" placeholder="支持 {{goal}} / {{roleName}} / {{roleDescription}} / {{runTitle}} / {{peerRoles}}">${AgentConsole.utils.escapeHtml(role.promptTemplate || '')}</textarea>
                    <div class="agent-helper-text">常用变量：<code>{{goal}}</code>、<code>{{roleName}}</code>、<code>{{roleDescription}}</code>、<code>{{runTitle}}</code>。V3 角色还支持 <code>{{taskCard}}</code>、<code>{{reviewDirective}}</code>、<code>{{partnerName}}</code>、<code>{{partnerSummary}}</code>、<code>{{roundNumber}}</code>、<code>{{reviewFocus}}</code>、<code>{{workspaceName}}</code>、<code>{{stagePlanSummary}}</code>、<code>{{currentStage}}</code>、<code>{{currentStageGoal}}</code>、<code>{{architectureGuardrails}}</code>、<code>{{changeDecision}}</code>。</div>
                </div>

                <div class="settings-section">
                    <div class="section-title-row"><h3>权限边界</h3></div>
                    <div class="pill-row">
                        <label class="agent-checkbox-row"><input name="allowAllTools" type="checkbox" ${role.allowAllTools ? 'checked' : ''}> <span>允许所有工具</span></label>
                        <label class="agent-checkbox-row"><input name="allowAllPaths" type="checkbox" ${role.allowAllPaths ? 'checked' : ''}> <span>允许所有目录</span></label>
                        <label class="agent-checkbox-row"><input name="allowAllUrls" type="checkbox" ${role.allowAllUrls ? 'checked' : ''}> <span>允许所有 URL</span></label>
                    </div>
                    <div class="agent-helper-text">想要“直接执行、不弹窗，但路径绝不越界”：推荐保持 <code>允许所有工具=开</code>、<code>允许所有目录=关</code>。留空工作路径就等于 <code>.</code>，而 <code>copilot</code> 会从 Run 仓库根启动；这样工具放开，但目录仍锁在当前 Run 根目录树内。</div>
                    <div class="settings-grid settings-grid-3">
                        <div>
                            <label class="agent-label">允许工具</label>
                            <textarea class="agent-textarea textarea-code" name="allowedTools" placeholder="一行一个工具名">${AgentConsole.utils.escapeHtml(AgentConsole.utils.toMultilineList(role.allowedTools))}</textarea>
                        </div>
                        <div>
                            <label class="agent-label">拒绝工具</label>
                            <textarea class="agent-textarea textarea-code" name="deniedTools" placeholder="一行一个工具名">${AgentConsole.utils.escapeHtml(AgentConsole.utils.toMultilineList(role.deniedTools))}</textarea>
                        </div>
                        <div>
                            <label class="agent-label">允许 URL</label>
                            <textarea class="agent-textarea textarea-code" name="allowedUrls" placeholder="一行一个 URL 或域名模式">${AgentConsole.utils.escapeHtml(AgentConsole.utils.toMultilineList(role.allowedUrls))}</textarea>
                        </div>
                    </div>
                    <label class="agent-label">允许目录</label>
                    <textarea class="agent-textarea textarea-code" name="allowedPaths" placeholder="一行一个路径">${AgentConsole.utils.escapeHtml(AgentConsole.utils.toMultilineList(role.allowedPaths))}</textarea>
                </div>

                <div class="settings-section">
                    <div class="section-title-row"><h3>环境变量</h3></div>
                    <label class="agent-label">KEY=VALUE</label>
                    <textarea class="agent-textarea textarea-code" name="environmentVariables" placeholder="例如\nNODE_ENV=development\nCI=true">${AgentConsole.utils.escapeHtml(AgentConsole.utils.dictionaryToLines(role.environmentVariables))}</textarea>
                    <div class="agent-helper-text">一行一个环境变量，保存时会解析成对象。小心别把秘密直接写得满屏乱飞——变量是老实的，日志不是。</div>
                </div>
            </div>
        `;
    }

    function addRole() {
        persistCurrentRoleEditor();
        const newRoleId = `role-${Date.now()}`;
        AgentConsole.state.roles.push({
            roleId: newRoleId,
            name: 'New Role',
            description: '请描述这个角色的职责。',
            icon: '🤖',
            promptTemplate: '项目目标：{{goal}}。你的角色：{{roleName}}。请先阅读工作区，再开始推进，并在末尾用 STATUS / SUMMARY / NEXT 三行总结。',
            model: AgentConsole.state.settings?.defaultModel || 'gpt-5.4',
            allowAllTools: true,
            allowAllPaths: false,
            allowAllUrls: false,
            workspacePath: '.',
            allowedUrls: [],
            allowedTools: [],
            deniedTools: [],
            allowedPaths: [],
            environmentVariables: {}
        });
        AgentConsole.state.selectedRoleId = newRoleId;
        renderList();
        renderEditor();
        window.AgentConsole?.Runs?.renderRoleOptions();
    }

    function duplicateRole() {
        persistCurrentRoleEditor();
        const role = getSelectedRole();
        if (!role) {
            return;
        }

        const clone = JSON.parse(JSON.stringify(role));
        clone.roleId = `${role.roleId}-copy-${Date.now().toString().slice(-4)}`;
        clone.name = `${role.name} Copy`;
        AgentConsole.state.roles.push(clone);
        AgentConsole.state.selectedRoleId = clone.roleId;
        renderList();
        renderEditor();
        window.AgentConsole?.Runs?.renderRoleOptions();
    }

    function deleteRole() {
        const role = getSelectedRole();
        if (!role) {
            return;
        }

        if (!confirm(`确认删除角色 “${role.name || role.roleId}” 吗？`)) {
            return;
        }

        AgentConsole.state.roles = AgentConsole.state.roles.filter(item => item.roleId !== role.roleId);
        AgentConsole.state.selectedRoleId = AgentConsole.state.roles[0]?.roleId || null;
        renderList();
        renderEditor();
        window.AgentConsole?.Runs?.renderRoleOptions();
    }

    async function saveRoles() {
        try {
            persistCurrentRoleEditor();
            const payload = {
                settings: AgentConsole.state.settings || {},
                roles: AgentConsole.state.roles
            };
            const savedCatalog = await AgentConsole.api.saveRoles(payload);
            AgentConsole.state.roleCatalog = savedCatalog;
            AgentConsole.state.roles = Array.isArray(savedCatalog.roles) ? savedCatalog.roles : [];
            AgentConsole.state.settings = savedCatalog.settings || AgentConsole.state.settings;
            if (!AgentConsole.state.roles.some(role => role.roleId === AgentConsole.state.selectedRoleId)) {
                AgentConsole.state.selectedRoleId = AgentConsole.state.roles[0]?.roleId || null;
            }
            renderList();
            renderEditor();
            window.AgentConsole?.Runs?.renderRoleOptions();
            window.AgentConsole?.Settings?.render();
            AgentConsole.utils.showToast('角色配置已保存');
        } catch (error) {
            console.error(error);
            AgentConsole.utils.showToast(error.message || '保存角色失败', 'error');
        }
    }

    function bindStaticEvents() {
        document.getElementById('btnAddRole')?.addEventListener('click', addRole);
        document.getElementById('btnSaveRoles')?.addEventListener('click', saveRoles);

        document.getElementById('rolesList')?.addEventListener('click', event => {
            const button = event.target.closest('[data-role-select]');
            if (!button) {
                return;
            }

            persistCurrentRoleEditor();
            AgentConsole.state.selectedRoleId = button.getAttribute('data-role-select');
            renderList();
            renderEditor();
        });

        document.getElementById('roleEditor')?.addEventListener('click', event => {
            const action = event.target.closest('[data-role-action]')?.getAttribute('data-role-action');
            if (!action) {
                return;
            }

            if (action === 'duplicate') {
                duplicateRole();
            } else if (action === 'delete') {
                deleteRole();
            }
        });
    }

    AgentConsole.Roles = {
        bindStaticEvents,
        renderList,
        renderEditor,
        persistCurrentRoleEditor,
        saveRoles
    };
})();
