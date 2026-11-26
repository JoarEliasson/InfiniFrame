"""Native geometry probes for a test-owned companion window. No work runs on import."""
import ctypes
from ctypes import wintypes


def window_geometry(pid, title, resize=None):
    """Read physical rectangles/DPI, optionally requesting an outer size through SetWindowPos.

    A resize is used only to check native constraints after fullscreen exit, independently of
    protocol SetBounds validation. Select exactly one visible window by companion PID and title.
    """
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    signatures = {
        "EnumWindows": ([enum_proc, wintypes.LPARAM], wintypes.BOOL),
        "GetWindowThreadProcessId": ([wintypes.HWND, ctypes.POINTER(wintypes.DWORD)], wintypes.DWORD),
        "IsWindowVisible": ([wintypes.HWND], wintypes.BOOL),
        "GetWindowTextW": ([wintypes.HWND, wintypes.LPWSTR, ctypes.c_int], ctypes.c_int),
        "GetWindowRect": ([wintypes.HWND, ctypes.POINTER(wintypes.RECT)], wintypes.BOOL),
        "GetClientRect": ([wintypes.HWND, ctypes.POINTER(wintypes.RECT)], wintypes.BOOL),
        "ClientToScreen": ([wintypes.HWND, ctypes.POINTER(wintypes.POINT)], wintypes.BOOL),
        "MonitorFromWindow": ([wintypes.HWND, wintypes.DWORD], wintypes.HANDLE),
        "GetMonitorInfoW": ([wintypes.HANDLE, ctypes.c_void_p], wintypes.BOOL),
        "GetDpiForWindow": ([wintypes.HWND], wintypes.UINT),
        "SetThreadDpiAwarenessContext": ([ctypes.c_void_p], ctypes.c_void_p),
        "SetWindowPos": ([wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                          ctypes.c_int, ctypes.c_int, wintypes.UINT], wintypes.BOOL),
    }
    for name, (args, result) in signatures.items():
        method = getattr(user32, name)
        method.argtypes, method.restype = args, result

    class MonitorInfo(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("monitor", wintypes.RECT),
                    ("work", wintypes.RECT), ("flags", wintypes.DWORD)]

    def checked(result):
        if not result:
            raise ctypes.WinError(ctypes.get_last_error())
        return result

    def rect(value):
        return [value.left, value.top, value.right - value.left, value.bottom - value.top]

    handles = []

    @enum_proc
    def enumerate_window(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            caption = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(hwnd, caption, len(caption))
            if caption.value == title:
                handles.append(hwnd)
        return True

    # Read physical pixels without changing the companion's DPI context; restore our thread's
    # previous context even if a probe fails. The process under test still uses Session(scale=1).
    previous = checked(user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4)))
    try:
        checked(user32.EnumWindows(enumerate_window, 0))
        if len(handles) != 1:
            raise AssertionError(("expected one test-owned window", pid, title, handles))
        hwnd = handles[0]
        if resize is not None:
            # No move, activation or Z-order change. Do not suppress WM_WINDOWPOSCHANGING:
            # that is where Windows consults the companion's native size constraints.
            checked(user32.SetWindowPos(hwnd, None, 0, 0, *resize, 0x0002 | 0x0004 | 0x0010))
        outer, client, origin = wintypes.RECT(), wintypes.RECT(), wintypes.POINT()
        checked(user32.GetWindowRect(hwnd, ctypes.byref(outer)))
        checked(user32.GetClientRect(hwnd, ctypes.byref(client)))
        checked(user32.ClientToScreen(hwnd, ctypes.byref(origin)))
        monitor = MonitorInfo()
        monitor.size = ctypes.sizeof(monitor)
        checked(user32.GetMonitorInfoW(checked(user32.MonitorFromWindow(hwnd, 2)), ctypes.byref(monitor)))
        return {"hwnd": int(hwnd), "outer_px": rect(outer),
                "client_px": [origin.x, origin.y, client.right - client.left, client.bottom - client.top],
                "monitor_px": rect(monitor.monitor), "work_area_px": rect(monitor.work),
                "dpi": checked(user32.GetDpiForWindow(hwnd)), "requested_outer_px": resize}
    finally:
        checked(user32.SetThreadDpiAwarenessContext(previous))
