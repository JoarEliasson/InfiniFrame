"""Actual native geometry convergence, cancellation and state confirmation.

Default minimization acceptance is strict. The explicit diagnostic environment
INFINIFRAME_CEF_EXPECT_MINIMIZE=unobservable instead verifies a bounded Error106
on window managers that cannot publish minimization. That mode does not qualify
native minimization support.
"""
import os
import struct
import time

from smoke import Session, parse_state, renderer_sandbox, string, text


def reply(session, request):
    predicate = lambda frame: frame[1] == request and frame[0] in (100, 106)
    found = next((frame for frame in session.events if predicate(frame)), None)
    return found if found is not None else session.until(predicate)


def actual(session, view=1):
    return parse_state(session.command(12, view)[4])


def converged(session, expected, view=1):
    until = time.monotonic() + 2.5
    stable = None
    while time.monotonic() < until:
        bounds = actual(session, view)[:4]
        if bounds == expected:
            if stable is None: stable = time.monotonic()
            if time.monotonic() - stable >= .2: return
        else: stable = None
        time.sleep(.025)
    raise AssertionError(("bounds did not converge", expected, bounds))


def remains(session, expected, view=1):
    for _ in range(10):
        assert actual(session, view)[:4] == expected, (expected, actual(session, view))
        time.sleep(.05)


def set_bounds(session, expected, view=1):
    start = time.monotonic()
    session.command(7, view, body=struct.pack("<iiii", *expected))
    elapsed = time.monotonic() - start
    assert elapsed < 2.5, elapsed
    assert actual(session, view)[:4] == expected
    remains(session, expected, view)


def run():
    session = Session()
    try:
        generation = session.create(1)
        renderer_sandbox(session.p.pid)
        initial = (130, 100, 640, 480)
        converged(session, initial)
        remains(session, initial)
        print("PASS initial bounds converge and remain actual", flush=True)
        for expected in ((100, 100, 800, 600), (100, 100, 640, 480),
                         (160, 140, 700, 500), (120, 100, 800, 600)):
            set_bounds(session, expected)
        print("PASS repeated moves/resizes: exact actual bounds stay stable after ACK", flush=True)

        first = session.send(7, 1, body=struct.pack("<iiii", 150, 150, 750, 550))
        second = session.send(7, 1, body=struct.pack("<iiii", 170, 140, 720, 520))
        done = reply(session, second)
        assert done[0] == 100, done
        cancelled = reply(session, first)
        assert cancelled[0] == 106 and text(cancelled[4]) == "geometry operation superseded"
        remains(session, (170, 140, 720, 520))
        print("PASS superseded geometry finishes once; stale ticks cannot overwrite replacement", flush=True)

        pending = session.send(7, 1, body=struct.pack("<iiii", 180, 160, 760, 560))
        session.command(9, 1, body=struct.pack("<i", 1))
        cancelled = reply(session, pending)
        assert cancelled[0] == 106 and text(cancelled[4]) == "geometry interrupted by window state change"
        set_bounds(session, (120, 100, 800, 600))
        print("PASS state request cancels pending geometry and subsequent bounds succeed", flush=True)

        sibling = session.create(2)
        converged(session, (160, 100, 640, 480), 2)
        pending = session.send(7, 2, body=struct.pack("<iiii", 150, 120, 720, 520))
        session.command(3, 2)
        cancelled = reply(session, pending)
        assert cancelled[0] == 106 and "closure" in text(cancelled[4])
        assert session.evaluate(1, generation, "6*7") == 42
        reopened = session.create(2)
        assert reopened != sibling
        converged(session, (160, 100, 640, 480), 2)
        remains(session, (160, 100, 640, 480), 2)
        print("PASS closure cancels geometry; sibling healthy and same view ID reopens cleanly", flush=True)

        for state, flag, expected in ((2, 1, True), (3, 1, False), (5, 4, True), (6, 4, False)):
            session.command(9, 1, body=struct.pack("<i", state))
            assert bool(actual(session)[4] & flag) == expected
        unobservable = os.environ.get("INFINIFRAME_CEF_EXPECT_MINIMIZE") == "unobservable"
        result = session.command(9, 1, body=struct.pack("<i", 4), error=unobservable)
        if unobservable:
            assert "did not report minimized state" in text(result[4])
            assert not (actual(session)[4] & 2)
            print("EXPECTED_FAILURE: minimized state unobservable; bounded Error106 verified (minimization NOT QUALIFIED)", flush=True)
        else:
            assert actual(session)[4] & 2
            print("PASS actual minimized state confirmed before ACK", flush=True)
        session.command(9, 1, body=struct.pack("<i", 6))
        assert not (actual(session)[4] & (2 | 4))
        assert session.evaluate(2, reopened, "6*7") == 42
        print("PASS fullscreen/maximize/restore ACKs match actual CEF state; sibling isolation", flush=True)
        session.close()
    finally:
        session.abort()


if __name__ == "__main__":
    run()
