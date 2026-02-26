// ===== RepoOPS Client Application =====

/** @type {signalR.HubConnection} */
let connection = null;

/** @type {Map<string, {terminal: Terminal, fitAddon: FitAddon, taskId: string, isRunning: boolean}>} */
const terminals = new Map();

/** Current active tab execution ID */
let activeTabId = null;

/** Running task count */
let runningCount = 0;

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
        html += `<div class="task-group-items" id="${groupId}">`;

        if (group.tasks) {
            group.tasks.forEach(task => {
                const icon = task.icon || '▶️';
                const desc = task.description ? `title="${escapeHtml(task.description)}"` : '';
                html += `<div class="task-item" ${desc} data-task-id="${escapeHtml(task.id)}" onclick="runTask('${escapeHtml(task.id)}', '${escapeHtml(task.name)}')">`;
                html += `<span class="task-icon">${icon}</span>`;
                html += `<span class="task-name">${escapeHtml(task.name)}</span>`;
                html += `</div>`;
            });
        }

        html += `</div></div>`;
    });

    container.innerHTML = html;
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

// ===== Terminal Management =====

function createTerminalTab(executionId, taskName, taskId) {
    // Hide welcome screen
    document.getElementById('welcomeScreen').style.display = 'none';

    // Create terminal instance
    const terminal = new Terminal({
        theme: {
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
        },
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
    const btn = document.getElementById('btnStop');
    if (!activeTabId) {
        btn.disabled = true;
        return;
    }
    const info = terminals.get(activeTabId);
    btn.disabled = !(info && info.isRunning);
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
    initConnection();
    loadConfig();
    initResizeHandle();
    initWindowResize();
});
