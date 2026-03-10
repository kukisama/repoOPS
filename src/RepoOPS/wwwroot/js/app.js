// ===== RepoOPS Client Application =====

/** @type {signalR.HubConnection} */
let connection = null;

/** @type {Map<string, {terminal: Terminal, fitAddon: FitAddon, taskId: string, isRunning: boolean}>} */
const terminals = new Map();

/** Current active tab execution ID */
let activeTabId = null;

/** Running task count */
let runningCount = 0;

/** Whether all tabs are being closed */
let isClosingAllTabs = false;

/** Current context-menu target task */
let contextMenuTaskId = null;
let contextMenuGroupIndex = -1;
let contextMenuTaskIndex = -1;

/** Current loaded task config */
let currentTaskConfig = null;

/** Drag state for task list */
let draggedTaskMeta = null;
let suppressTaskClickUntil = 0;

const themePalettes = {
    light: {
        terminal: {
            background: '#f5f7fb',
            foreground: '#1f2937',
            cursor: '#0a66d4',
            cursorAccent: '#f5f7fb',
            selectionBackground: 'rgba(10, 102, 212, 0.2)',
            black: '#1f2937',
            red: '#cf222e',
            green: '#1a7f37',
            yellow: '#9a6700',
            blue: '#0969da',
            magenta: '#8250df',
            cyan: '#1b7c83',
            white: '#6e7781',
            brightBlack: '#57606a',
            brightRed: '#a40e26',
            brightGreen: '#116329',
            brightYellow: '#7d4e00',
            brightBlue: '#0550ae',
            brightMagenta: '#6639ba',
            brightCyan: '#0f5d66',
            brightWhite: '#111827'
        }
    },
    dark: {
        terminal: {
            background: '#1e1e1e',
            foreground: '#cccccc',
            cursor: '#cccccc',
            cursorAccent: '#1e1e1e',
            selectionBackground: 'rgba(0, 120, 212, 0.3)',
            black: '#1e1e1e',
            red: '#f14c4c',
            green: '#4ec965',
            yellow: '#e8a838',
            blue: '#3794ff',
            magenta: '#bc3fbc',
            cyan: '#29b8db',
            white: '#cccccc',
            brightBlack: '#666666',
            brightRed: '#f14c4c',
            brightGreen: '#4ec965',
            brightYellow: '#e8a838',
            brightBlue: '#3794ff',
            brightMagenta: '#bc3fbc',
            brightCyan: '#29b8db',
            brightWhite: '#ffffff'
        }
    }
};

const Theme = (() => {
    let currentTheme = 'light';

    function detectTheme() {
        const saved = localStorage.getItem('repoops-theme');
        if (saved === 'light' || saved === 'dark') {
            return saved;
        }

        return 'light';
    }

    function init() {
        setTheme(detectTheme(), false);
    }

    function setTheme(theme, persist = true) {
        if (!themePalettes[theme]) {
            theme = 'light';
        }

        currentTheme = theme;
        document.documentElement.setAttribute('data-theme', theme);

        if (persist) {
            localStorage.setItem('repoops-theme', theme);
        }

        applyTerminalTheme();
        updateToggleButton();
    }

    function toggleTheme() {
        setTheme(currentTheme === 'light' ? 'dark' : 'light');
    }

    function getTheme() {
        return currentTheme;
    }

    function getTerminalTheme() {
        return themePalettes[currentTheme].terminal;
    }

    function applyTerminalTheme() {
        terminals.forEach(info => {
            if (info && info.terminal) {
                info.terminal.options.theme = getTerminalTheme();
            }
        });
    }

    function updateToggleButton() {
        const btn = document.getElementById('btnThemeSwitch');
        if (!btn || typeof I18n === 'undefined' || typeof I18n.t !== 'function') {
            return;
        }

        const isLight = currentTheme === 'light';
        btn.textContent = I18n.t(isLight ? 'theme.switchToDark' : 'theme.switchToLight');
        btn.title = I18n.t(isLight ? 'theme.switchTitleToDark' : 'theme.switchTitleToLight');
    }

    return { init, setTheme, toggleTheme, getTheme, getTerminalTheme, updateToggleButton };
})();

window.Theme = Theme;

// ===== SignalR Connection =====

function initConnection() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl('/hub/tasks')
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

    connection.on('TaskOutput', (executionId, output) => {
        const info = terminals.get(executionId);
        if (info && info.terminal) {
            info.terminal.write(output);
        }
    });

    connection.on('TaskStarted', (executionId, taskName) => {
        updateRunningCount(1);
        updateTaskItemStatus(executionId, true);
    });

    connection.on('TaskCompleted', (executionId, exitCode) => {
        const info = terminals.get(executionId);
        if (info) {
            info.isRunning = false;
            updateTabStatus(executionId, exitCode === 0 ? 'success' : exitCode === -1 ? 'cancelled' : 'error');
        }
        updateRunningCount(-1);
        updateStopButton();
        updateTaskItemStatus(executionId, false);
    });

    connection.onreconnecting(() => {
        updateConnectionStatus(false);
    });

    connection.onreconnected(() => {
        updateConnectionStatus(true);
    });

    connection.onclose(() => {
        updateConnectionStatus(false);
    });

    startConnection();
}

async function startConnection() {
    try {
        await connection.start();
        updateConnectionStatus(true);
        console.log('SignalR connected.');
    } catch (err) {
        console.error('SignalR connection error:', err);
        updateConnectionStatus(false);
        setTimeout(startConnection, 5000);
    }
}

function updateConnectionStatus(connected) {
    const el = document.getElementById('connectionStatus');
    if (connected) {
        el.textContent = I18n.t('status.connected');
        el.className = 'status-indicator status-connected';
    } else {
        el.textContent = I18n.t('status.disconnected');
        el.className = 'status-indicator status-disconnected';
    }
}

// ===== Task Configuration =====

async function loadConfig() {
    try {
        const response = await fetch('/api/config');
        const config = await response.json();
        currentTaskConfig = config;
        renderTaskList(config);
    } catch (err) {
        console.error('Failed to load config:', err);
        document.getElementById('taskList').innerHTML =
            `<div class="loading">${I18n.t('loading.failed')}</div>`;
    }
}

function refreshConfig() {
    loadConfig();
}

function renderTaskList(config) {
    const container = document.getElementById('taskList');

    if (!config.groups || config.groups.length === 0) {
        container.innerHTML = `<div class="loading">${I18n.t('no.tasks')}</div>`;
        return;
    }

    let html = '';

    config.groups.forEach((group, groupIndex) => {
        const groupId = `group-${groupIndex}`;
        const icon = group.icon || '📁';

        html += `<div class="task-group">`;
        html += `<div class="task-group-header" onclick="toggleGroup('${groupId}')">`;
        html += `<span class="chevron" id="chevron-${groupId}">▼</span>`;
        html += `<span class="group-icon">${icon}</span>`;
        html += `<span>${escapeHtml(group.name)}</span>`;
        html += `</div>`;
        html += `<div class="task-group-items" id="${groupId}" data-group-index="${groupIndex}">`;

        if (group.tasks) {
            group.tasks.forEach((task, taskIndex) => {
                const icon = task.icon || '▶️';
                const desc = task.description ? `title="${escapeHtml(task.description)}"` : '';
                html += `<div class="task-item" draggable="true" ${desc} data-group-index="${groupIndex}" data-task-index="${taskIndex}" data-task-id="${escapeHtml(task.id)}" data-task-name="${escapeHtml(task.name)}">`;
                html += `<span class="task-icon">${icon}</span>`;
                html += `<span class="task-name">${escapeHtml(task.name)}</span>`;
                html += `</div>`;
            });
        }

        html += `</div></div>`;
    });

    container.innerHTML = html;
    attachTaskListEvents(container);
}

function attachTaskListEvents(container) {
    container.querySelectorAll('.task-item').forEach(taskItem => {
        taskItem.addEventListener('click', () => {
            if (Date.now() < suppressTaskClickUntil) {
                return;
            }

            const taskId = taskItem.getAttribute('data-task-id');
            const taskName = taskItem.getAttribute('data-task-name');
            if (!taskId || !taskName) {
                return;
            }

            runTask(taskId, taskName);
        });

        taskItem.addEventListener('contextmenu', (event) => {
            event.preventDefault();
            event.stopPropagation();

            const taskId = taskItem.getAttribute('data-task-id');
            const groupIndex = Number.parseInt(taskItem.getAttribute('data-group-index') || '-1', 10);
            const taskIndex = Number.parseInt(taskItem.getAttribute('data-task-index') || '-1', 10);
            if (!taskId) {
                return;
            }

            showTaskContextMenu(event.clientX, event.clientY, taskId, taskItem, groupIndex, taskIndex);
        });

        taskItem.addEventListener('dragstart', (event) => {
            const groupIndex = Number.parseInt(taskItem.getAttribute('data-group-index') || '-1', 10);
            const taskIndex = Number.parseInt(taskItem.getAttribute('data-task-index') || '-1', 10);
            if (groupIndex < 0 || taskIndex < 0) {
                event.preventDefault();
                return;
            }

            draggedTaskMeta = { sourceGroupIndex: groupIndex, sourceTaskIndex: taskIndex };
            taskItem.classList.add('dragging');

            if (event.dataTransfer) {
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', taskItem.getAttribute('data-task-id') || 'drag-task');
            }

            hideTaskContextMenu();
        });

        taskItem.addEventListener('dragend', () => {
            taskItem.classList.remove('dragging');
            draggedTaskMeta = null;
            suppressTaskClickUntil = Date.now() + 200;

            container.querySelectorAll('.task-group-items.drag-over').forEach(groupItems => {
                groupItems.classList.remove('drag-over');
            });
        });
    });

    container.querySelectorAll('.task-group-items').forEach(groupItems => {
        groupItems.addEventListener('dragover', (event) => {
            if (!draggedTaskMeta) {
                return;
            }

            event.preventDefault();
            groupItems.classList.add('drag-over');

            const draggingItem = container.querySelector('.task-item.dragging');
            if (!draggingItem) {
                return;
            }

            const afterElement = getTaskItemAfterPointer(groupItems, event.clientY);
            if (!afterElement) {
                groupItems.appendChild(draggingItem);
            } else if (afterElement !== draggingItem) {
                groupItems.insertBefore(draggingItem, afterElement);
            }
        });

        groupItems.addEventListener('drop', async (event) => {
            if (!draggedTaskMeta || !currentTaskConfig || !Array.isArray(currentTaskConfig.groups)) {
                return;
            }

            event.preventDefault();
            groupItems.classList.remove('drag-over');

            const targetGroupIndex = Number.parseInt(groupItems.getAttribute('data-group-index') || '-1', 10);
            const draggingItem = container.querySelector('.task-item.dragging');
            if (targetGroupIndex < 0 || !draggingItem) {
                return;
            }

            const targetTaskIndex = Array.from(groupItems.querySelectorAll('.task-item')).indexOf(draggingItem);
            if (targetTaskIndex < 0) {
                return;
            }

            await moveTaskAndPersist(draggedTaskMeta.sourceGroupIndex, draggedTaskMeta.sourceTaskIndex, targetGroupIndex, targetTaskIndex);
        });

        groupItems.addEventListener('dragleave', (event) => {
            if (!groupItems.contains(event.relatedTarget)) {
                groupItems.classList.remove('drag-over');
            }
        });
    });
}

function showTaskContextMenu(x, y, taskId, taskItem, groupIndex, taskIndex) {
    const menu = document.getElementById('taskContextMenu');
    if (!menu) {
        return;
    }

    hideTaskContextMenu();

    contextMenuTaskId = taskId;
    contextMenuGroupIndex = Number.isFinite(groupIndex) ? groupIndex : -1;
    contextMenuTaskIndex = Number.isFinite(taskIndex) ? taskIndex : -1;
    taskItem.classList.add('context-target');

    menu.hidden = false;
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;

    const rect = menu.getBoundingClientRect();
    const maxLeft = window.innerWidth - rect.width - 8;
    const maxTop = window.innerHeight - rect.height - 8;
    menu.style.left = `${Math.max(8, Math.min(x, maxLeft))}px`;
    menu.style.top = `${Math.max(8, Math.min(y, maxTop))}px`;
}

function hideTaskContextMenu() {
    const menu = document.getElementById('taskContextMenu');
    if (menu) {
        menu.hidden = true;
    }

    document.querySelectorAll('.task-item.context-target').forEach(el => {
        el.classList.remove('context-target');
    });

    contextMenuTaskId = null;
    contextMenuGroupIndex = -1;
    contextMenuTaskIndex = -1;
}

function editTaskFromContextMenu() {
    const taskId = contextMenuTaskId;
    hideTaskContextMenu();

    if (!taskId || typeof ConfigEditor === 'undefined' || typeof ConfigEditor.openTask !== 'function') {
        return;
    }

    ConfigEditor.openTask(taskId);
}

async function copyTaskFromContextMenu() {
    if (!currentTaskConfig || !Array.isArray(currentTaskConfig.groups)) {
        hideTaskContextMenu();
        return;
    }

    const sourceGroup = currentTaskConfig.groups[contextMenuGroupIndex];
    const sourceTask = sourceGroup?.tasks?.[contextMenuTaskIndex];
    hideTaskContextMenu();

    if (!sourceTask) {
        return;
    }

    const clonedTask = {
        ...sourceTask,
        id: sourceTask.id ? `${sourceTask.id}-copy` : '',
        name: sourceTask.name ? `${sourceTask.name} (副本)` : ''
    };

    sourceGroup.tasks.splice(contextMenuTaskIndex + 1, 0, clonedTask);
    await saveTaskConfigAndReload();
}

async function deleteTaskFromContextMenu() {
    if (!currentTaskConfig || !Array.isArray(currentTaskConfig.groups)) {
        hideTaskContextMenu();
        return;
    }

    const sourceGroup = currentTaskConfig.groups[contextMenuGroupIndex];
    const sourceTask = sourceGroup?.tasks?.[contextMenuTaskIndex];
    hideTaskContextMenu();

    if (!sourceTask) {
        return;
    }

    if (!confirm(I18n.t('menu.confirmDeleteTask'))) {
        return;
    }

    sourceGroup.tasks.splice(contextMenuTaskIndex, 1);
    await saveTaskConfigAndReload();
}

function getTaskItemAfterPointer(groupItems, pointerY) {
    const siblings = Array.from(groupItems.querySelectorAll('.task-item:not(.dragging)'));

    let closest = {
        offset: Number.NEGATIVE_INFINITY,
        element: null
    };

    siblings.forEach(taskItem => {
        const box = taskItem.getBoundingClientRect();
        const offset = pointerY - box.top - box.height / 2;

        if (offset < 0 && offset > closest.offset) {
            closest = { offset, element: taskItem };
        }
    });

    return closest.element;
}

async function moveTaskAndPersist(sourceGroupIndex, sourceTaskIndex, targetGroupIndex, targetTaskIndex) {
    if (!currentTaskConfig || !Array.isArray(currentTaskConfig.groups)) {
        return;
    }

    const sourceGroup = currentTaskConfig.groups[sourceGroupIndex];
    const targetGroup = currentTaskConfig.groups[targetGroupIndex];
    if (!sourceGroup || !targetGroup || !Array.isArray(sourceGroup.tasks) || !Array.isArray(targetGroup.tasks)) {
        return;
    }

    if (sourceTaskIndex < 0 || sourceTaskIndex >= sourceGroup.tasks.length) {
        return;
    }

    const [movingTask] = sourceGroup.tasks.splice(sourceTaskIndex, 1);
    if (!movingTask) {
        return;
    }

    let normalizedTargetIndex = targetTaskIndex;
    if (sourceGroupIndex === targetGroupIndex && sourceTaskIndex < normalizedTargetIndex) {
        normalizedTargetIndex -= 1;
    }

    normalizedTargetIndex = Math.max(0, Math.min(normalizedTargetIndex, targetGroup.tasks.length));
    targetGroup.tasks.splice(normalizedTargetIndex, 0, movingTask);

    await saveTaskConfigAndReload();
}

async function saveTaskConfigAndReload() {
    try {
        const response = await fetch('/api/config', {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(currentTaskConfig)
        });

        if (!response.ok) {
            throw new Error(`Save failed with status ${response.status}`);
        }

        await loadConfig();
    } catch (err) {
        console.error('Failed to save task config:', err);
        alert(I18n.t('menu.saveConfigFailed'));
    }
}

function toggleGroup(groupId) {
    const items = document.getElementById(groupId);
    const chevron = document.getElementById(`chevron-${groupId}`);

    if (items.classList.contains('collapsed')) {
        items.classList.remove('collapsed');
        items.style.maxHeight = items.scrollHeight + 'px';
        chevron.classList.remove('collapsed');
    } else {
        items.style.maxHeight = items.scrollHeight + 'px';
        // Force reflow
        items.offsetHeight;
        items.classList.add('collapsed');
        chevron.classList.add('collapsed');
    }
}

// ===== Task Execution =====

async function runTask(taskId, taskName) {
    if (!connection || connection.state !== signalR.HubConnectionState.Connected) {
        alert(I18n.t('alert.notConnected'));
        return;
    }

    try {
        const executionId = await connection.invoke('StartTask', taskId);
        createTerminalTab(executionId, taskName, taskId);
    } catch (err) {
        console.error('Failed to start task:', err);
        alert(`${I18n.t('alert.startFailed')} ${err.message || err}`);
    }
}

async function stopCurrentTask() {
    if (!activeTabId) return;

    const info = terminals.get(activeTabId);
    if (!info || !info.isRunning) return;

    try {
        await connection.invoke('StopTask', activeTabId);
    } catch (err) {
        console.error('Failed to stop task:', err);
    }
}

async function stopTask(executionId) {
    const info = terminals.get(executionId);
    if (!info || !info.isRunning) return;

    try {
        await connection.invoke('StopTask', executionId);
    } catch (err) {
        console.error('Failed to stop task:', err);
    }
}

async function closeAllTabs() {
    if (isClosingAllTabs || terminals.size === 0) {
        return;
    }

    isClosingAllTabs = true;
    updateStopButton();

    try {
        const executionIds = Array.from(terminals.keys());
        for (const executionId of executionIds) {
            await closeTab(executionId);
        }
    } finally {
        isClosingAllTabs = false;
        updateStopButton();
    }
}

// ===== Terminal Management =====

function createTerminalTab(executionId, taskName, taskId) {
    // Hide welcome screen
    document.getElementById('welcomeScreen').style.display = 'none';

    // Create terminal instance
    const terminal = new Terminal({
        theme: Theme.getTerminalTheme(),
        fontFamily: 'Consolas, "Courier New", monospace',
        fontSize: 14,
        lineHeight: 1.2,
        scrollback: 10000,
        cursorBlink: false,
        disableStdin: true,
        convertEol: false
    });

    const fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);

    // Create terminal wrapper
    const wrapper = document.createElement('div');
    wrapper.className = 'terminal-wrapper';
    wrapper.id = `terminal-${executionId}`;
    document.getElementById('terminalContainer').appendChild(wrapper);

    terminal.open(wrapper);
    fitAddon.fit();

    // Store terminal info
    terminals.set(executionId, {
        terminal,
        fitAddon,
        taskId,
        isRunning: true
    });

    // Create tab
    addTab(executionId, taskName);

    // Switch to the new tab
    switchTab(executionId);
}

function clearCurrentTerminal() {
    if (!activeTabId) return;
    const info = terminals.get(activeTabId);
    if (info && info.terminal) {
        info.terminal.clear();
    }
}

// ===== Tab Management =====

function addTab(executionId, taskName) {
    const tabBar = document.getElementById('tabBar');

    // Remove placeholder if exists
    const placeholder = tabBar.querySelector('.tab-placeholder');
    if (placeholder) placeholder.remove();

    const tab = document.createElement('div');
    tab.className = 'tab';
    tab.id = `tab-${executionId}`;
    tab.onclick = (e) => {
        if (!e.target.classList.contains('tab-close')) {
            switchTab(executionId);
        }
    };

    tab.innerHTML = `
        <span class="tab-status running"></span>
        <span class="tab-name">${escapeHtml(taskName)}</span>
        <button class="tab-close" onclick="closeTab('${executionId}')" title="Close">×</button>
    `;

    tabBar.appendChild(tab);
    updateStopButton();
}

function switchTab(executionId) {
    // Deactivate current tab
    if (activeTabId) {
        const currentTab = document.getElementById(`tab-${activeTabId}`);
        if (currentTab) currentTab.classList.remove('active');
        const currentTerminal = document.getElementById(`terminal-${activeTabId}`);
        if (currentTerminal) currentTerminal.classList.remove('active');
    }

    // Activate new tab
    activeTabId = executionId;
    const tab = document.getElementById(`tab-${executionId}`);
    if (tab) tab.classList.add('active');
    const terminalWrapper = document.getElementById(`terminal-${executionId}`);
    if (terminalWrapper) terminalWrapper.classList.add('active');

    // Fit terminal
    const info = terminals.get(executionId);
    if (info && info.fitAddon) {
        setTimeout(() => info.fitAddon.fit(), 50);
    }

    updateStopButton();
}

async function closeTab(executionId) {
    const info = terminals.get(executionId);

    // Stop task if running
    if (info && info.isRunning) {
        await stopTask(executionId);
    }

    // Remove tab
    const tab = document.getElementById(`tab-${executionId}`);
    if (tab) tab.remove();

    // Remove terminal
    const terminalWrapper = document.getElementById(`terminal-${executionId}`);
    if (terminalWrapper) terminalWrapper.remove();

    // Dispose terminal
    if (info && info.terminal) {
        info.terminal.dispose();
    }

    terminals.delete(executionId);

    // Switch to another tab or show welcome
    if (activeTabId === executionId) {
        activeTabId = null;
        const remainingTabs = document.querySelectorAll('.tab');
        if (remainingTabs.length > 0) {
            const lastTab = remainingTabs[remainingTabs.length - 1];
            const id = lastTab.id.replace('tab-', '');
            switchTab(id);
        } else {
            document.getElementById('welcomeScreen').style.display = 'flex';
            const tabBar = document.getElementById('tabBar');
            tabBar.innerHTML = `<div class="tab-placeholder">${I18n.t('tab.placeholder')}</div>`;
        }
    }

    updateStopButton();
}

function updateTabStatus(executionId, status) {
    const tab = document.getElementById(`tab-${executionId}`);
    if (!tab) return;

    const statusEl = tab.querySelector('.tab-status');
    if (statusEl) {
        statusEl.className = `tab-status ${status}`;
    }
}

function updateStopButton() {
    const stopBtn = document.getElementById('btnStop');
    const closeAllBtn = document.getElementById('btnCloseAllTabs');

    if (closeAllBtn) {
        closeAllBtn.disabled = isClosingAllTabs || terminals.size === 0;
    }

    if (!stopBtn) {
        return;
    }

    if (!activeTabId || isClosingAllTabs) {
        stopBtn.disabled = true;
        return;
    }

    const info = terminals.get(activeTabId);
    stopBtn.disabled = !(info && info.isRunning);
}

// ===== Task Item Status =====

function updateTaskItemStatus(executionId, isRunning) {
    // We could track which task items are running, but since
    // the same task can be run multiple times, we'll keep it simple
}

function updateRunningCount(delta) {
    runningCount = Math.max(0, runningCount + delta);
    document.getElementById('runningCount').textContent = runningCount;
}

// ===== Resize Handle =====

function initResizeHandle() {
    const handle = document.getElementById('resizeHandle');
    const panel = document.getElementById('taskPanel');
    let isResizing = false;

    handle.addEventListener('mousedown', (e) => {
        isResizing = true;
        handle.classList.add('active');
        document.body.style.cursor = 'col-resize';
        document.body.style.userSelect = 'none';
        e.preventDefault();
    });

    document.addEventListener('mousemove', (e) => {
        if (!isResizing) return;
        const newWidth = Math.max(200, Math.min(500, e.clientX));
        panel.style.width = newWidth + 'px';

        // Refit active terminal
        if (activeTabId) {
            const info = terminals.get(activeTabId);
            if (info && info.fitAddon) {
                info.fitAddon.fit();
            }
        }
    });

    document.addEventListener('mouseup', () => {
        if (isResizing) {
            isResizing = false;
            handle.classList.remove('active');
            document.body.style.cursor = '';
            document.body.style.userSelect = '';
        }
    });
}

// ===== Window Resize =====

function initWindowResize() {
    let resizeTimeout;
    window.addEventListener('resize', () => {
        hideTaskContextMenu();
        clearTimeout(resizeTimeout);
        resizeTimeout = setTimeout(() => {
            terminals.forEach((info) => {
                if (info.fitAddon) {
                    try {
                        info.fitAddon.fit();
                    } catch (e) {
                        // Ignore fit errors during resize
                    }
                }
            });
        }, 100);
    });
}

// ===== Utility Functions =====

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// ===== Initialization =====

document.addEventListener('DOMContentLoaded', () => {
    I18n.init();
    Theme.init();
    initConnection();
    loadConfig();
    initResizeHandle();
    initWindowResize();

    document.addEventListener('click', hideTaskContextMenu);
    document.addEventListener('scroll', hideTaskContextMenu, true);
    document.addEventListener('contextmenu', (event) => {
        if (!event.target.closest('.task-item') && !event.target.closest('#taskContextMenu')) {
            hideTaskContextMenu();
        }
    });
    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            hideTaskContextMenu();
        }
    });
});
