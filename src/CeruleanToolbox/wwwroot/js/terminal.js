// Glue between the console page (Blazor) and xterm.js: the page owns the shell process, this owns the screen.
// xterm is vendored in lib/xterm so the page also works offline and inside the desktop shell's WebView.

const loaded = {};
let term = null;
let observer = null;

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

    host.addEventListener('click', () => term.focus());
    term.focus();
    return [term.cols, term.rows];
}

export function write(text) { term?.write(text); }
export function clear() { term?.reset(); }
export function focus() { term?.focus(); }

export function dispose() {
    observer?.disconnect();
    observer = null;
    term?.dispose();
    term = null;
}
