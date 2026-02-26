// ===== RepoOPS Configuration Editor =====

const ConfigEditor = (() => {
    let currentConfig = null;
    let isOpen = false;

    function open() {
        if (isOpen) return;
        isOpen = true;
        loadAndShow();
    }

    function close() {
        isOpen = false;
        const overlay = document.getElementById('editorOverlay');
        if (overlay) overlay.remove();
    }

    async function loadAndShow() {
        try {
            const response = await fetch('/api/config');
            currentConfig = await response.json();
            render();
        } catch (err) {
            console.error('Failed to load config for editing:', err);
            showToast(I18n.t('loading.failed'), 'error');
            close();
        }
    }

    function render() {
        const old = document.getElementById('editorOverlay');
        if (old) old.remove();

        const overlay = document.createElement('div');
        overlay.id = 'editorOverlay';
        overlay.className = 'editor-overlay';
        overlay.innerHTML = buildEditorHTML();
        document.body.appendChild(overlay);

        overlay.querySelector('.editor-overlay-bg').onclick = close;
        overlay.querySelector('.editor-close-btn').onclick = close;
        overlay.querySelector('.editor-save-btn').onclick = save;
        overlay.querySelector('.editor-cancel-btn').onclick = close;
        overlay.querySelector('.editor-add-group-btn').onclick = addGroup;

        attachGroupEvents(overlay);

        I18n.applyTranslations();
    }

    function buildEditorHTML() {
        const t = I18n.t;
        let groupsHtml = '';
        if (currentConfig.groups) {
            currentConfig.groups.forEach((group, gi) => {
                groupsHtml += buildGroupHTML(group, gi);
            });
        }

        return `
        <div class="editor-overlay-bg"></div>
        <div class="editor-panel">
            <div class="editor-header">
                <h2 data-i18n="editor.title">${t('editor.title')}</h2>
                <button class="editor-close-btn btn-icon" title="Close">✕</button>
            </div>
            <div class="editor-body">
                <div class="editor-toolbar">
                    <button class="editor-add-group-btn editor-btn-add" data-i18n="editor.addGroup">${t('editor.addGroup')}</button>
                    <div class="editor-toolbar-right">
                        <button class="editor-cancel-btn editor-btn-secondary" data-i18n="editor.cancel">${t('editor.cancel')}</button>
                        <button class="editor-save-btn editor-btn-primary" data-i18n="editor.save">${t('editor.save')}</button>
                    </div>
                </div>
                <div class="editor-body-content">
                <div class="editor-section">
                    <h3 data-i18n="editor.global">${t('editor.global')}</h3>
                    <div class="editor-field">
                        <label data-i18n="editor.scriptsBasePath">${t('editor.scriptsBasePath')}</label>
                        <input type="text" id="editorScriptsBasePath"
                            value="${escapeAttr(currentConfig.scriptsBasePath || '')}"
                            data-i18n-placeholder="editor.scriptsBasePath.placeholder"
                            placeholder="${t('editor.scriptsBasePath.placeholder')}">
                    </div>
                    <div class="editor-field">
                        <label data-i18n="editor.defaultWorkingDir">${t('editor.defaultWorkingDir')}</label>
                        <input type="text" id="editorDefaultWorkDir"
                            value="${escapeAttr(currentConfig.defaultWorkingDirectory || '')}"
                            data-i18n-placeholder="editor.defaultWorkingDir.placeholder"
                            placeholder="${t('editor.defaultWorkingDir.placeholder')}">
                    </div>
                </div>

                <div class="editor-section">
                    <div class="editor-section-header">
                        <h3 data-i18n="editor.groups">${t('editor.groups')}</h3>
                    </div>
                    <div id="editorGroups">
                        ${groupsHtml}
                    </div>
                </div>
                </div>
            </div>
        </div>`;
    }

    function buildGroupHTML(group, gi) {
        const t = I18n.t;
        let tasksHtml = '';
        if (group.tasks) {
            group.tasks.forEach((task, ti) => {
                tasksHtml += buildTaskHTML(task, gi, ti);
            });
        }

        return `
        <div class="editor-group" data-group-index="${gi}">
            <div class="editor-group-header">
                <div class="editor-group-fields">
                    <button class="editor-group-collapse-btn" title="折叠/展开">▼</button>
                    <div class="editor-field-inline">
                        <input type="text" class="editor-input-icon group-icon" value="${escapeAttr(group.icon || '')}"
                            data-i18n-placeholder="editor.groupIcon.placeholder"
                            placeholder="${t('editor.groupIcon.placeholder')}">
                    </div>
                    <div class="editor-field-inline editor-field-grow">
                        <input type="text" class="group-name" value="${escapeAttr(group.name || '')}"
                            data-i18n-placeholder="editor.groupName.placeholder"
                            placeholder="${t('editor.groupName.placeholder')}">
                    </div>
                    <button class="editor-btn-delete delete-group-btn" data-i18n-title="editor.deleteGroup" title="${t('editor.deleteGroup')}">🗑️</button>
                </div>
            </div>
            <div class="editor-group-body">
                <div class="editor-tasks">
                    <div class="editor-tasks-header">
                        <span data-i18n="editor.tasks">${t('editor.tasks')}</span>
                        <button class="editor-btn-add-small add-task-btn" data-i18n="editor.addTask">${t('editor.addTask')}</button>
                    </div>
                    <div class="editor-task-list">
                        ${tasksHtml}
                    </div>
                </div>
            </div>
        </div>`;
    }

    function buildTaskHTML(task, gi, ti) {
        const t = I18n.t;
        const label = task.name || `Task ${ti + 1}`;
        return `
        <div class="editor-task" data-group-index="${gi}" data-task-index="${ti}">
            <div class="editor-task-header">
                <span class="editor-task-label">${escapeHtmlStr(label)}</span>
                <div class="editor-task-actions">
                    <button class="editor-btn-copy clone-task-btn" title="复制任务">📋</button>
                    <button class="editor-btn-delete delete-task-btn" data-i18n-title="editor.deleteTask" title="${t('editor.deleteTask')}">🗑️</button>
                </div>
            </div>
            <div class="editor-task-fields">
                <div class="editor-field-row">
                    <div class="editor-field-inline">
                        <label data-i18n="editor.taskIcon">${t('editor.taskIcon')}</label>
                        <input type="text" class="editor-input-icon task-icon" value="${escapeAttr(task.icon || '')}"
                            data-i18n-placeholder="editor.taskIcon.placeholder"
                            placeholder="${t('editor.taskIcon.placeholder')}">
                    </div>
                    <div class="editor-field-inline editor-field-grow">
                        <label data-i18n="editor.taskId">${t('editor.taskId')}</label>
                        <input type="text" class="task-id" value="${escapeAttr(task.id || '')}"
                            data-i18n-placeholder="editor.taskId.placeholder"
                            placeholder="${t('editor.taskId.placeholder')}">
                    </div>
                    <div class="editor-field-inline editor-field-grow">
                        <label data-i18n="editor.taskName">${t('editor.taskName')}</label>
                        <input type="text" class="task-name" value="${escapeAttr(task.name || '')}"
                            data-i18n-placeholder="editor.taskName.placeholder"
                            placeholder="${t('editor.taskName.placeholder')}">
                    </div>
                </div>
                <div class="editor-field-row">
                    <div class="editor-field-inline editor-field-grow">
                        <label data-i18n="editor.taskDesc">${t('editor.taskDesc')}</label>
                        <input type="text" class="task-description" value="${escapeAttr(task.description || '')}"
                            data-i18n-placeholder="editor.taskDesc.placeholder"
                            placeholder="${t('editor.taskDesc.placeholder')}">
                    </div>
                </div>
                <div class="editor-field-row">
                    <div class="editor-field-inline editor-field-grow">
                        <label data-i18n="editor.taskScript">${t('editor.taskScript')}</label>
                        <input type="text" class="task-script" value="${escapeAttr(task.script || '')}"
                            placeholder="${t('editor.taskScript.placeholder') || '脚本路径(.ps1)或命令名(adb/python/dotnet...)'}">
                    </div>
                    <div class="editor-field-inline editor-field-grow">
                        <label data-i18n="editor.taskArgs">${t('editor.taskArgs')}</label>
                        <input type="text" class="task-arguments" value="${escapeAttr(task.arguments || '')}"
                            data-i18n-placeholder="editor.taskArgs.placeholder"
                            placeholder="${t('editor.taskArgs.placeholder')}">
                    </div>
                </div>
                <div class="editor-field-row">
                    <div class="editor-field-inline editor-field-grow">
                        <label data-i18n="editor.taskWorkDir">${t('editor.taskWorkDir')}</label>
                        <input type="text" class="task-workingDirectory" value="${escapeAttr(task.workingDirectory || '')}"
                            data-i18n-placeholder="editor.taskWorkDir.placeholder"
                            placeholder="${t('editor.taskWorkDir.placeholder')}">
                    </div>
                </div>
            </div>
        </div>`;
    }

    function attachGroupEvents(container) {
        // Collapse toggle
        container.querySelectorAll('.editor-group-collapse-btn').forEach(btn => {
            btn.onclick = function (e) {
                e.stopPropagation();
                const groupEl = this.closest('.editor-group');
                const body = groupEl.querySelector('.editor-group-body');
                this.classList.toggle('collapsed');
                body.classList.toggle('collapsed');
            };
        });

        // Click group header (but not inputs) to toggle collapse
        container.querySelectorAll('.editor-group-header').forEach(header => {
            header.addEventListener('click', function (e) {
                if (e.target.tagName === 'INPUT' || e.target.tagName === 'BUTTON') return;
                const btn = this.querySelector('.editor-group-collapse-btn');
                if (btn) btn.click();
            });
        });

        container.querySelectorAll('.delete-group-btn').forEach(btn => {
            btn.onclick = function () {
                if (confirm(I18n.t('editor.confirmDeleteGroup'))) {
                    const groupEl = this.closest('.editor-group');
                    groupEl.remove();
                }
            };
        });

        container.querySelectorAll('.delete-task-btn').forEach(btn => {
            btn.onclick = function () {
                if (confirm(I18n.t('editor.confirmDeleteTask'))) {
                    const taskEl = this.closest('.editor-task');
                    taskEl.remove();
                }
            };
        });

        container.querySelectorAll('.clone-task-btn').forEach(btn => {
            btn.onclick = function () {
                const taskEl = this.closest('.editor-task');
                const task = readTaskFromDOM(taskEl);
                task.id = task.id ? task.id + '-copy' : '';
                task.name = task.name ? task.name + ' (副本)' : '';
                const gi = taskEl.closest('.editor-group').getAttribute('data-group-index');
                const ti = taskEl.closest('.editor-task-list').querySelectorAll('.editor-task').length;
                const tempDiv = document.createElement('div');
                tempDiv.innerHTML = buildTaskHTML(task, gi, ti);
                const cloned = tempDiv.firstElementChild;
                taskEl.after(cloned);
                attachTaskEvents(cloned);
                I18n.applyTranslations();
            };
        });

        container.querySelectorAll('.add-task-btn').forEach(btn => {
            btn.onclick = function () {
                const groupEl = this.closest('.editor-group');
                const taskList = groupEl.querySelector('.editor-task-list');
                const gi = groupEl.getAttribute('data-group-index');
                const ti = taskList.querySelectorAll('.editor-task').length;
                const task = { id: '', name: '', description: '', script: '', arguments: '', workingDirectory: '', icon: '' };
                const tempDiv = document.createElement('div');
                tempDiv.innerHTML = buildTaskHTML(task, gi, ti);
                const newTask = tempDiv.firstElementChild;
                taskList.appendChild(newTask);
                attachTaskEvents(newTask);
                I18n.applyTranslations();
            };
        });
    }

    function readTaskFromDOM(taskEl) {
        return {
            id: taskEl.querySelector('.task-id').value,
            name: taskEl.querySelector('.task-name').value,
            description: taskEl.querySelector('.task-description').value || '',
            script: taskEl.querySelector('.task-script').value,
            arguments: taskEl.querySelector('.task-arguments').value || '',
            workingDirectory: taskEl.querySelector('.task-workingDirectory').value || '',
            icon: taskEl.querySelector('.task-icon').value || ''
        };
    }

    function attachTaskEvents(taskEl) {
        taskEl.querySelector('.delete-task-btn').onclick = function () {
            if (confirm(I18n.t('editor.confirmDeleteTask'))) {
                taskEl.remove();
            }
        };
        taskEl.querySelector('.clone-task-btn').onclick = function () {
            const task = readTaskFromDOM(taskEl);
            task.id = task.id ? task.id + '-copy' : '';
            task.name = task.name ? task.name + ' (副本)' : '';
            const gi = taskEl.closest('.editor-group').getAttribute('data-group-index');
            const ti = taskEl.closest('.editor-task-list').querySelectorAll('.editor-task').length;
            const tempDiv = document.createElement('div');
            tempDiv.innerHTML = buildTaskHTML(task, gi, ti);
            const cloned = tempDiv.firstElementChild;
            taskEl.after(cloned);
            attachTaskEvents(cloned);
            I18n.applyTranslations();
        };
    }

    function addGroup() {
        const groupsContainer = document.getElementById('editorGroups');
        const gi = groupsContainer.querySelectorAll('.editor-group').length;
        const group = { name: '', icon: '', tasks: [] };
        const tempDiv = document.createElement('div');
        tempDiv.innerHTML = buildGroupHTML(group, gi);
        const newGroup = tempDiv.firstElementChild;
        groupsContainer.appendChild(newGroup);
        attachGroupEvents(newGroup);
        I18n.applyTranslations();
    }

    function collectConfig() {
        const config = {
            scriptsBasePath: document.getElementById('editorScriptsBasePath').value || null,
            defaultWorkingDirectory: document.getElementById('editorDefaultWorkDir').value || null,
            groups: []
        };

        document.querySelectorAll('#editorGroups .editor-group').forEach(groupEl => {
            const group = {
                name: groupEl.querySelector('.group-name').value,
                icon: groupEl.querySelector('.group-icon').value || null,
                tasks: []
            };

            groupEl.querySelectorAll('.editor-task').forEach(taskEl => {
                const task = {
                    id: taskEl.querySelector('.task-id').value,
                    name: taskEl.querySelector('.task-name').value,
                    description: taskEl.querySelector('.task-description').value || null,
                    script: taskEl.querySelector('.task-script').value,
                    arguments: taskEl.querySelector('.task-arguments').value || null,
                    workingDirectory: taskEl.querySelector('.task-workingDirectory').value || null,
                    icon: taskEl.querySelector('.task-icon').value || null
                };
                group.tasks.push(task);
            });

            config.groups.push(group);
        });

        return config;
    }

    async function save() {
        const config = collectConfig();

        for (let gi = 0; gi < config.groups.length; gi++) {
            const group = config.groups[gi];
            if (!group.name.trim()) {
                showToast(I18n.t('editor.validationGroupName'), 'error');
                return;
            }
            for (let ti = 0; ti < group.tasks.length; ti++) {
                const task = group.tasks[ti];
                if (!task.id.trim() || !task.name.trim() || !task.script.trim()) {
                    showToast(I18n.t('editor.validationTaskFields'), 'error');
                    return;
                }
            }
        }

        try {
            const response = await fetch('/api/config', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(config)
            });

            if (response.ok) {
                showToast(I18n.t('editor.saved'), 'success');
                close();
                if (typeof refreshConfig === 'function') {
                    refreshConfig();
                }
            } else {
                showToast(I18n.t('editor.saveFailed'), 'error');
            }
        } catch (err) {
            console.error('Failed to save config:', err);
            showToast(I18n.t('editor.saveFailed'), 'error');
        }
    }

    function showToast(message, type) {
        const toast = document.createElement('div');
        toast.className = `toast toast-${type}`;
        toast.textContent = message;
        document.body.appendChild(toast);
        setTimeout(() => toast.classList.add('toast-visible'), 10);
        setTimeout(() => {
            toast.classList.remove('toast-visible');
            setTimeout(() => toast.remove(), 300);
        }, 3000);
    }

    function escapeAttr(text) {
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    function escapeHtmlStr(text) {
        const div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    return { open, close };
})();
