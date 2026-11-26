"""Protocol 3 window style, style policy and panel rendering options on the real companion.

- Frameless fixed-size windows: outer bounds equal the content area (the document's viewport).
- Size limits: Create and SetBounds outside the limits fail explicitly; invalid limits and flags are rejected.
- Content-area bounds (framed windows): the measured client area converges to the requested bounds.
- Style policy: inline style attributes apply only with the host option; <style> elements stay blocked.
- Rendering options: a forced device scale factor of 1 and the pinch/overscroll switches reach the renderer.
"""
import os
import re
import shutil
import struct
import subprocess
import time
from pathlib import Path

from smoke import (CONTENT_BOUNDS, FIXED_SIZE, FRAMELESS, Session, parse_state, string, text, window_style)

INLINE_STYLE, NO_PINCH, NO_OVERSCROLL = 1, 2, 4


def child_command_lines(parent):
    """Command lines of the companion's Chromium subprocesses (all descendants on Linux, where renderers are
    started by the zygote; direct children on Windows)."""
    if os.name != "nt":
        lines, pending, seen = [], [parent], set()
        while pending and len(seen) < 128:
            pid = pending.pop()
            if pid in seen:
                continue
            seen.add(pid)
            try:
                for task in (Path("/proc") / str(pid) / "task").iterdir():
                    try:
                        pending.extend(int(child) for child in (task / "children").read_text().split())
                    except FileNotFoundError:
                        pass
                if pid != parent:
                    lines.append((Path("/proc") / str(pid) / "cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace"))
            except (FileNotFoundError, ProcessLookupError):
                pass
        return lines
    import ctypes
    from ctypes import wintypes
    k = ctypes.windll.kernel32
    n = ctypes.windll.ntdll

    class Entry(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("usage", wintypes.DWORD), ("pid", wintypes.DWORD),
                    ("heap", ctypes.c_size_t), ("module", wintypes.DWORD), ("threads", wintypes.DWORD),
                    ("parent", wintypes.DWORD), ("priority", wintypes.LONG), ("flags", wintypes.DWORD),
                    ("exe", wintypes.WCHAR * 260)]

    class UString(ctypes.Structure):
        _fields_ = [("length", wintypes.USHORT), ("maximum", wintypes.USHORT), ("buffer", ctypes.c_void_p)]
    k.CreateToolhelp32Snapshot.restype = ctypes.c_void_p
    k.OpenProcess.restype = ctypes.c_void_p
    k.Process32FirstW.argtypes = [ctypes.c_void_p, ctypes.POINTER(Entry)]
    k.Process32NextW.argtypes = [ctypes.c_void_p, ctypes.POINTER(Entry)]
    k.CloseHandle.argtypes = [ctypes.c_void_p]
    n.NtQueryInformationProcess.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, wintypes.ULONG,
                                            ctypes.POINTER(wintypes.ULONG)]
    snapshot = k.CreateToolhelp32Snapshot(2, 0)
    entry = Entry()
    entry.size = ctypes.sizeof(entry)
    lines = []
    try:
        valid = k.Process32FirstW(snapshot, ctypes.byref(entry))
        while valid:
            if entry.parent == parent:
                handle = k.OpenProcess(0x1000, False, entry.pid)
                try:
                    size = wintypes.ULONG()
                    n.NtQueryInformationProcess(handle, 60, None, 0, ctypes.byref(size))
                    if 0 < size.value < 65536:
                        buffer = ctypes.create_string_buffer(size.value)
                        if n.NtQueryInformationProcess(handle, 60, buffer, size, ctypes.byref(size)) == 0:
                            u = UString.from_buffer(buffer)
                            lines.append(ctypes.wstring_at(u.buffer, u.length // 2))
                finally:
                    if handle: k.CloseHandle(handle)
            valid = k.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        k.CloseHandle(snapshot)
    return lines


def x11_size_hints(title):
    """(minimum, maximum) WM_NORMAL_HINTS of the X11 window with this title, or None without xprop/X11."""
    if os.name == "nt" or not os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY") or not shutil.which("xprop"):
        return None
    clients = subprocess.run(["xprop", "-root", "_NET_CLIENT_LIST"], capture_output=True, text=True).stdout
    for window in re.findall(r"0x[0-9a-f]+", clients):
        if title not in subprocess.run(["xprop", "-id", window, "WM_NAME"], capture_output=True, text=True).stdout:
            continue
        hints = subprocess.run(["xprop", "-id", window, "WM_NORMAL_HINTS"], capture_output=True, text=True).stdout
        size = lambda kind: tuple(map(int, m.groups())) if (m := re.search(kind + r" size: (\d+) by (\d+)", hints)) else None
        return size("minimum"), size("maximum")
    return None


def actual(session, view):
    return parse_state(session.command(12, view)[4])[:4]


def viewport(session, view, generation):
    return tuple(session.evaluate(view, generation, "[window.innerWidth, window.innerHeight]"))


def styled(session, view, generation):
    """Computed widths for an attribute style set through setAttribute and a <style> element rule."""
    return session.evaluate(view, generation, """(() => {
      const a = document.createElement('div'); a.setAttribute('style', 'display:block;width:123px');
      const b = document.createElement('div'); b.className = 'probe-element';
      const s = document.createElement('style'); s.textContent = '.probe-element{display:block;width:77px}';
      document.head.appendChild(s); document.body.append(a, b);
      return [getComputedStyle(a).width, getComputedStyle(b).width];
    })()""")


def create_error(session, view, bounds, flags=0, limits=(0, 0, 0, 0)):
    reply = session.command(2, view, body=string("rejected") + struct.pack("<iiii", *bounds) +
                            string("app://localhost/index.html") + string("") + window_style(flags, limits), error=True)
    return text(reply[4])


def converge(session, view, expected, timeout=2.5):
    until = time.monotonic() + timeout
    while time.monotonic() < until:
        if actual(session, view) == expected: return
        time.sleep(.025)
    raise AssertionError(("bounds did not converge", expected, actual(session, view)))


def run():
    session = Session(options=INLINE_STYLE | NO_PINCH | NO_OVERSCROLL, scale=1.0)
    try:
        bounds = (100, 100, 800, 500)
        generation = session.create(1, flags=FRAMELESS | FIXED_SIZE, bounds=bounds)
        assert actual(session, 1) == bounds
        assert viewport(session, 1, generation) == (800, 500), viewport(session, 1, generation)
        print("PASS frameless fixed-size window: outer bounds", bounds, "equal the 800x500 viewport", flush=True)
        hints = x11_size_hints("Native CEF smoke 1")
        if hints is not None:
            assert hints == ((800, 500), (800, 500)), hints
        session.command(7, 1, body=struct.pack("<iiii", 120, 110, 640, 400))
        assert actual(session, 1) == (120, 110, 640, 400)
        assert viewport(session, 1, generation) == (640, 400)
        if hints is not None:
            hints = x11_size_hints("Native CEF smoke 1")
            assert hints == ((640, 400), (640, 400)), hints
            print("PASS X11 size hints pin the fixed size (min == max) and follow programmatic bounds", flush=True)
        print("PASS programmatic bounds still resize a fixed-size frameless window", flush=True)

        requested = (120, 110, 640, 400)
        for enter, leave, flag in ((5, 6, 4), (2, 3, 1)):
            session.command(9, 1, body=struct.pack("<i", enter))
            state = parse_state(session.command(12, 1)[4])
            assert state[4] & flag and state[2:4] != requested[2:4], ("requested state on a fixed-size window", enter, state)
            session.command(9, 1, body=struct.pack("<i", leave))
            converge(session, 1, requested, timeout=4)
            if hints is not None:
                assert x11_size_hints("Native CEF smoke 1") == ((640, 400), (640, 400))
        print("PASS maximize and full screen apply to a fixed-size window; leaving them restores and re-pins its size",
              flush=True)

        assert session.evaluate(1, generation, "window.devicePixelRatio") == 1
        widths = styled(session, 1, generation)
        assert widths[0] == "123px" and widths[1] != "77px", widths
        print("PASS inline style attributes apply with the host option; <style> elements stay blocked;",
              "devicePixelRatio 1", flush=True)

        renderers = [line for line in child_command_lines(session.p.pid) if "--type=renderer" in line]
        assert renderers, "no renderer processes"
        for line in renderers:
            assert "--force-device-scale-factor=1" in line, line
            assert "ElasticOverscroll" in line and "OverscrollHistoryNavigation" in line, line
        print("PASS renderer command lines carry the forced scale factor and disabled overscroll features", flush=True)
        if not all("--disable-pinch" in line for line in renderers):
            print("NOTE --disable-pinch is applied to the browser process (renderers do not carry it);",
                  "pinch behaviour itself needs touch hardware to verify", flush=True)

        limits = (400, 300, 1000, 800)
        assert create_error(session, 2, (100, 100, 300, 300), limits=limits) == "window size outside its size limits"
        assert create_error(session, 2, (100, 100, 640, 480), limits=(500, 0, 400, 0)) == "invalid window size limits"
        assert create_error(session, 2, (100, 100, 640, 480), flags=8) == "unsupported window flags"
        session.create(2, limits=limits, bounds=(140, 100, 640, 480))
        reply = session.command(7, 2, body=struct.pack("<iiii", 140, 100, 300, 200), error=True)
        assert text(reply[4]) == "bounds outside the window's size limits", text(reply[4])
        session.command(7, 2, body=struct.pack("<iiii", 140, 100, 900, 700))
        print("PASS size limits: invalid limits, flags and out-of-range Create/SetBounds rejected explicitly", flush=True)
        session.command(3, 2)

        third = session.create(3, flags=CONTENT_BOUNDS, bounds=(160, 120, 640, 480))
        converge(session, 3, (160, 120, 640, 480))
        assert viewport(session, 3, third) == (640, 480), viewport(session, 3, third)
        print("PASS content-area bounds: a framed window's client area converges to 640x480", flush=True)
        session.close()
    finally:
        session.abort()

    session = Session()
    try:
        generation = session.create(1)
        widths = styled(session, 1, generation)
        assert widths[0] != "123px" and widths[1] != "77px", widths
        print("PASS default strict policy: inline style attributes and <style> elements stay blocked", flush=True)
        renderers = [line for line in child_command_lines(session.p.pid) if "--type=renderer" in line]
        assert renderers and not any("--force-device-scale-factor" in line for line in renderers)
        session.close()
    finally:
        session.abort()


if __name__ == "__main__":
    run()
