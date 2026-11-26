"""Reject stale wire layouts and unsupported host options before browser initialization or any window."""
import os
import struct
import subprocess

from smoke import EXE, HOST_CWD, PROTOCOL, hello_body, string


def launch(frame):
    return subprocess.run([str(EXE)], input=struct.pack("<I", len(frame)) + frame,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          cwd=HOST_CWD, timeout=15)


def rejected(result, nonce, diagnostic):
    assert result.returncode == 76, (result.returncode, result.stderr)
    assert result.stdout == b"", "A rejected Hello must never complete"
    assert nonce not in result.stderr, "Bootstrap material must not appear in diagnostics"
    assert diagnostic.encode() in result.stderr, (diagnostic, result.stderr)


def run():
    for version in (1, PROTOCOL - 1, PROTOCOL + 1):
        nonce = os.urandom(32)
        body = nonce + string("app://localhost/") + (b"" if version < PROTOCOL else struct.pack("<Id", 0, 0.0))
        rejected(launch(struct.pack("<HHQQQ", version, 1, 1, 0, 0) + body), nonce,
                 f"protocol version mismatch: this companion implements protocol {PROTOCOL}, "
                 f"the managed provider sent protocol {version}")
        print(f"PASS protocol {version} rejected before Hello with a version diagnostic", flush=True)

    cases = (
        ("unknown host option bit", lambda nonce: hello_body(nonce, options=8), "unsupported host options"),
        ("device scale factor above 4", lambda nonce: hello_body(nonce, scale=4.5), "unsupported device scale factor"),
        ("device scale factor below 0.5", lambda nonce: hello_body(nonce, scale=0.25), "unsupported device scale factor"),
        ("NaN device scale factor", lambda nonce: hello_body(nonce, scale=float("nan")), "unsupported device scale factor"),
        ("missing host options (protocol 2 body)", lambda nonce: nonce + string("app://localhost/"),
         "invalid application origin or host options"),
    )
    for name, body, diagnostic in cases:
        nonce = os.urandom(32)
        rejected(launch(struct.pack("<HHQQQ", PROTOCOL, 1, 1, 0, 0) + body(nonce)), nonce, diagnostic)
        print(f"PASS {name} rejected before CEF initialization", flush=True)


if __name__ == "__main__":
    run()
