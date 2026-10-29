// Glue between the console page (Blazor) and xterm.js: the page owns the shell process, this owns the screen.
// xterm is vendored in lib/xterm so the page also works offline and inside the desktop shell's WebView.

const loaded = {};
let term = null;
let observer = null;
let hostElement = null;
let onContextMenu = null;

function loadScript(src) {
    return loaded[src] ??= new Promise((resolve, reject) => {
        const el = document.createElement('script');
        el.src = src;
        el.onload = resolve;
        el.onerror = () => reject(new Error('Impossibile caricare ' + src));
        document.head.appendChild(el);
    });
}

function loadCss(href) {
    if (document.querySelector(`link[href="${href}"]`)) return;
    const el = document.createElement('link');
    el.rel = 'stylesheet';
    el.href = href;
    document.head.appendChild(el);
}

async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return;
    } catch { /* no clipboard permission: use the old way below */ }
    const box = document.createElement('textarea');
    box.value = text;
    box.style.position = 'fixed';
    box.style.opacity = '0';
    document.body.appendChild(box);
    box.select();
    try { document.execCommand('copy'); } finally { box.remove(); term?.focus(); }
}

// Same habits as the Windows console: Ctrl+C copies only when something is selected (otherwise it stays the
// interrupt), Ctrl+Shift+C / Ctrl+Insert always copy, Ctrl+V / Ctrl+Shift+V / Shift+Insert paste, and a right
// click copies the selection or pastes the clipboard.
function copySelection() {
    if (term.hasSelection()) {
        copyText(term.getSelection());
        term.clearSelection();
    }
}

function handleKey(ev) {
    if (ev.type !== 'keydown') return true;
    const ctrl = ev.ctrlKey || ev.metaKey;
    const key = ev.key.toLowerCase();

    if ((ctrl && key === 'c' && (ev.shiftKey || term.hasSelection())) || (ctrl && ev.key === 'Insert')) {
        copySelection();
        ev.preventDefault();
        return false;
    }

    // Returning false stops xterm from typing Ctrl+V into the shell; the browser's own paste event then reaches xterm.
    if ((ctrl && key === 'v') || (ev.shiftKey && ev.key === 'Insert')) return false;

    return true;
}

// Returns [cols, rows] once the terminal is laid out, so the shell can start at the right size.
export async function init(host, dotNetRef) {
    loadCss('lib/xterm/xterm.css');
    await loadScript('lib/xterm/xterm.js');
    await loadScript('lib/xterm/addon-fit.js');

    dispose();

    term = new window.Terminal({
        cursorBlink: true,
        fontFamily: "'Cascadia Mono', Consolas, 'Courier New', monospace",
        fontSize: 14,
        scrollback: 5000,
        theme: { background: '#012456', foreground: '#eeedf0' },
    });
    const fit = new window.FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(host);
    fit.fit();

    term.onData(data => dotNetRef.invokeMethodAsync('OnInput', data));
    term.onResize(size => dotNetRef.invokeMethodAsync('OnResize', size.cols, size.rows));

    observer = new ResizeObserver(() => {
        try { fit.fit(); } catch { /* host not laid out yet */ }
    });
    observer.observe(host);

    term.attachCustomKeyEventHandler(handleKey);

    hostElement = host;
    onContextMenu = async ev => {
        ev.preventDefault();
        if (term.hasSelection()) {
            copySelection();
            return;
        }
        try {
            const text = await navigator.clipboard.readText();
            if (text) term.paste(text);
        } catch { /* clipboard not readable: nothing to paste */ }
        term.focus();
    };
    host.addEventListener('contextmenu', onContextMenu);

    host.addEventListener('click', () => term.focus());
    term.focus();
    return [term.cols, term.rows];
}

export function write(text) { term?.write(text); }
export function clear() { term?.reset(); }
export function focus() { term?.focus(); }

export function dispose() {
    if (hostElement && onContextMenu) hostElement.removeEventListener('contextmenu', onContextMenu);
    hostElement = null;
    onContextMenu = null;
    observer?.disconnect();
    observer = null;
    term?.dispose();
    term = null;
}
