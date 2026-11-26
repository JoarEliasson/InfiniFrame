"""Real companion regressions for retained documents and rejected resources.

Run with the installed, freshly built host in INFINIFRAME_CEF_SMOKE_HOST.
The companion runs with its normal sandbox and no browser argument overrides.
"""
import json
import struct
import time

from smoke import Session, parse_state, renderer_sandbox, string, text, window_style


class ResourceSession(Session):
    def service(self, frame):
        if frame[0] != 103 or b"/invalid-response" not in frame[4]:
            return super().service(frame)
        self.resource_count += 1
        body = struct.pack("<i", 200) + string("text/plain") + struct.pack("<I", 2) + b"ok"
        case = self.invalid_case
        if case == "status":
            body = struct.pack("<i", 199) + body[4:]
        elif case == "mime-long":
            body = struct.pack("<i", 200) + string("x" * 257) + struct.pack("<I", 0)
        elif case == "mime-empty":
            body = struct.pack("<i", 200) + string("") + struct.pack("<I", 0)
        elif case == "mime-newline":
            body = struct.pack("<i", 200) + string("text/plain\r\nx: invalid") + struct.pack("<I", 0)
        elif case == "mime-nul":
            body = struct.pack("<i", 200) + string("text/plain\0x") + struct.pack("<I", 0)
        elif case == "truncated":
            body = body[:-1]
        elif case == "trailing":
            body += b"unexpected"
        elif case == "utf8":
            body = struct.pack("<iI", 200, 1) + b"\xff" + struct.pack("<I", 0)
        else:
            raise AssertionError(case)
        # The stale malformed reply must not consume the live request's slot.
        self.send(11, frame[2], frame[3] ^ 1, body, frame[1])
        self.send(11, frame[2], frame[3], body, frame[1])
        # Duplicate malformed completion must be harmless after release.
        self.send(11, frame[2], frame[3], body, frame[1])


def wait_url(session, generation, url):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        actual = parse_state(session.command(12, 1)[4])[-1]
        if actual == url:
            assert session.evaluate(1, generation, "location.href") == url
            return
        time.sleep(.02)
    raise AssertionError((url, actual))


def run():
    session = ResourceSession()
    try:
        generation = session.create(1)
        sibling = session.create(2)
        renderer_sandbox(session.p.pid)
        session.evaluate(1, generation, "window.retainedValue = 42")
        for fragment in ("#section", "#other", "#"):
            start = len(session.events)
            url = "app://localhost/index.html" + fragment
            print("NAVIGATE fragment target", url, "active generation", generation, flush=True)
            reply = session.command(4, 1, body=string(url))
            print("NAVIGATE reply", reply[0:4], list(reply[4]), flush=True)
            assert reply[4] == b"\x01" and reply[3] == generation
            wait_url(session, generation, url)
            assert session.evaluate(1, generation, "retainedValue") == 42
            assert not any(frame[0] in (101, 108) and frame[2] == 1 for frame in session.events[start:])
        # A renderer-originated fragment change has the same lifetime rule.
        start = len(session.events)
        session.evaluate(1, generation, "location.hash = 'script'; true")
        wait_url(session, generation, "app://localhost/index.html#script")
        assert not any(frame[0] in (101, 108) and frame[2] == 1 for frame in session.events[start:])
        # Renderer hash removal retains the context. LoadURL without a target
        # fragment instead replaces the document, as observed in real CEF.
        session.evaluate(1, generation, "location.hash = ''; true")
        actual = session.evaluate(1, generation, "location.href")
        assert actual in ("app://localhost/index.html", "app://localhost/index.html#"), actual
        wait_url(session, generation, actual)
        assert session.evaluate(1, generation, "retainedValue") == 42
        reply = session.command(4, 1, body=string("app://localhost/index.html#script"))
        assert reply[4] == b"\x01" and reply[3] == generation
        wait_url(session, generation, "app://localhost/index.html#script")
        start = len(session.events)
        reply = session.command(4, 1, body=string("app://localhost/index.html"))
        assert reply[4] == b"\x00" and reply[3] != generation
        assert any(frame[0] == 101 and frame[2] == 1 and frame[3] == reply[3] for frame in session.events[start:])
        generation = reply[3]
        wait_url(session, generation, "app://localhost/index.html")
        assert session.evaluate(1, generation, "typeof retainedValue") == "undefined"
        reply = session.command(4, 1, body=string("app://localhost/index.html#script"))
        assert reply[4] == b"\x01" and reply[3] == generation
        wait_url(session, generation, "app://localhost/index.html#script")
        # An identical fragment target also retains the document. An identical
        # fragmentless target reloads it. Verify actual state in both cases.
        reply = session.command(4, 1, body=string("app://localhost/index.html#script"))
        assert reply[4] == b"\x01" and reply[3] == generation
        assert session.evaluate(1, generation, "6*7") == 42
        start = len(session.events)
        reply = session.command(4, 1, body=string("app://localhost/index.html"))
        assert reply[4] == b"\x00"
        predicate = lambda frame: frame[0] == 101 and frame[2] == 1 and frame[3] == reply[3]
        doc = next((frame for frame in session.events[start:] if predicate(frame)), None)
        if doc is None: doc = session.until(predicate)
        assert doc[3] != generation
        old_generation, generation = generation, doc[3]
        assert text(session.command(6, 1, old_generation, string("42"), error=True)[4]) == "CEF_DOCUMENT_RETIRED"
        assert session.evaluate(1, generation, "typeof retainedValue") == "undefined"
        previous = generation
        reply = session.command(4, 1, body=string("app://localhost/index.html"))
        assert reply[4] == b"\x00" and reply[3] != previous
        generation = reply[3]
        print("PASS fragment navigation retains context; reload replaces it; actual URL query agrees", flush=True)

        cases = ("status", "mime-long", "mime-empty", "mime-newline", "mime-nul", "truncated", "trailing", "utf8")
        # More than the native 16 pending slots for each rejection class catches
        # both stalled callbacks and unreleased slots without internal hooks.
        for case in cases:
            session.invalid_case = case
            for iteration in range(20):
                token = f"invalid:{case}:{iteration}:"
                start = len(session.events)
                script = "fetch('/invalid-response?case=" + case + "&n=" + str(iteration) + "').then(async r=>infiniframe.host.postData(" + json.dumps(token) + "+JSON.stringify([r.status,await r.text()]))); 'started'"
                assert session.evaluate(1, generation, script) == "started"
                predicate = lambda frame: frame[0] == 102 and frame[2] == 1 and text(frame[4]).startswith(token)
                result = next((frame for frame in session.events[start:] if predicate(frame)), None)
                if result is None:
                    result = session.until(predicate)
                assert json.loads(text(result[4])[len(token):]) == [502, ""]
            assert session.evaluate(2, sibling, "6*7") == 42
            print("PASS rejected resource completion/release:", case, "20 requests", flush=True)
        assert session.evaluate(1, generation, "6*7") == 42
        # Hold a full navigation before document registration. A second native
        # request must fail explicitly; closure must finish the first request.
        session.hold = True
        request = session.send(4, 1, body=string("app://localhost/hold-navigation"))
        session.until(lambda frame: frame[0] == 103 and frame[2] == 1 and b"/hold-navigation" in frame[4])
        error = session.command(4, 1, body=string("app://localhost/other-navigation"), error=True)
        assert text(error[4]) == "navigation already pending"
        start = len(session.events)
        session.command(3, 1)
        error = next((frame for frame in session.events[start:] if frame[1] == request and frame[0] == 106), None)
        if error is None: error = session.until(lambda frame: frame[1] == request and frame[0] == 106)
        assert text(error[4]) == "view closed during navigation"
        assert session.evaluate(2, sibling, "6*7") == 42
        print("PASS native overlapping navigation rejected; close finishes pending navigation; sibling healthy", flush=True)
        session.close()
        print("PASS shutdown after invalid resource and fragment regressions", flush=True)
    finally:
        print("Document lifecycle events", [(frame[0], frame[2], frame[3]) for frame in session.events if frame[0] in (101, 108)], flush=True)
        session.abort()

    # A load submitted before the initial document can attach must complete
    # against its own confirmed context, not the initial view's resource load.
    session = ResourceSession()
    try:
        session.hold = True
        session.command(2, 3, body=string("Navigate before initial attachment") +
                        struct.pack("<iiii", 100, 100, 640, 480) +
                        string("app://localhost/hold-initial") + string("") + window_style())
        reply = session.command(4, 3, body=string("app://localhost/index.html"))
        assert reply[4] == b"\x00" and reply[3]
        assert any(frame[0] == 101 and frame[2] == 3 and frame[3] == reply[3] and
                   text(frame[4]) == "app://localhost/index.html" for frame in session.events)
        assert session.evaluate(3, reply[3], "location.href") == "app://localhost/index.html"
        session.close()
        print("PASS navigation before initial attachment acknowledges its own actual context", flush=True)
    finally:
        session.abort()


if __name__ == "__main__":
    run()
