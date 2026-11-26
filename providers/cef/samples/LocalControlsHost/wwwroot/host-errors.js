// Local diagnostics only. Preserve the browser's console and bound traffic, text and bridge retries.
(() => {
    const originalError = console.error.bind(console);
    const pending = [];
    let sent = 0;
    let attempts = 0;
    let timer = null;
    function flush() {
        if (!window.infiniframe?.host?.postData) return;
        while (pending.length && sent < 20) {
            try {
                window.infiniframe.host.postData(JSON.stringify({
                    version: 2, id: "local-controls:error", command: "Post", data: pending[0]
                }));
                pending.shift();
                sent++;
            } catch { break; } // A retiring document may no longer have its host bridge.
        }
        if (sent >= 20) pending.length = 0;
    }
    function forward(value) {
        if (sent >= 20) return;
        if (pending.length < 4) pending.push(String(value).slice(0, 12000));
        flush();
    }
    console.error = (...values) => {
        originalError(...values);
        forward(values.map(value => value instanceof Error ? value.stack || value.message : String(value)).join(" "));
    };
    window.addEventListener("error", event => forward(event.error?.stack || event.message));
    window.addEventListener("unhandledrejection", event => forward(event.reason?.stack || event.reason));
    window.addEventListener("load", flush);
    // The bridge may attach after load. Retry for at most ten seconds, then leave console output intact.
    timer = setInterval(() => {
        flush();
        if (++attempts >= 40 || sent >= 20) clearInterval(timer);
    }, 250);
    window.addEventListener("pagehide", () => { clearInterval(timer); pending.length = 0; }, { once: true });
})();
