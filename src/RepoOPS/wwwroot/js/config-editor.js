// ===== RepoOPS Configuration Editor =====

const ConfigEditor = (() => {
    let currentConfig = null;
    let isOpen = false;
    let pendingFocusTaskId = null;
    let currentIconInput = null;
    const recentIconsStorageKey = 'repoops-recent-icons';

    const iconCategories = [
        { key: 'common', icons: ['📁', '📂', '📦', '🗂️', '⭐', '📝', '📋', '✅', '❌', '⚠️', '🔥', '🧩'] },
        { key: 'build', icons: ['🛠️', '⚙️', '🔧', '🔨', '🏗️', '📦', '🧪', '🚀'] },
        { key: 'run', icons: ['▶️', '⏹️', '⏯️', '🔄', '⏱️', '✅', '🚀', '🔥'] },
        { key: 'project', icons: ['💻', '🖥️', '📁', '📦', '🗃️', '📋', '📝', '🔒'] },
        { key: 'device', icons: ['📱', '⌚', '🖨️', '🎮', '🧷', '💾', '🔌', '🔋'] },
        { key: 'network', icons: ['🌐', '☁️', '📡', '📤', '📥', '🔗', '🛰️', '🔐'] }
    ];

    const iconSuggestions = Array.from(new Set(iconCategories.flatMap(category => category.icons)));

    function open() {
        if (isOpen) return;
        isOpen = true;
        loadAndShow();
    }

    function openTask(taskId) {
        pendingFocusTaskId = taskId;

        if (isOpen) {
            focusPendingTask();
            return;
        }

        isOpen = true;
        loadAndShow();
    }

    function close() {
        isOpen = false;
        pendingFocusTaskId = null;
        currentIconInput = null;
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
        // Remove old overlay if exists
        const old = document.getElementById('editorOverlay');
        if (old) old.remove();

        const overlay = document.createElement('div');
        overlay.id = 'editorOverlay';
        overlay.className = 'editor-overlay';
        overlay.innerHTML = buildEditorHTML();
        document.body.appendChild(overlay);

        // Attach events
        overlay.querySelector('.editor-overlay-bg').onclick = close;
        overlay.querySelector('.editor-close-btn').onclick = close;
        overlay.querySelector('.editor-save-btn').onclick = save;
        overlay.querySelector('.editor-cancel-btn').onclick = close;
        overlay.querySelector('.editor-add-group-btn').onclick = addGroup;

        // Attach group/task delete and add-task buttons
        attachGroupEvents(overlay);
        attachIconPickerEvents(overlay);

        // Apply i18n to new elements
        I18n.applyTranslations();
        focusPendingTask();
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
                ${buildIconDatalistHTML()}
                ${buildIconPickerHTML()}
                <!-- Global Settings -->
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

                <!-- Groups -->
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

    function buildIconDatalistHTML() {
        const options = iconSuggestions
            .map(icon => `<option value="${icon}">${icon}</option>`)
            .join('');

        return `<datalist id="iconSuggestions">${options}</datalist>`;
    }

    function buildIconPickerHTML() {
        const t = I18n.t;

        return `
        <div class="editor-icon-picker" id="editorIconPicker" hidden>
            <div class="editor-icon-picker-title" data-i18n="editor.iconPickerTitle">${t('editor.iconPickerTitle')}</div>
            <input type="text" class="editor-icon-picker-search" id="editorIconPickerSearch"
                data-i18n-placeholder="editor.iconSearchPlaceholder"
                placeholder="${t('editor.iconSearchPlaceholder')}">
            <div class="editor-icon-picker-body" id="editorIconPickerBody">
                ${buildIconPickerSections('')}
            </div>
        </div>`;
    }

    function buildIconPickerSections(searchTerm) {
        const t = I18n.t;
        const normalized = (searchTerm || '').trim().toLowerCase();
        const sections = [];

        const recentIcons = getRecentIcons();
        if (recentIcons.length > 0) {
            const recentFiltered = filterIcons(recentIcons, normalized);
            if (recentFiltered.length > 0) {
                sections.push(buildIconSection(t('editor.iconRecent'), recentFiltered));
            }
        }

        iconCategories.forEach(category => {
            const filtered = filterIcons(category.icons, normalized);
            if (filtered.length > 0) {
                sections.push(buildIconSection(t(`editor.iconCategory${capitalize(category.key)}`), filtered));
            }
        });

        if (sections.length === 0) {
            return `<div class="editor-icon-picker-empty">${t('editor.iconNoResults')}</div>`;
        }

        return sections.join('');
    }

    function buildIconSection(title, icons) {
        const items = icons
            .map(icon => `<button type="button" class="editor-icon-option" data-icon="${icon}" title="${icon}">${icon}</button>`)
            .join('');

        return `
        <div class="editor-icon-section">
            <div class="editor-icon-section-title">${title}</div>
            <div class="editor-icon-picker-grid">${items}</div>
        </div>`;
    }

    function filterIcons(icons, searchTerm) {
        if (!searchTerm) {
            return Array.from(new Set(icons));
        }

        return Array.from(new Set(icons)).filter(icon => icon.includes(searchTerm));
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
                        <div class="editor-icon-input-group">
                            <input type="text" class="editor-input-icon group-icon" value="${escapeAttr(group.icon || '')}"
                                list="iconSuggestions"
                                data-i18n-placeholder="editor.groupIcon.placeholder"
                                placeholder="${t('editor.groupIcon.placeholder')}">
                            <button type="button" class="editor-icon-picker-trigger" data-i18n-title="editor.chooseIcon" title="${t('editor.chooseIcon')}">▾</button>
                        </div>
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
        return `
        <div class="editor-task" data-group-index="${gi}" data-task-index="${ti}" data-task-id="${escapeAttr(task.id || '')}">
            <input type="hidden" class="task-description" value="${escapeAttr(task.description || '')}">
            <input type="hidden" class="task-workingDirectory" value="${escapeAttr(task.workingDirectory || '')}">
            <div class="editor-task-header">
                <span class="editor-task-label">${escapeHtmlStr(task.name || `Task ${ti + 1}`)}</span>
                <div class="editor-task-actions">
                    <button class="editor-btn-copy clone-task-btn" title="复制任务">📋</button>
                    <button class="editor-btn-delete delete-task-btn" data-i18n-title="editor.deleteTask" title="${t('editor.deleteTask')}">🗑️</button>
                </div>
            </div>
            <div class="editor-task-fields">
                <div class="editor-field-row">
                    <div class="editor-field-inline">
                        <label data-i18n="editor.taskIcon">${t('editor.taskIcon')}</label>
                        <div class="editor-icon-input-group">
                            <input type="text" class="editor-input-icon task-icon" value="${escapeAttr(task.icon || '')}"
                                list="iconSuggestions"
                                data-i18n-placeholder="editor.taskIcon.placeholder"
                                placeholder="${t('editor.taskIcon.placeholder')}">
                            <button type="button" class="editor-icon-picker-trigger" data-i18n-title="editor.chooseIcon" title="${t('editor.chooseIcon')}">▾</button>
                        </div>
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
                attachIconPickerEvents(newTask);
                I18n.applyTranslations();
            };
        });
    }

    function attachIconPickerEvents(container) {
        container.querySelectorAll('.editor-icon-picker-trigger').forEach(btn => {
            btn.onclick = function (e) {
                e.preventDefault();
                e.stopPropagation();

                const input = this.closest('.editor-icon-input-group')?.querySelector('input');
                if (!input) {
                    return;
                }

                toggleIconPicker(this, input);
            };
        });

        const picker = document.getElementById('editorIconPicker');
        if (picker && !picker.dataset.bound) {
            picker.dataset.bound = 'true';
            const searchInput = picker.querySelector('#editorIconPickerSearch');
            const body = picker.querySelector('#editorIconPickerBody');

            searchInput?.addEventListener('input', () => {
                rerenderIconPicker(searchInput.value);
            });

            body?.addEventListener('click', (e) => {
                const option = e.target.closest('.editor-icon-option');
                if (!option) {
                    return;
                }

                e.preventDefault();
                e.stopPropagation();
                applyPickedIcon(option.getAttribute('data-icon') || '');
            });

            const overlay = document.getElementById('editorOverlay');
            overlay?.addEventListener('click', (event) => {
                if (!event.target.closest('.editor-icon-picker') && !event.target.closest('.editor-icon-picker-trigger')) {
                    closeIconPicker();
                }
            });
        }
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
            attachIconPickerEvents(cloned);
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
        // Attach events
        attachGroupEvents(newGroup);
        attachIconPickerEvents(newGroup);
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

        // Validate required fields
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
                // Refresh the task list
                refreshConfig();
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
        return text.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function escapeHtmlStr(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function focusPendingTask() {
        if (!pendingFocusTaskId) {
            return;
        }

        if (focusTaskCard(pendingFocusTaskId)) {
            pendingFocusTaskId = null;
        }
    }

    function focusTaskCard(taskId) {
        const taskEls = Array.from(document.querySelectorAll('.editor-task'));
        const taskEl = taskEls.find(el => el.getAttribute('data-task-id') === taskId);
        if (!taskEl) {
            return false;
        }

        document.querySelectorAll('.editor-task.editor-task-target').forEach(el => {
            el.classList.remove('editor-task-target');
        });

        const groupEl = taskEl.closest('.editor-group');
        const body = groupEl?.querySelector('.editor-group-body');
        const collapseBtn = groupEl?.querySelector('.editor-group-collapse-btn');

        if (body && body.classList.contains('collapsed')) {
            body.classList.remove('collapsed');
            collapseBtn?.classList.remove('collapsed');
        }

        taskEl.classList.add('editor-task-target');
        taskEl.scrollIntoView({ behavior: 'smooth', block: 'center' });

        const focusInput = taskEl.querySelector('.task-name') || taskEl.querySelector('.task-id');
        focusInput?.focus();

        setTimeout(() => {
            taskEl.classList.remove('editor-task-target');
        }, 2200);

        return true;
    }

    function toggleIconPicker(trigger, input) {
        const picker = document.getElementById('editorIconPicker');
        const searchInput = document.getElementById('editorIconPickerSearch');
        if (!picker) {
            return;
        }

        if (!picker.hidden && currentIconInput === input) {
            closeIconPicker();
            return;
        }

        currentIconInput = input;
        if (searchInput) {
            searchInput.value = '';
        }
        rerenderIconPicker('');
        highlightSelectedIcon(input.value);

        picker.hidden = false;

        const triggerRect = trigger.getBoundingClientRect();
        const pickerWidth = Math.min(420, window.innerWidth - 24);
        const pickerHeight = Math.min(520, window.innerHeight - 24);
        const left = Math.max(8, Math.min(triggerRect.left, window.innerWidth - pickerWidth - 12));
        const top = Math.max(8, Math.min(triggerRect.bottom + 8, window.innerHeight - pickerHeight - 12));

        picker.style.left = `${left}px`;
        picker.style.top = `${top}px`;
        searchInput?.focus();
    }

    function closeIconPicker() {
        const picker = document.getElementById('editorIconPicker');
        const searchInput = document.getElementById('editorIconPickerSearch');
        if (picker) {
            picker.hidden = true;
        }
        if (searchInput) {
            searchInput.value = '';
        }
        currentIconInput = null;
    }

    function highlightSelectedIcon(selectedIcon) {
        document.querySelectorAll('.editor-icon-option').forEach(option => {
            option.classList.toggle('active', option.getAttribute('data-icon') === selectedIcon);
        });
    }

    function applyPickedIcon(icon) {
        if (!currentIconInput) {
            return;
        }

        currentIconInput.value = icon;
        currentIconInput.dispatchEvent(new Event('input', { bubbles: true }));
        saveRecentIcon(icon);
        currentIconInput.focus();
        closeIconPicker();
    }

    function rerenderIconPicker(searchTerm) {
        const body = document.getElementById('editorIconPickerBody');
        if (!body) {
            return;
        }

        body.innerHTML = buildIconPickerSections(searchTerm);
        if (currentIconInput) {
            highlightSelectedIcon(currentIconInput.value);
        }
    }

    function getRecentIcons() {
        try {
            const raw = localStorage.getItem(recentIconsStorageKey);
            const parsed = raw ? JSON.parse(raw) : [];
            return Array.isArray(parsed) ? parsed.filter(Boolean).slice(0, 12) : [];
        } catch {
            return [];
        }
    }

    function saveRecentIcon(icon) {
        if (!icon) {
            return;
        }

        const recent = getRecentIcons().filter(item => item !== icon);
        recent.unshift(icon);

        try {
            localStorage.setItem(recentIconsStorageKey, JSON.stringify(recent.slice(0, 12)));
        } catch {
            // ignore storage errors
        }
    }

    function capitalize(text) {
        return text.charAt(0).toUpperCase() + text.slice(1);
    }

    return { open, openTask, close };
})();
