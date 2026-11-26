// One random marker per document, and the JS function a delayed page-1 callback tries to reach.
window.__documentMarker = crypto.randomUUID();
window.__probeStamps = [];
window.probeStamp = (value) => {
    window.__probeStamps.push(value);
    document.documentElement.dataset.lastStamp = value;
    return true;
};
window.__cspViolations = [];
document.addEventListener("securitypolicyviolation", (event) => {
    window.__cspViolations.push({ directive: event.violatedDirective, blocked: event.blockedURI || "inline" });
});
