"""Exercise the generated adapter and confirmed bootstrap failure lifetimes."""
import json
import struct

from smoke import ROOT, Session, string, text, window_style


def create(session, view, script, url="app://localhost/index.html"):
    return session.command(2, view, body=string("Native bootstrap regression") +
                           struct.pack("<iiii", 100, 100, 640, 480) + string(url) + string(script) +
                           window_style())


def run():
    bundle = (ROOT.parents[3] / "src/InfiniFrame.Js/wwwroot/InfiniFrame.js").read_text(encoding="utf-8")
    session = Session()
    try:
        create(session, 1, bundle)
        document = session.until(lambda frame: frame[0] == 101 and frame[2] == 1)
        generation = document[3]
        envelope = json.dumps({"version": 2, "id": "__infiniframe:javascript:eval", "command": "Post",
                               "data": {"requestId": "native-regression", "script": "({answer:42})"}})
        start = len(session.events)
        session.command(5, 1, generation, string(envelope))
        predicate = lambda frame: frame[0] == 102 and frame[2] == 1 and "__infiniframe:javascript:eval:result" in text(frame[4])
        result = next((frame for frame in session.events[start:] if predicate(frame)), None)
        if result is None: result = session.until(predicate)
        value = json.loads(text(result[4]))["data"]
        assert value["requestId"] == "native-regression" and json.loads(value["result"]) == {"answer": 42}
        session.evaluate(1, generation, "infiniframe.host.receiveCallback(()=>{throw new Error('private fixture')}); true")
        error = session.command(5, 1, generation, string("throws"), error=True)
        assert text(error[4]) == "receive callback failed"
        print("PASS actual generated adapter multiplexing, public eval envelope, throwing receiver", flush=True)
        session.close()
    finally:
        session.abort()

    session = Session()
    try:
        create(session, 1, "if (location.pathname === '/bootstrap-fail') throw new Error('private fixture');")
        initial = session.until(lambda frame: frame[0] == 101 and frame[2] == 1)[3]
        sibling = session.create(2)
        assert session.evaluate(1, initial, "6*7") == 42
        start = len(session.events)
        error = session.command(4, 1, body=string("app://localhost/bootstrap-fail"), error=True)
        assert text(error[4]) == "renderer context initialization failed"
        predicate = lambda frame: frame[0] == 105 and frame[2] == 1
        fault = next((frame for frame in session.events[start:] if predicate(frame)), None)
        if fault is None: fault = session.until(predicate)
        assert fault[3] == 0 and "private fixture" not in text(fault[4])
        assert not any(frame[0] == 101 and frame[2] == 1 for frame in session.events[start:])
        assert session.evaluate(2, sibling, "6*7") == 42
        start = len(session.events)
        recovered = session.command(4, 1, body=string("app://localhost/index.html"))
        assert recovered[4] == b"\x00" and recovered[3] != initial
        assert any(frame[0] == 101 and frame[2] == 1 and frame[3] == recovered[3] for frame in session.events[start:])
        assert session.evaluate(1, recovered[3], "6*7") == 42
        # Drain later renderer replies through multiple ordered evaluations.
        start = len(session.events)
        for _ in range(10): assert session.evaluate(1, recovered[3], "6*7") == 42
        assert not any(frame[0] == 105 and frame[2] == 1 for frame in session.events[start:])
        print("PASS conditional bootstrap failure: pending navigation fails, no Document101, sibling healthy, fresh context recovers", flush=True)
        session.close()
    finally:
        session.abort()

    session = Session()
    try:
        start = len(session.events)
        create(session, 9, "throw new Error('private initialization fixture')", "app://localhost/frame.html")
        fault = session.until(lambda frame: frame[0] == 105 and frame[2] == 9)
        assert fault[3] == 0 and "private initialization fixture" not in text(fault[4])
        assert not any(frame[0] == 101 and frame[2] == 9 for frame in session.events[start:])
        session.close()
        print("PASS initial failed bootstrap is confirmed, sanitized, and never registered", flush=True)
    finally:
        session.abort()


if __name__ == "__main__":
    run()
