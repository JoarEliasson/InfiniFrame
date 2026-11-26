"""Runs one consumer scenario and watches it from outside (Windows x64 and Linux x64).

Usage: python scenario_driver.py <consumer-executable> <scenario> <run-dir> <state-root> [timeout-seconds]
                                 [kill-consumer-at=<MARKER>]

* Starts the consumer, captures its output to <run-dir>/<scenario>.log, and points the provider's state root at
  <state-root> (CONSUMER_STATE_ROOT). The consumer writes its own report to <run-dir>/report-<scenario>.json.
* Samples the consumer's process tree every 250 ms (count, working set, private bytes, by CEF process type).
* Acts on consumer markers, touching only processes inside this consumer's own tree:
    KILL-RENDERER companion=<pid>  -> kills the renderer processes descended from exactly that companion pid
    KILL-COMPANION companion=<pid> -> kills exactly that companion pid (verified to be the consumer's child)
    NATIVE-CLOSE title=<title>     -> posts WM_CLOSE to the top-level window with that exact title owned by the companion
                                      (Windows only)
  Nothing is ever matched by process name alone; each kill is recorded with pid, parent and creation time.
* At every marker, records listening/established sockets of every process in the tree (expected: none).
* After exit: verifies that no process of the tree is still alive and that the state root holds no new entries.
Writes <run-dir>/driver-<scenario>.json. Requires psutil (pip install psutil).
"""
import ctypes
import ctypes.wintypes
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

import psutil

exe, scenario, run_dir, state_root = sys.argv[1], sys.argv[2], Path(sys.argv[3]), Path(sys.argv[4])
timeout = int(sys.argv[5]) if len(sys.argv) > 5 else 600
# Optional: kill-consumer-at=<MARKER> terminates the consumer this driver started (exact pid) at that marker, to show that
# the provider's job object ends the whole CEF tree when the application process dies without any cleanup.
kill_consumer_at = sys.argv[6].split('=', 1)[1] if len(sys.argv) > 6 and sys.argv[6].startswith('kill-consumer-at=') else None
consumer_killed_at = None
run_dir.mkdir(parents=True, exist_ok=True)
state_root.mkdir(parents=True, exist_ok=True)
state_before = sorted(p.name for p in state_root.iterdir())
report_path = run_dir / f"report-{scenario}.json"
log_path = run_dir / f"{scenario}.log"

env = dict(os.environ)
env["CONSUMER_STATE_ROOT"] = str(state_root)
t0 = time.monotonic()
consumer = subprocess.Popen([exe, scenario, str(report_path)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            env=env, cwd=str(Path(exe).parent), text=True, encoding="utf-8", errors="replace", bufsize=1)
root = psutil.Process(consumer.pid)
root_created = root.create_time()

lock = threading.Lock()
seen = {}          # pid -> {name, ppid, created, type}
peaks = {"processes": 0, "wset": 0, "private": 0, "by_type": {}}
connections_seen = {}  # (type, status, local, remote) -> first seen (s)
samples = 0
markers = []
actions = []
first_marker_s = {}
done = threading.Event()


def proc_type(p):
    try:
        args = p.cmdline()
        for arg in args:
            if arg.startswith("--type="):
                sub = next((a.split("=", 1)[1] for a in args if a.startswith("--utility-sub-type=")), None)
                return arg[7:] + (f" ({sub})" if sub else "")
        return "companion" if p.name().lower() in COMPANION_NAMES else p.name()
    except (psutil.Error, OSError):
        return "?"


COMPANION_NAMES = {"infiniframe.cef.host.exe", "infiniframe.cef.host"}


def tree():
    try:
        return [root] + root.children(recursive=True)
    except psutil.Error:
        return []


def census():
    rows = []
    for p in tree():
        try:
            with p.oneshot():
                mem = p.memory_info()
                private = getattr(mem, "private", None)
                if private is None:  # Linux: resident minus shared pages approximates private memory
                    private = max(0, mem.rss - getattr(mem, "shared", 0))
                rows.append({"pid": p.pid, "ppid": p.ppid(), "created": p.create_time(), "name": p.name(),
                             "type": "consumer" if p.pid == root.pid else proc_type(p),
                             "wset": mem.rss, "private": private, "peak_wset": getattr(mem, "peak_wset", 0)})
        except (psutil.Error, OSError):
            pass
    return rows


def sampler():
    global samples
    while not done.is_set():
        rows = census()
        with lock:
            samples += 1
            for r in rows:
                seen.setdefault(r["pid"], {k: r[k] for k in ("name", "ppid", "created", "type")})
            cef = [r for r in rows if r["type"] != "consumer"]
            peaks["processes"] = max(peaks["processes"], len(cef))
            peaks["wset"] = max(peaks["wset"], sum(r["wset"] for r in rows))
            peaks["private"] = max(peaks["private"], sum(r["private"] for r in rows))
            counts = {}
            for r in cef:
                counts[r["type"]] = counts.get(r["type"], 0) + 1
            for k, v in counts.items():
                peaks["by_type"][k] = max(peaks["by_type"].get(k, 0), v)
        if samples % 4 == 1:  # about once a second: every socket of every process in the tree
            for s in sockets(rows):
                if s.get("status"):
                    connections_seen.setdefault((s["type"], s["status"], s["local"], s["remote"]), round(time.monotonic() - t0, 3))
        done.wait(0.25)


def sockets(rows):
    found = []
    for r in rows:
        try:
            for c in psutil.Process(r["pid"]).net_connections(kind="inet"):
                found.append({"pid": r["pid"], "type": r["type"], "status": c.status,
                              "local": f"{c.laddr.ip}:{c.laddr.port}" if c.laddr else None,
                              "remote": f"{c.raddr.ip}:{c.raddr.port}" if c.raddr else None})
        except (psutil.Error, OSError) as e:
            found.append({"pid": r["pid"], "type": r["type"], "error": type(e).__name__})
    return found


def profiles():
    """Read-only view of this run's live CEF profile(s): top-level entries, installed extension ids, file count."""
    found = []
    for run in state_root.glob("run-*"):
        if run.name in state_before:
            continue
        for profile in (run / "state" / "InfiniFrame" / "Cef" / "Profiles").glob("*"):
            try:
                extensions = profile / "Default" / "Extensions"
                found.append({"run": run.name, "profile": profile.name,
                              "top": sorted(p.name for p in profile.iterdir()),
                              "default": sorted(p.name for p in (profile / "Default").iterdir()) if (profile / "Default").is_dir() else [],
                              "extensions": sorted(p.name for p in extensions.iterdir()) if extensions.is_dir() else [],
                              "files": sum(1 for p in profile.rglob("*") if p.is_file())})
            except OSError as e:
                found.append({"run": run.name, "profile": profile.name, "error": type(e).__name__})
    return found


INTEGRITY = {0x0000: "untrusted", 0x1000: "low", 0x2000: "medium", 0x2100: "medium-plus", 0x3000: "high", 0x4000: "system"}


def proc_sandbox(pid):
    """Linux only: seccomp mode (2 = filter), NoNewPrivs, real uid, and user/PID namespaces versus the consumer's."""
    try:
        status = {}
        for line in Path(f"/proc/{pid}/status").read_text().splitlines():
            key, _, value = line.partition(":")
            status[key] = value.strip()

        def namespace(target, kind):
            return os.readlink(f"/proc/{target}/ns/{kind}")

        return {"seccomp": status.get("Seccomp"), "noNewPrivs": status.get("NoNewPrivs"), "uid": status.get("Uid", "").split()[:1],
                "ownUserNamespace": namespace(pid, "user") != namespace(root.pid, "user"),
                "ownPidNamespace": namespace(pid, "pid") != namespace(root.pid, "pid")}
    except OSError as e:
        return {"error": type(e).__name__}


def token_sandbox(pid):
    """Windows: the process token's integrity level and AppContainer flag (read-only, query-limited access).
    Linux: the /proc view (proc_sandbox)."""
    if sys.platform != "win32":
        return proc_sandbox(pid) if sys.platform.startswith("linux") else None
    w = ctypes.wintypes
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    adv = ctypes.WinDLL("advapi32", use_last_error=True)
    k32.OpenProcess.restype = w.HANDLE
    adv.GetSidSubAuthorityCount.restype = ctypes.POINTER(ctypes.c_ubyte)
    adv.GetSidSubAuthorityCount.argtypes = [ctypes.c_void_p]
    adv.GetSidSubAuthority.restype = ctypes.POINTER(w.DWORD)
    adv.GetSidSubAuthority.argtypes = [ctypes.c_void_p, w.DWORD]
    process = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not process:
        return {"error": f"OpenProcess {ctypes.get_last_error()}"}
    token = w.HANDLE()
    try:
        if not adv.OpenProcessToken(process, 0x0008, ctypes.byref(token)):  # TOKEN_QUERY
            return {"error": f"OpenProcessToken {ctypes.get_last_error()}"}
        returned = w.DWORD()
        app_container = w.DWORD()
        adv.GetTokenInformation(token, 29, ctypes.byref(app_container), 4, ctypes.byref(returned))  # TokenIsAppContainer
        label = ctypes.create_string_buffer(128)
        if not adv.GetTokenInformation(token, 25, label, 128, ctypes.byref(returned)):  # TokenIntegrityLevel
            return {"appContainer": bool(app_container.value), "error": f"integrity {ctypes.get_last_error()}"}
        sid = ctypes.cast(label, ctypes.POINTER(ctypes.c_void_p))[0]
        count = adv.GetSidSubAuthorityCount(sid)[0]
        rid = adv.GetSidSubAuthority(sid, count - 1)[0]
        return {"appContainer": bool(app_container.value), "integrity": INTEGRITY.get(rid, hex(rid))}
    finally:
        if token:
            k32.CloseHandle(token)
        k32.CloseHandle(process)


def snapshot(name):
    rows = census()
    snap = {"marker": name, "at_s": round(time.monotonic() - t0, 3), "processes": len(rows) - 1,
            "by_type": {}, "wset_total": sum(r["wset"] for r in rows), "private_total": sum(r["private"] for r in rows),
            "consumer_wset": next((r["wset"] for r in rows if r["type"] == "consumer"), None),
            "sockets": sockets(rows), "profiles": profiles(),
            "sandbox": [{"pid": r["pid"], "type": r["type"], **(token_sandbox(r["pid"]) or {})} for r in rows]}
    for r in rows:
        if r["type"] != "consumer":
            snap["by_type"][r["type"]] = snap["by_type"].get(r["type"], 0) + 1
    return snap


def owned_companion(pid):
    p = psutil.Process(pid)
    if p.ppid() != root.pid or p.name().lower() not in COMPANION_NAMES or p.create_time() < root_created:
        raise RuntimeError(f"pid {pid} is not this consumer's companion")
    return p


def kill_exact(p, why):
    info = {"why": why, "pid": p.pid, "ppid": p.ppid(), "created": p.create_time(), "type": proc_type(p)}
    p.kill()
    info["killed_at_s"] = round(time.monotonic() - t0, 3)
    actions.append(info)


if sys.platform == "win32":
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.wintypes.BOOL, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)


def close_window(title, companion_pid):
    if sys.platform != "win32":
        raise RuntimeError("native close injection is implemented for Windows (WM_CLOSE) only")
    matches = []

    def visit(hwnd, _):
        length = user32.GetWindowTextLengthW(hwnd)
        if length:
            buffer = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buffer, length + 1)
            if buffer.value == title:
                pid = ctypes.wintypes.DWORD()
                user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                matches.append((hwnd, pid.value))
        return True

    user32.EnumWindows(EnumWindowsProc(visit), 0)
    owned = [(h, p) for h, p in matches if p == companion_pid]
    if len(owned) != 1:
        raise RuntimeError(f"expected one window titled {title!r} owned by {companion_pid}, found {matches}")
    if not user32.PostMessageW(owned[0][0], 0x0010, 0, 0):
        raise ctypes.WinError(ctypes.get_last_error())
    actions.append({"why": "native close (WM_CLOSE posted)", "hwnd": owned[0][0], "pid": companion_pid,
                    "at_s": round(time.monotonic() - t0, 3)})


def handle(line):
    if not line.startswith("MARKER "):
        return
    body = line[7:].strip()
    name = body.split(" ", 1)[0]
    first_marker_s.setdefault(name, round(time.monotonic() - t0, 3))
    try:
        if name == "KILL-RENDERER":
            companion = owned_companion(int(body.split("companion=")[1]))
            # Descendants, not just children: on Linux renderers are forked from the zygote (still this run's tree).
            renderers = [c for c in companion.children(recursive=True) if proc_type(c) == "renderer"]
            if not renderers:
                raise RuntimeError("no renderer child of the companion")
            for r in renderers:
                kill_exact(r, "renderer loss (descendant of this run's companion)")
        elif name == "KILL-COMPANION":
            kill_exact(owned_companion(int(body.split("companion=")[1])), "companion loss (this run's companion)")
        elif name == "NATIVE-CLOSE":
            companion = next(c for c in root.children() if c.name().lower() in COMPANION_NAMES)
            close_window(body.split("title=", 1)[1], companion.pid)
        markers.append(snapshot(name))
        if kill_consumer_at and name == kill_consumer_at:
            global consumer_killed_at
            tree_before = [r for r in census() if r["type"] != "consumer"]
            consumer.kill()  # the consumer this driver started; nothing else
            consumer_killed_at = time.monotonic()
            actions.append({"why": f"consumer killed at marker {name} (exact pid {consumer.pid})", "cef_processes_at_kill": len(tree_before),
                            "at_s": round(consumer_killed_at - t0, 3)})
    except Exception as e:  # recorded; the consumer's own checks then fail visibly
        actions.append({"why": f"marker {name} action failed", "error": f"{type(e).__name__}: {e}"})


threading.Thread(target=sampler, daemon=True).start()
with open(log_path, "w", encoding="utf-8") as log:
    try:
        for line in consumer.stdout:
            log.write(line)
            log.flush()
            handle(line)
            if time.monotonic() - t0 > timeout:
                raise TimeoutError
        code = consumer.wait(timeout=60)
    except (TimeoutError, subprocess.TimeoutExpired):
        consumer.kill()  # only the consumer this driver started
        code = "driver-timeout"
exit_s = round(time.monotonic() - t0, 3)
done.set()
if consumer_killed_at is not None:
    # The tree must die without the application's help: poll every recorded pid (with its creation time) for up to 15 s.
    def alive_pids():
        found = []
        for pid, info in seen.items():
            try:
                if pid != consumer.pid and abs(psutil.Process(pid).create_time() - info["created"]) < 0.01:
                    found.append(pid)
            except psutil.NoSuchProcess:
                pass
        return found
    while alive_pids() and time.monotonic() - consumer_killed_at < 15:
        time.sleep(0.05)
    actions.append({"why": "after consumer kill", "tree_gone_after_s": round(time.monotonic() - consumer_killed_at, 3),
                    "still_alive": alive_pids()})
time.sleep(2.0)
left = []
for pid, info in seen.items():
    try:
        p = psutil.Process(pid)
        if abs(p.create_time() - info["created"]) < 0.01:
            left.append({"pid": pid, **info})
    except psutil.NoSuchProcess:
        pass
state_after = sorted(p.name for p in state_root.iterdir())
types = {}
for info in seen.values():
    types[info["type"]] = types.get(info["type"], 0) + 1
result = {
    "scenario": scenario, "consumer_exit_code": code, "wall_s": exit_s, "first_marker_s": first_marker_s,
    "samples": samples, "sample_interval_s": 0.25,
    "peak": {"cef_processes": peaks["processes"], "tree_wset_bytes": peaks["wset"], "tree_private_bytes": peaks["private"],
             "cef_processes_by_type": peaks["by_type"]},
    "distinct_processes_seen": len(seen) - 1, "distinct_by_type": types,
    "markers": markers, "actions": actions,
    "sockets_seen": [{"type": k[0], "status": k[1], "local": k[2], "remote": k[3], "first_seen_s": v} for k, v in sorted(connections_seen.items(), key=lambda kv: kv[1])],
    "listening_sockets_seen": sum(1 for k in connections_seen if k[1] == "LISTEN"),
    "remote_endpoints_seen": sorted({k[3] for k in connections_seen if k[3]}),
    "after_exit": {"processes_still_alive": left, "new_state_root_entries": [n for n in state_after if n not in state_before],
                   "state_root_entries_removed_during_run": [n for n in state_before if n not in state_after]},
    "note": "Sampled every 250 ms: peaks are lower bounds. Working set/private bytes are per-process sums (shared pages counted per process).",
}
(run_dir / f"driver-{scenario}.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
alive = len(left)
print(f"DRIVER {scenario}: exit {code} in {exit_s}s; peak cef processes {peaks['processes']}; peak tree wset "
      f"{peaks['wset'] / 2**20:.0f} MiB; private {peaks['private'] / 2**20:.0f} MiB; alive after exit {alive}; "
      f"new state entries {result['after_exit']['new_state_root_entries']}; actions {[a.get('why') for a in actions]}; "
      f"listening {result['listening_sockets_seen']}; remote endpoints {result['remote_endpoints_seen']}")
