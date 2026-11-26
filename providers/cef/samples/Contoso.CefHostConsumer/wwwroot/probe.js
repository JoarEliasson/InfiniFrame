export async function verifyAssets() {
    const icon = await fetch("probe.ico");
    if (!icon.ok) throw new Error(`Binary asset status ${icon.status}`);
    const bytes = new Uint8Array(await icon.arrayBuffer());
    if (bytes.length < 4 || bytes[0] !== 0 || bytes[1] !== 0 || bytes[2] !== 1 || bytes[3] !== 0)
        throw new Error("Binary asset was altered");
    for (const path of ["Contoso.CefHostConsumer.styles.css", "_content/InfiniLore.InfiniFrame.Js/InfiniFrame.js"]) {
        const response = await fetch(path);
        if (!response.ok || (await response.text()).length === 0) throw new Error(`Offline asset missing: ${path}`);
    }
    return "PASS";
}

// Status, size and selected headers for each request, as the browser observed them.
async function resourceStatuses(requests) {
    const results = [];
    for (const [method, path] of requests) {
        try {
            const response = await fetch(path, { method, cache: "no-store" });
            const body = method === "HEAD" ? new ArrayBuffer(0) : await response.arrayBuffer();
            results.push({
                method, path, status: response.status, bytes: body.byteLength,
                contentType: response.headers.get("content-type"),
                csp: response.headers.get("content-security-policy"),
                nosniff: response.headers.get("x-content-type-options"),
                cache: response.headers.get("cache-control")
            });
        }
        catch (error) {
            results.push({ method, path, error: String(error) });
        }
    }
    return results;
}

// Records what the document's CSP does to inline styles (attribute and element) and to inline script.
function inlineStyleProbe() {
    const host = document.createElement("div");
    host.innerHTML = '<span id="csp-attr" style="color: rgb(1, 2, 3)">attr</span><style>#csp-element { color: rgb(4, 5, 6); }</style><span id="csp-element">element</span>';
    document.body.appendChild(host);
    const attr = getComputedStyle(document.getElementById("csp-attr")).color;
    const element = getComputedStyle(document.getElementById("csp-element")).color;
    const script = document.createElement("script");
    script.textContent = "window.__inlineScriptRan = true;";
    document.body.appendChild(script);
    const result = { attributeStyleApplied: attr === "rgb(1, 2, 3)", elementStyleApplied: element === "rgb(4, 5, 6)",
        inlineScriptRan: window.__inlineScriptRan === true, violations: window.__cspViolations.slice(0, 16) };
    host.remove();
    script.remove();
    return result;
}

window.probe = { resourceStatuses, inlineStyleProbe };
