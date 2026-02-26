// xterm.js interop bridge for UniCore WebShell.
// Loaded by App.razor; consumed by WebShell.razor via IJSRuntime.

window.terminalInterop = (function () {
    // Active terminal instances keyed by containerId.
    const instances = {};

    return {
        // Called once the terminal container <div> is in the DOM.
        // dotnetRef  – DotNetObjectReference pointing at the WebShell component.
        // containerId – id of the <div> to mount xterm.js into.
        // cols / rows – initial terminal dimensions.
        init: function (dotnetRef, containerId, cols, rows) {
            const term = new Terminal({
                cols: cols || 80,
                rows: rows || 24,
                cursorBlink: true,
                fontFamily: '"Cascadia Code", "Fira Code", "JetBrains Mono", "Consolas", monospace',
                fontSize: 14,
                lineHeight: 1.2,
                scrollback: 5000,
                convertEol: true,
                theme: {
                    background:        '#050914',
                    foreground:        '#c9f8da',
                    cursor:            '#00e676',
                    cursorAccent:      '#050914',
                    selectionBackground: 'rgba(0,230,118,0.25)',
                    black:             '#1a1a2e',
                    brightBlack:       '#555577',
                    red:               '#ff5252',
                    brightRed:         '#ff5252',
                    green:             '#00e676',
                    brightGreen:       '#69ff96',
                    yellow:            '#ffab40',
                    brightYellow:      '#ffd180',
                    blue:              '#448aff',
                    brightBlue:        '#82b1ff',
                    magenta:           '#ea80fc',
                    brightMagenta:     '#ea80fc',
                    cyan:              '#64fcda',
                    brightCyan:        '#a7fdeb',
                    white:             '#c9f8da',
                    brightWhite:       '#ffffff',
                },
            });

            const fitAddon = new FitAddon.FitAddon();
            term.loadAddon(fitAddon);

            const container = document.getElementById(containerId);
            if (!container) {
                console.error('terminalInterop.init: container not found:', containerId);
                return;
            }

            term.open(container);
            fitAddon.fit();
            setTimeout(() => term.focus(), 50);

            // Forward keystrokes/paste to the server as base64-encoded UTF-8.
            term.onData(function (data) {
                // btoa only handles latin-1; use this trick for full UTF-8 support.
                const b64 = btoa(unescape(encodeURIComponent(data)));
                dotnetRef.invokeMethodAsync('ReceiveInput', b64)
                    .catch(e => console.error('[WebShell] ReceiveInput failed:', e));
            });

            // Re-focus the terminal whenever the user clicks inside the wrapper,
            // in case a Blazor re-render caused focus to be lost.
            container.addEventListener('click', () => term.focus());

            // Notify server when the user resizes the window.
            const resizeObserver = new ResizeObserver(function () {
                fitAddon.fit();
                dotnetRef.invokeMethodAsync('ReceiveResize', term.cols, term.rows);
            });
            resizeObserver.observe(container);

            instances[containerId] = { term, fitAddon, resizeObserver, dotnetRef };
        },

        // Write a chunk of server output (base64-encoded raw bytes) to the terminal.
        write: function (containerId, base64Data) {
            const inst = instances[containerId];
            if (!inst) return;
            try {
                const binary = atob(base64Data);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) {
                    bytes[i] = binary.charCodeAt(i);
                }
                inst.term.write(bytes);
            } catch (e) {
                console.error('terminalInterop.write error:', e);
            }
        },

        // Trigger a manual fit (e.g. after layout changes).
        fit: function (containerId) {
            const inst = instances[containerId];
            if (inst) {
                inst.fitAddon.fit();
                inst.dotnetRef.invokeMethodAsync('ReceiveResize', inst.term.cols, inst.term.rows);
            }
        },

        // Clean up when the Blazor component is disposed.
        dispose: function (containerId) {
            const inst = instances[containerId];
            if (inst) {
                inst.resizeObserver.disconnect();
                inst.term.dispose();
                delete instances[containerId];
            }
        },
    };
})();
