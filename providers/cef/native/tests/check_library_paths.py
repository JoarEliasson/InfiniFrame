"""Read ELF dynamic metadata and reject working-directory library lookup."""
import os
from pathlib import Path
import struct


def check():
    host = Path(os.environ["INFINIFRAME_CEF_SMOKE_HOST"])
    data = host.read_bytes()
    assert data[:6] == b"\x7fELF\x02\x01", "Expected 64-bit little-endian ELF"
    offset, = struct.unpack_from("<Q", data, 32)
    size, count = struct.unpack_from("<HH", data, 54)
    segments = [struct.unpack_from("<IIQQQQQQ", data, offset + index * size) for index in range(count)]
    dynamic = next(segment for segment in segments if segment[0] == 2)
    entries = []
    for position in range(dynamic[2], dynamic[2] + dynamic[5], 16):
        tag, value = struct.unpack_from("<qQ", data, position)
        if tag == 0: break
        entries.append((tag, value))
    table = next(value for tag, value in entries if tag == 5)
    load = next(segment for segment in segments if segment[0] == 1 and
                segment[3] <= table < segment[3] + segment[5])
    table = load[2] + table - load[3]
    paths = []
    for tag, value in entries:
        if tag in (15, 29):
            start = table + value
            paths.extend(data[start:data.index(0, start)].decode("ascii").split(":"))
    assert paths == ["$ORIGIN"], ("Unexpected library lookup paths", paths)
    print("PASS ELF library lookup is $ORIGIN only; no working-directory search", flush=True)


if __name__ == "__main__":
    check()
