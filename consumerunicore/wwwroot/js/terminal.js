// xterm.js interop bridge for UniCore WebShell.
// Loaded by App.razor; consumed by WebShell.razor via IJSRuntime.

window.terminalInterop = (function () {
  // Active terminal instances keyed by containerId.
  const instances = {};
  const textEncoder = new TextEncoder();

  function bytesToBase64(bytes) {
    let binary = "";
    const chunkSize = 0x8000;
    for (let i = 0; i < bytes.length; i += chunkSize) {
      const slice = bytes.subarray(i, i + chunkSize);
      binary += String.fromCharCode.apply(null, slice);
    }
    return btoa(binary);
  }

  function base64ToBytes(base64Data) {
    const binary = atob(base64Data);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  }

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
        fontFamily:
          '"Cascadia Code", "Fira Code", "JetBrains Mono", "Consolas", monospace',
        fontSize: 14,
        lineHeight: 1.2,
        scrollback: 10000,
        convertEol: true,
        theme: {
          background: "#050914",
          foreground: "#c9f8da",
          cursor: "#00e676",
          cursorAccent: "#050914",
          selectionBackground: "rgba(0,230,118,0.25)",
          black: "#1a1a2e",
          brightBlack: "#555577",
          red: "#ff5252",
          brightRed: "#ff5252",
          green: "#00e676",
          brightGreen: "#69ff96",
          yellow: "#ffab40",
          brightYellow: "#ffd180",
          blue: "#448aff",
          brightBlue: "#82b1ff",
          magenta: "#ea80fc",
          brightMagenta: "#ea80fc",
          cyan: "#64fcda",
          brightCyan: "#a7fdeb",
          white: "#c9f8da",
          brightWhite: "#ffffff",
        },
      });

      const fitAddon = new FitAddon.FitAddon();
      term.loadAddon(fitAddon);

      const container = document.getElementById(containerId);
      if (!container) {
        console.error(
          "terminalInterop.init: container not found:",
          containerId,
        );
        return;
      }

      term.open(container);
      fitAddon.fit();
      setTimeout(() => term.focus(), 50);

      // Forward keystrokes/paste to the server as base64-encoded UTF-8.
      term.onData(function (data) {
        const b64 = bytesToBase64(textEncoder.encode(data));
        dotnetRef
          .invokeMethodAsync("ReceiveInput", b64)
          .catch((e) => console.error("[WebShell] ReceiveInput failed:", e));
      });

      // Keep browser shortcuts from stealing terminal navigation keys.
      const customKeyHandler = function (event) {
        const hasSelection = !!term.getSelection();
        const withCtrlOrMeta = event.ctrlKey || event.metaKey;
        const isCopy =
          withCtrlOrMeta &&
          event.code === "KeyC" &&
          (event.shiftKey || hasSelection);
        const isPaste =
          (withCtrlOrMeta && event.code === "KeyV") ||
          (event.shiftKey && event.code === "Insert");

        if (isCopy && hasSelection) {
          navigator.clipboard.writeText(term.getSelection()).catch(() => {});
          event.preventDefault();
          return false;
        }

        if (isPaste) {
          navigator.clipboard
            .readText()
            .then((text) => {
              if (text) term.paste(text);
            })
            .catch(() => {});
          event.preventDefault();
          return false;
        }

        if (
          event.key === "ArrowUp" ||
          event.key === "ArrowDown" ||
          event.key === "ArrowLeft" ||
          event.key === "ArrowRight"
        ) {
          event.preventDefault();
        }

        return true;
      };
      term.attachCustomKeyEventHandler(customKeyHandler);

      const pasteHandler = function (event) {
        const text = event.clipboardData
          ? event.clipboardData.getData("text")
          : "";
        if (!text) return;
        event.preventDefault();
        term.paste(text);
      };

      const keydownHandler = function (event) {
        if (
          event.key === "ArrowUp" ||
          event.key === "ArrowDown" ||
          event.key === "ArrowLeft" ||
          event.key === "ArrowRight"
        ) {
          event.preventDefault();
        }
      };

      // Re-focus the terminal whenever the user clicks inside the wrapper,
      // in case a Blazor re-render caused focus to be lost.
      const clickHandler = function () {
        term.focus();
      };
      container.addEventListener("click", clickHandler);
      container.addEventListener("paste", pasteHandler);
      container.addEventListener("keydown", keydownHandler);

      if (term.textarea) {
        term.textarea.addEventListener("paste", pasteHandler);
      }

      // Notify server when the user resizes the window.
      const resizeObserver = new ResizeObserver(function () {
        fitAddon.fit();
        dotnetRef.invokeMethodAsync("ReceiveResize", term.cols, term.rows);
      });
      resizeObserver.observe(container);

      const windowResizeHandler = function () {
        fitAddon.fit();
        dotnetRef.invokeMethodAsync("ReceiveResize", term.cols, term.rows);
      };
      window.addEventListener("resize", windowResizeHandler);

      instances[containerId] = {
        term,
        fitAddon,
        resizeObserver,
        dotnetRef,
        clickHandler,
        pasteHandler,
        keydownHandler,
        windowResizeHandler,
      };
    },

    // Write a chunk of server output (base64-encoded raw bytes) to the terminal.
    write: function (containerId, base64Data) {
      const inst = instances[containerId];
      if (!inst) return;
      try {
        const bytes = base64ToBytes(base64Data);
        inst.term.write(bytes);
      } catch (e) {
        console.error("terminalInterop.write error:", e);
      }
    },

    // Trigger a manual fit (e.g. after layout changes).
    fit: function (containerId) {
      const inst = instances[containerId];
      if (inst) {
        inst.fitAddon.fit();
        inst.dotnetRef.invokeMethodAsync(
          "ReceiveResize",
          inst.term.cols,
          inst.term.rows,
        );
      }
    },

    // Clean up when the Blazor component is disposed.
    dispose: function (containerId) {
      const inst = instances[containerId];
      if (inst) {
        inst.resizeObserver.disconnect();
        window.removeEventListener("resize", inst.windowResizeHandler);
        const container = document.getElementById(containerId);
        if (container) {
          container.removeEventListener("click", inst.clickHandler);
          container.removeEventListener("paste", inst.pasteHandler);
          container.removeEventListener("keydown", inst.keydownHandler);
        }
        if (inst.term.textarea) {
          inst.term.textarea.removeEventListener("paste", inst.pasteHandler);
        }
        inst.term.dispose();
        delete instances[containerId];
      }
    },
  };
})();
