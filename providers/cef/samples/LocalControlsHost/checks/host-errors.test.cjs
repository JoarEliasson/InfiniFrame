// Exercise only the diagnostic forwarder in a synthetic JS context, never a browser or renderer.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../wwwroot/host-errors.js'), 'utf8');
let checks = 0;
function check(test, name) { assert.ok(test, name); console.log('PASS: ' + name); checks++; }
function context() {
    const events = {};
    const originals = [];
    const posts = [];
    let tick, cleared = false;
    const window = { addEventListener(name, callback) { events[name] = callback; } };
    const console = { error(...values) { originals.push(values); } };
    vm.runInNewContext(source, { window, console,
        setInterval(callback, delay) { assert.equal(delay, 250); tick = callback; return 1; },
        clearInterval(id) { assert.equal(id, 1); cleared = true; }
    });
    return { window, console, events, originals, posts,
        attach() { window.infiniframe = { host: { postData(data) { posts.push(JSON.parse(data)); } } }; },
        tick() { tick(); }, get cleared() { return cleared; } };
}
const early = context();
for (let i = 0; i < 10; i++) early.console.error('early ' + i);
check(early.originals.length === 10, 'All console errors remain visible before bridge attachment');
early.attach(); early.tick();
check(early.posts.length === 4, 'Bridge-unavailable queue is bounded at four errors');
check(early.posts.every(p => p.version === 2 && p.id === 'local-controls:error' && p.command === 'Post'), 'Forwarder uses the local public v2 Post envelope');
early.console.error('x'.repeat(15000));
check(early.posts.at(-1).data.length === 12000, 'Forwarded text is capped at 12000 characters');
for (let i = 0; i < 50; i++) early.console.error('late ' + i);
check(early.posts.length === 20 && early.originals.length === 61, 'Twenty-message lifetime cap preserves original console output');
early.tick();
check(early.cleared, 'Retry interval ends after the lifetime cap');
const missing = context();
for (let i = 0; i < 39; i++) missing.tick();
check(!missing.cleared, 'Bridge retry budget lasts 39 ticks');
missing.tick();
check(missing.cleared, 'Missing bridge retry ends at 40 ticks');
const closing = context();
closing.console.error('queued before pagehide');
closing.events.pagehide(); closing.attach(); closing.tick();
check(closing.cleared && closing.posts.length === 0, 'Pagehide stops retries and clears queued document messages');
const bridgeFailure = context();
bridgeFailure.window.infiniframe = { host: { postData() { throw new Error('document retired'); } } };
bridgeFailure.console.error('original failure');
check(bridgeFailure.originals.length === 1, 'A failed bridge does not hide the original console error');
const events = context(); events.attach();
events.events.error({ message: 'script failure' });
events.events.unhandledrejection({ reason: 'rejected interop' });
check(events.posts.length === 2 && events.posts[1].data === 'rejected interop', 'Script errors and unhandled rejections reach diagnostics');
console.log(`Completed ${checks} synthetic diagnostic-forwarder checks. No browser qualification is claimed.`);
