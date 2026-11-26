# Native CEF companion

This standalone CMake project builds `InfiniFrame.Cef.Host` and the official
`libcef_dll_wrapper` against stable CEF API **15600**. Supply an independently
verified official CEF **156.0.8+g426920c+chromium-156.0.8078.48** binary distribution
for the target architecture (`scripts/cef_restore.py` restores and verifies the
pinned one). This project does not download dependencies.

## Build and install

Use a fresh build directory, a matching native compiler, and a dedicated
application-owned install directory. From `providers/cef`, on Windows:

```powershell
cmake -S native -B build/cef -A x64 `
  "-DCEF_ROOT=C:/dependencies/cef" -Dapi_version=15600
cmake --build build/cef --config Release --target InfiniFrame.Cef.Host
cmake --install build/cef --config Release --prefix C:/applications/cef-payload
```

Use `-A ARM64` with a matching Windows ARM64 distribution and compiler. On Linux,
use the native x64 or ARM64 compiler and matching Linux distribution:

```sh
cmake -S native -B build/cef -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DCEF_ROOT=/opt/dependencies/cef -Dapi_version=15600
cmake --build build/cef --target InfiniFrame.Cef.Host
cmake --install build/cef --prefix /opt/applications/cef-payload
```

Linux installation explicitly installs the host as a program (0755). Other
payload files use normal data permissions. No SUID `chrome-sandbox` is installed.
Verify modes on a normal POSIX filesystem; DrvFS can conceal permission defects.
The host uses `$ORIGIN` library lookup. `scripts/cef-build.ps1` writes
`cef-backend.json`; changing the host requires regenerating its hash inventory.

Windows uses the matching official `bootstrapc.exe`, renamed
`InfiniFrame.Cef.Host.exe`, with the companion DLL. CMake grants LPAC read/execute
access only to the dedicated built/installed runtime directories. A null sandbox
bootstrap pointer is rejected. Linux requires a desktop session and working
unprivileged user namespaces, and must run as an ordinary user.

The payload includes CEF libraries, snapshots, ICU/resource packs, locales, and
unmodified upstream notices. Use the official distribution's resource inventory;
do not flatten locale folders. macOS is rejected by this project.

## Lifetime and security

The companion owns native CEF objects, windows and the CEF UI loop. It executes
CEF subprocess handling before reading a launch handshake. Managed components
and application services remain in the consumer process. Anonymous inherited
pipes carry the authenticated launch handshake and bounded binary protocol;
stdout carries protocol data only. Host failure diagnostics on stderr avoid
message/resource bodies; CEF's own diagnostics may include paths and platform
details.

Sandboxing is mandatory, remote debugging is disabled, and browser command-line
arguments are rejected. The `app` scheme is registered consistently in browser
and renderer processes. Only `app://localhost/` is accepted for application
navigation/resources; external origins, popups, downloads, permissions, file
and JavaScript dialogs are denied. Resources carry a same-origin CSP and
`nosniff`. No certificate, origin or sandbox bypass flags are used. The managed
host may relax only inline style attributes (`style-src-attr 'unsafe-inline'`);
`<style>` elements and other-origin stylesheets stay governed by
`default-src 'self'`. The optional panel rendering switches
(`force-device-scale-factor`, `disable-pinch`,
`disable-features=ElasticOverscroll,OverscrollHistoryNavigation`) are fixed
strings selected by Hello flags, never taken from the command line or environment.

Each launch creates a private profile, removed best-effort on clean shutdown.
A failed profile removal emits at most one bounded stderr diagnostic with the
fixed label `profile cleanup failed`, the error category (at most 32 characters)
and its numeric error code. Success is silent. No paths or contents are logged,
and the error does not identify a particular file or handle owner. This reports
the existing single removal attempt; it does not retry or repair cleanup. A
failed stderr write can lose the diagnostic. Native exit code 0 and a protocol
Shutdown acknowledgement do not certify that the profile was removed.

The managed provider separately owns cleanup of its run directory after
companion/process-tree teardown and reports that outcome. Its post-exit cleanup
policy is distinct from the native best-effort attempt; neither this diagnostic
nor a successful shutdown guarantees profile absence for every workload.

On Windows, a fixed browser-process setting routes CEF error logging to inherited
stderr at unchanged severity. A CEF log file inside the profile can otherwise
remain open after `CefShutdown` and prevent removal. The managed connection drains
stderr and retains a bounded diagnostic tail; native validation captures it separately.
The smoke test verifies profile removal after both graceful shutdown and pipe EOF.
Linux also atomically creates a short owner-only `/tmp/ifc-XXXXXX` runtime
directory for Chromium socket files, independent of the profile's XDG location,
and removes its own directory on clean shutdown. Context generations are native identities: full document
replacement retires pending commands/resources; fragment navigation retains
its actual V8 context and generation. Document registration confirms the current
renderer context before releasing bootstrap messages. Closed notifications
follow native `OnBeforeClose`; EOF closes all windows and shuts CEF down.

## Protocol 3

Frames have a little-endian u32 length (maximum 8 MiB), then u16 version **3**,
u16 kind, and u64 request/view/document identities, followed by the body. The
header after length is 28 bytes. Strings/blobs have u32 byte counts; strings
must be valid UTF-8. Any other protocol version is rejected before
initialization (exit 76) with the diagnostic `protocol version mismatch: this
companion implements protocol 3, the managed provider sent protocol N`. Protocol 3
differs from protocol 2 only in the Hello and Create bodies below.

- Hello carries 32 raw nonce bytes, the application origin, a u32 host-option
  bit set and an f64 device scale factor (0: the platform's; otherwise 0.5..4).
  Options: 1 = inline style attributes (CSP `style-src-attr 'unsafe-inline'`),
  2 = no pinch zoom, 4 = no elastic overscroll or overscroll history navigation.
  Unknown bits, an unsupported factor or a missing field end the companion with
  exit 76 before CEF initializes. Success echoes the nonce, RID, full compiled CEF
  version and the applied option bits. Credentials never reach renderer state,
  command lines or diagnostics.
- Create carries title, x/y/width/height, initial URL, the existing generated
  InfiniFrame bootstrap script, then a u32 window-flag set and four i32 size
  limits (minimum width/height, maximum width/height; 0 = unconstrained, else at
  most 16384). Flags: 1 = frameless (no frame or title bar; the companion fails the
  initial bounds if the window still reports a non-client area), 2 = fixed size
  (no user resizing or maximize affordance; programmatic bounds and states still
  apply; on Linux, where non-resizable views windows also refuse programmatic
  resizing, the window stays resizable for views and pins equal minimum and
  maximum size hints to the last requested size), 4 = content bounds (bounds, queries and limits describe the client area;
  the companion measures the frame and corrects the window bounds, up to two
  corrections). Without flag 4, bounds and limits are outer window bounds, which
  for a frameless window equal the content area. Unknown flags, invalid limits or
  an initial size outside them are rejected; later size changes must also obey
  them. A move preserves the currently observed size even outside these limits.
  Native minimum/maximum sizes are suspended while CEF reports fullscreen, for
  fixed and resizable windows alike, so normal-window limits cannot cap the
  display-sized window. Exiting fullscreen restores the existing limits/pin
  policy. This does not change bounds-request validation or the state refusal
  described below.
  At most 32 views are live. Bounds are screen DIPs. Under Wayland a client cannot
  place its window: the compositor positions it and the reported origin stays the
  requested one, so a frameless panel window is normally created at 0,0.
- Navigate (kind 4) success has exactly one byte: 1 for a fragment change retaining
  the document; 0 for document navigation. The success header carries the actual
  resulting document generation. Full-navigation success follows confirmed
  Document101 registration; fragment success acknowledges submission and its
  actual URL changes asynchronously. An identical fragment target retains
  its context; an identical fragmentless target requests a reload.
  A target with no fragment requests a full document load even when removing
  the current fragment; renderer-side `location.hash = ''` retains its context.
  Overlapping full-navigation requests for one view are rejected; failures,
  closure and the bounded deadline finish outstanding navigation requests.
- Window-state query (kind 12) success contains five i32 values (x, y, width,
  height, flags), a counted title, then the counted actual CEF main-frame URL.
  Flags are fullscreen=1, minimized=2, maximized=4, focused/active=8.
- Bounds commands acknowledge after actual CEF bounds settle. Each per-view
  operation has a two-second limit and at most two measured position
  corrections for window-manager frame/client differences. Size constraints
  and coordinate limits fail explicitly. Bounds require a normal window with no
  pending state transition: minimized, maximized and fullscreen windows reject
  them without cancelling a transition or changing saved normal bounds/pins.
  Same-size moves compare against measured bounds in the query's coordinate mode;
  they bypass configured size limits but can still receive this state refusal or
  a compositor refusal. The size is never clamped to turn a move into a resize.
  Accepted bounds/state requests or closure cancel an outstanding geometry
  operation. On Linux, fixed-window normal restore bounds survive until geometry
  converges successfully. Failure, timeout or supersession restores the previous
  internal pin and retains the normal target for an explicit bounds retry or
  Restore. This is not atomic rollback of submitted OS bounds or published size
  hints: queries continue to report actual geometry, and a retry may also fail.
  Managed creation sends an initial bounds command after allocation and before
  exposing WindowCreated/Ready. No correction
  runs after completion, and queries always report actual CEF bounds.
- Fullscreen, maximize, minimize and restore commands acknowledge only after the
  requested state is observable through CEF. Unobservable state transitions fail
  within two seconds. In particular, a compositor that does not report iconic/
  hidden state cannot qualify minimization merely because its request was sent.
- SetIcon (kind 13) accepts counted PNG bytes, at most 1 MiB and 1..512 pixels
  per dimension.
- SendMessage passes an opaque V8 string argument; Evaluate returns synchronous
  JSON results. Stale document operations return `CEF_DOCUMENT_RETIRED`.
  Missing/throwing callbacks, Promises and unsupported evaluations fail visibly.
- Resource responses echo request/view/generation, including generation zero
  before document registration. GET/HEAD, binary/empty bodies and statuses
  200..599 are supported. MIME is at most 256 UTF-8 bytes without CR/LF/NUL.
  Malformed live responses complete with HTTP 502 and release their slot;
  mismatched identities cannot consume a live request, and late replies are ignored.

Transport queues are bounded at 256 frames/64 MiB per direction. Native renderer
submissions and renderer-originated bridge queues also have finite budgets.
At most 16 resource leases share a 64 MiB native payload budget. IO continuations
are posted asynchronously. Pending document commands have 30-second deadlines;
transport failure, overflow or renderer timeout shuts down visibly.

## Native regression tests

Python 3 tests launch the real installed companion with its normal sandbox.
Set the host, working directory outside the payload, RID and optional log folder:

```sh
export INFINIFRAME_CEF_SMOKE_HOST=/opt/applications/cef-payload/InfiniFrame.Cef.Host
export INFINIFRAME_CEF_SMOKE_CWD=/tmp
export INFINIFRAME_CEF_SMOKE_RID=linux-x64
export INFINIFRAME_CEF_SMOKE_LOGS=/tmp/cef-test-logs
python3 native/tests/smoke.py
python3 native/tests/navigation_resource_regression.py
python3 native/tests/bootstrap_regression.py
python3 native/tests/protocol_version_regression.py
python3 native/tests/check_posix_install.py
python3 native/tests/check_library_paths.py
python3 native/tests/geometry_state_regression.py
python3 native/tests/window_style_regression.py
python3 native/tests/fixed_window_state_regression.py
```

On Windows set the same environment variables to Windows paths, using the
installed `.exe` and `win-x64`/`win-arm64` RID. The POSIX permission and ELF
library-path checks run only on Linux. Tests inspect actual renderer
sandbox state (Windows token integrity; Linux Seccomp/NoNewPrivs), exercise
multiwindow messaging/resources/modules/state/icon/navigation/cancellation and
shutdown, and repeat malformed resource completions beyond the 16-slot limit.
The fragment regression checks retained JavaScript state and immutable generation
against actual URL queries, then verifies that a reload replaces the context.
The bootstrap regression uses the existing generated adapter and checks that a
failed initialization is correlated with the committed candidate/navigation
confirmation, fails the pending navigation, and never announces a usable document.
These are native contract tests; managed Blazor acceptance and package-consumer
validation remain separate requirements. Build success alone does not qualify
a platform; record actual results and any missing environments.
The fixed-window regression requires a desktop larger than 700x500 DIPs. It
combines constrained fixed normal bounds, maximize/fullscreen, rejected in-range
resizes and preserved-size moves outside the limits, then verifies restored
normal geometry and X11 size hints (when `xprop` is available). It also checks
pending transitions, supersession, cancellation/retry and close/reopen. Public
provider cases use outer bounds; framed content bounds are tested only through
the direct native protocol. Constrained resizable cases also enter/exit fullscreen.
Fullscreen checks require a viewport covering its display as well as bounds
outside the configured limits; a fullscreen flag alone is insufficient. Failure
logs include QueryState, JavaScript viewport/screen/DPR, and, on Windows, an
independent HWND/client/monitor rectangle and DPI probe. After exit, invalid
protocol resizes must still fail. Windows tests additionally resize only the
test-owned HWND through `SetWindowPos` to verify native minimum/maximum limits
independently of protocol validation, then restore normal bounds through the
protocol. X11 checks the restored fixed pin or configured hints when available.
Run this and the geometry/style regressions against
the freshly built companion; an older protocol-2 payload cannot qualify them.
Real compositor timeout/refusal paths and platforms without inspectable X11
hints still require platform qualification; syntax checking these scripts does
not execute native behavior.
The default state tests require observable minimization. On a WM already proven
unable to report it, `INFINIFRAME_CEF_EXPECT_MINIMIZE=unobservable` tests explicit
bounded rejection and prints `EXPECTED_FAILURE`; this diagnostic mode does not
qualify minimization or replace a passing standard-WM acceptance run.

For a dedicated Linux GCC/Clang build, use a fresh output directory and
`-DINFINIFRAME_CEF_ENABLE_SANITIZERS=ON`. The default instrumentation is
`address,undefined`; select `-DINFINIFRAME_CEF_SANITIZERS=undefined` for a separate
UBSan run covering actual browser and renderer execution. Sanitizers default OFF.

Address builds add a checked `libInfiniFrame.Cef.AsanAllocator.so` after libcef
and before libc in the dependency order. It routes CEF's explicit glibc allocation
entrypoints and its `RTLD_NEXT` usable-size lookup through ASan. The existing ASan
`__libc_memalign` interceptor retains its TLS bookkeeping. No ownership fallback,
interceptor suppression or sandbox change is used. Default and UBSan-only builds
add neither the adapter nor a subprocess helper.

The stock CEF child sandbox's finite data limits cannot accommodate ASan shadow
mappings. Address builds therefore build/install fixed sibling
`InfiniFrame.Cef.Subprocess` from the same sources, CEF and API, without sanitizer
instrumentation. Only the address-build compile definition selects it through
CEF's supported `browser_subprocess_path`; callers cannot choose another path.
Both executables install as 0755, the adapter as 0644, with origin-only library
lookup. The child keeps its normal sandbox. Effective ASan coverage is the
browser host/transport; **renderer ASan is not qualified**. The independent UBSan
build covers renderer execution. Official CEF and its wrapper remain uninstrumented.

Build the optional standalone allocator controls and run them before interpreting
an address-suite result:

```sh
cmake --build build/cef-asan --target InfiniFrame.Cef.Host InfiniFrame.Cef.AsanControls
python3 native/tests/asan_allocator_regression.py \
  --controls build/cef-asan/asan-controls/Release/InfiniFrame.Cef.AsanControls
```

The controls never load CEF. They verify ABI/data/alignment/null behavior and
the actual `RTLD_NEXT` provider, then require ASan rejection of overflow, UAF,
double-free, and invalid/interior/freed usable-size queries. Assertions stay on
even in Release, and the test removes sanitizer environment overrides.

Run the installed native scripts with default sanitizer settings first and
retain their raw diagnostics. The stock CEF run has separate LeakSanitizer
findings; a passing browser address suite does not qualify leak checking. To
measure address/undefined behavior separately, explicitly set
`ASAN_OPTIONS=detect_leaks=0:halt_on_error=1` in that test process only and report
the result as a **browser address suite**, alongside the default LeakSanitizer
failure. The companion never sets sanitizer options or suppressions itself.
