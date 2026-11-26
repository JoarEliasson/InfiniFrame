"""Verify an installed Linux payload's real POSIX modes, without repairing it."""
import os
from pathlib import Path
import stat


def check():
    assert os.name == "posix", "This check requires a native POSIX filesystem"
    host = Path(os.environ["INFINIFRAME_CEF_SMOKE_HOST"]).resolve(strict=True)
    mounts = []
    for line in Path("/proc/self/mountinfo").read_text().splitlines():
        before, after = line.split(" - ", 1)
        mount = Path(before.split()[4].replace("\\040", " "))
        if host.is_relative_to(mount):
            mounts.append((len(mount.parts), mount, after.split()[0]))
    _, mount, filesystem = max(mounts)
    assert filesystem not in ("9p", "drvfs", "ntfs", "ntfs3", "fuseblk"), (mount, filesystem)
    assert stat.S_IMODE(host.stat().st_mode) == 0o755, oct(host.stat().st_mode)
    assert os.access(host, os.X_OK)
    assert stat.S_IMODE((host.parent / "icudtl.dat").stat().st_mode) == 0o644
    assert stat.S_IMODE((host.parent / "locales").stat().st_mode) == 0o755
    assert not (host.parent / "chrome-sandbox").exists()
    helper = host.parent / "InfiniFrame.Cef.Subprocess"
    adapter = host.parent / "libInfiniFrame.Cef.AsanAllocator.so"
    if helper.exists() or adapter.exists():
        assert helper.is_file() and not helper.is_symlink()
        assert adapter.is_file() and not adapter.is_symlink()
        assert stat.S_IMODE(helper.stat().st_mode) == 0o755
        assert stat.S_IMODE(adapter.stat().st_mode) == 0o644
    print("PASS POSIX install", filesystem, "host=0755 icudtl.dat=0644 locales=0755 no SUID helper", flush=True)


if __name__ == "__main__":
    check()
