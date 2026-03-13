// ===== PTY Terminal — Interactive ConPTY sessions via SignalR =====

const PtyTerminal = (() => {
    /** @type {signalR.HubConnection} */
    let ptyConnection = null;

    /** Map sessionId → { terminal, fitAddon, taskId, isRunning } (also stored in global `terminals`) */
    const ptySessions = new Map();

    function init() {
        ptyConnection = new signalR.HubConnectionBuilder()
            .withUrl('/hub/pty')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        ptyConnection.on('PtyOutput', (sessionId, text) => {
            const info = terminals.get(sessionId);
            if (info && info.terminal) {
                info.terminal.write(text);
            }
        });

        ptyConnection.on('PtyCompleted', (sessionId, exitCode) => {
            // Only handle sessions started by PtyTerminal (Practice tab)
            if (!ptySessions.has(sessionId)) return;

            const info = terminals.get(sessionId);
            if (info) {
                info.isRunning = false;
                updateTabStatus(sessionId,
                    exitCode === 0 ? 'success' : exitCode === -1 ? 'cancelled' : 'error');
            }
            ptySessions.delete(sessionId);
            updateRunningCount(-1);
            updateStopButton();
            updateTaskItemStatus(sessionId, false);
        });

        startPtyConnection();
    }

    async function startPtyConnection() {
        try {
            await ptyConnection.start();
        } catch (err) {
            console.error('PtyHub connection failed:', err);
            setTimeout(startPtyConnection, 5000);
        }
    }

    async function runTask(taskId, taskName) {
        if (!ptyConnection || ptyConnection.state !== signalR.HubConnectionState.Connected) {
            alert(I18n.t('alert.notConnected'));
            return;
        }

        // Measure terminal area so we can tell the server what size to create
        const container = document.getElementById('terminalContainer');
        const approxCols = Math.max(40, Math.floor((container.clientWidth - 20) / 8.4));
        const approxRows = Math.max(10, Math.floor((container.clientHeight - 10) / 18));

        try {
            const sessionId = await ptyConnection.invoke('StartPtyTask', taskId, approxCols, approxRows);
            createPtyTerminalTab(sessionId, taskName, taskId);
            updateRunningCount(1);
            updateTaskItemStatus(sessionId, true);
        } catch (err) {
            console.error('Failed to start PTY task:', err);
            alert(`${I18n.t('alert.startFailed')} ${err.message || err}`);
        }
    }

    function createPtyTerminalTab(sessionId, taskName, taskId) {
        document.getElementById('welcomeScreen').style.display = 'none';

        const terminal = new Terminal({
            theme: Theme.getTerminalTheme(),
            fontFamily: 'Consolas, "Courier New", monospace',
            fontSize: 14,
            lineHeight: 1.2,
            scrollback: 10000,
            cursorBlink: true,
            disableStdin: false,
            convertEol: false
        });

        const fitAddon = new FitAddon.FitAddon();
        terminal.loadAddon(fitAddon);

        const wrapper = document.createElement('div');
        wrapper.className = 'terminal-wrapper';
        wrapper.id = `terminal-${sessionId}`;
        document.getElementById('terminalContainer').appendChild(wrapper);

        terminal.open(wrapper);
        fitAddon.fit();

        // Wire keyboard input → server
        terminal.onData((data) => {
            if (ptyConnection && ptyConnection.state === signalR.HubConnectionState.Connected) {
                ptyConnection.invoke('SendPtyInput', sessionId, data).catch(() => {});
            }
        });

        // Wire resize → server
        terminal.onResize(({ cols, rows }) => {
            if (ptyConnection && ptyConnection.state === signalR.HubConnectionState.Connected) {
                ptyConnection.invoke('ResizePty', sessionId, cols, rows).catch(() => {});
            }
        });

        const info = { terminal, fitAddon, taskId, isRunning: true };
        terminals.set(sessionId, info);
        ptySessions.set(sessionId, info);

        addTab(sessionId, taskName);
        switchTab(sessionId);
    }

    async function stopSession(sessionId) {
        if (!ptyConnection || ptyConnection.state !== signalR.HubConnectionState.Connected) return;
        try {
            await ptyConnection.invoke('StopPtyTask', sessionId);
        } catch (err) {
            console.error('Failed to stop PTY session:', err);
        }
    }

    /** Check whether a given session was started by PtyTerminal */
    function isPtySession(executionId) {
        return ptySessions.has(executionId);
    }

    return { init, runTask, stopSession, isPtySession };
})();

window.PtyTerminal = PtyTerminal;
