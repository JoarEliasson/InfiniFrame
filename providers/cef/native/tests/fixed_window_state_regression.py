"""Real-companion regression for fixed-window restore state and preserved-size moves.

Run only against a freshly built protocol-3 companion, on a desktop larger than
700x500 DIPs. Public provider bounds are outer bounds; CONTENT_BOUNDS below is a
direct-native compatibility case only. No mocked framework/native implementation.
"""
import json
import os
import struct
import time

from geometry_state_regression import actual, converged, remains, reply, set_bounds
from smoke import CONTENT_BOUNDS, FIXED_SIZE, FRAMELESS, Session, text
from window_style_regression import viewport, x11_size_hints
from windows_window_geometry import window_geometry


LIMITS = (320, 240, 700, 500)
STATE_REFUSAL = "bounds require a normal window with no pending state change"
LIMIT_REFUSAL = "bounds outside the window's size limits"


def rejected(session, view, bounds, reason):
    start = time.monotonic()
    result = session.command(7, view, body=struct.pack("<iiii", *bounds), error=True)
    assert time.monotonic() - start < 2.5
    assert text(result[4]) == reason, result
    return result[1]


def pinned(session, view, generation):
    hints = x11_size_hints("Native CEF smoke " + str(view))
    if hints is not None:
        # X11 hints are client pixels; the test fixes device scale to 1, including framed windows.
        size = viewport(session, view, generation)
        assert hints == (size, size), (view, hints, size)
    return hints is not None


def outside_limits(size):
    width, height = size
    return not (LIMITS[0] <= width <= LIMITS[2] and LIMITS[1] <= height <= LIMITS[3])


def geometry_evidence(session, view, generation):
    result = {"query_state": actual(session, view)}
    try:
        result["javascript"] = session.evaluate(view, generation, """({
          viewport: [innerWidth, innerHeight], outer: [outerWidth, outerHeight],
          screen: [screen.width, screen.height], available: [screen.availWidth, screen.availHeight],
          position: [screenX, screenY], devicePixelRatio
        })""")
    except Exception as error:
        result["javascript_error"] = repr(error)
    if os.name == "nt":
        try:
            result["native"] = window_geometry(session.p.pid, "Native CEF smoke " + str(view))
        except Exception as error:
            result["native_error"] = repr(error)
    return result


def fullscreen_matches(evidence):
    state, js = evidence["query_state"], evidence.get("javascript")
    if not js or not state[4] & 1 or list(state[2:4]) != js["viewport"] or js["viewport"] != js["screen"]:
        return False
    if os.name == "nt":
        native = evidence.get("native")
        if not native or native["outer_px"] != native["monitor_px"] or native["client_px"] != native["monitor_px"]:
            return False
    return True


def fullscreen_evidence(session, view, generation):
    # State ACK confirms the CEF flag. Allow the renderer a bounded time to finish its resize,
    # then verify display coverage independently; a fullscreen flag alone never passes this check.
    deadline = time.monotonic() + 2.5
    while True:
        evidence = geometry_evidence(session, view, generation)
        if fullscreen_matches(evidence) or time.monotonic() >= deadline:
            print("FULLSCREEN_GEOMETRY", json.dumps(evidence, sort_keys=True), flush=True)
            return evidence
        time.sleep(.025)


def restored_limits(session, view, generation, flags, normal):
    requests = [rejected(session, view, (*normal[:2], 701, normal[3]), LIMIT_REFUSAL),
                rejected(session, view, (*normal[:2], 319, normal[3]), LIMIT_REFUSAL)]
    if os.name == "nt":
        native = window_geometry(session.p.pid, "Native CEF smoke " + str(view))
        # Bypass protocol validation on this test-owned HWND to prove that the native min/max
        # callbacks resumed too. Only resize (no move/activate/Z-order); then restore via protocol.
        for requested, expected in (((native["outer_px"][2] * 4, native["outer_px"][3] * 4), LIMITS[2:]),
                                    ((1, 1), LIMITS[:2])):
            result = window_geometry(session.p.pid, "Native CEF smoke " + str(view), resize=requested)
            evidence = geometry_evidence(session, view, generation)
            evidence["native_resize_probe"] = result
            print("RESTORED_LIMITS", json.dumps(evidence, sort_keys=True), flush=True)
            assert tuple(evidence["query_state"][2:4]) == expected, evidence
            assert not evidence["query_state"][4] & 7, evidence
        set_bounds(session, normal, view)
    elif flags & FIXED_SIZE:
        pinned(session, view, generation)
    else:
        hints = x11_size_hints("Native CEF smoke " + str(view))
        if hints is not None:
            client = viewport(session, view, generation)
            frame = (0, 0) if flags & CONTENT_BOUNDS else (normal[2] - client[0], normal[3] - client[1])
            expected = tuple((size[0] - frame[0], size[1] - frame[1]) for size in (LIMITS[:2], LIMITS[2:]))
            assert hints == expected, (hints, expected)
    remains(session, normal, view)
    return requests


def fixed_window(session, view, flags, label):
    initial, normal = (100, 100, 600, 360), (120, 110, 640, 400)
    generation = session.create(view, flags=flags | FIXED_SIZE, limits=LIMITS, bounds=initial)
    assert session.evaluate(view, generation, "window.fixedWindowMarker = 42") == 42
    set_bounds(session, normal, view)  # non-resizable windows still accept programmatic resizing
    hints_checked = pinned(session, view, generation)
    requests = [rejected(session, view, (120, 110, 701, 400), LIMIT_REFUSAL)]
    remains(session, normal, view)
    pinned(session, view, generation)

    out_of_limit_moves = []
    for enter, leave, flag, state_name in ((5, 6, 4, "maximized"), (2, 3, 1, "fullscreen")):
        # Queue bounds behind the state command before reading its response. A bounds refusal must
        # not supersede the transition, even while the compositor has not published its flags yet.
        transition = session.send(9, view, body=struct.pack("<i", enter))
        during = session.send(7, view, body=struct.pack("<iiii", 130, 120, 620, 380))
        result = reply(session, during)
        assert result[0] == 106 and text(result[4]) == STATE_REFUSAL, result
        assert reply(session, transition)[0] == 100, (label, state_name, "transition superseded")
        requests.extend((during, transition))
        evidence = (fullscreen_evidence(session, view, generation) if state_name == "fullscreen"
                    else geometry_evidence(session, view, generation))
        observed = evidence["query_state"]
        assert observed[4] & flag, (label, state_name, observed)
        assert observed[2:4] != normal[2:4], (label, state_name, observed)

        # All framework move APIs send this observed size unchanged. Even outside startup limits,
        # it must reach the state policy, not be clamped or rejected as an out-of-limit resize.
        moved = (observed[0] + 10, observed[1] + 10, *observed[2:4])
        requests.append(rejected(session, view, moved, STATE_REFUSAL))
        if outside_limits(observed[2:4]):
            out_of_limit_moves.append(state_name)
        if state_name == "fullscreen":
            assert outside_limits(observed[2:4]), ("desktop must exceed size limits", label, evidence)
            assert fullscreen_matches(evidence), ("fullscreen must cover its display and viewport", label, evidence)

        # The original failure: a valid in-range resize in this state destroyed fixed_restore/pinned.
        requests.append(rejected(session, view, (130, 120, 620, 380), STATE_REFUSAL))
        # A real size change still violates the limits, including in the released state.
        invalid_width = 702 if observed[2] == 701 else 701
        requests.append(rejected(session, view,
                                 (observed[0], observed[1], invalid_width, observed[3]), LIMIT_REFUSAL))
        after = actual(session, view)
        assert after[:4] == observed[:4] and after[4] & 7 == observed[4] & 7, (observed, after)
        assert session.evaluate(view, generation, "window.fixedWindowMarker") == 42

        session.command(9, view, body=struct.pack("<i", leave))
        converged(session, normal, view)
        remains(session, normal, view)  # delayed ticks must not reapply rejected bounds
        assert not actual(session, view)[4] & 7
        pinned(session, view, generation)
        if state_name == "fullscreen":
            requests.extend(restored_limits(session, view, generation, flags | FIXED_SIZE, normal))
        print("PASS", label, state_name, "rejects bounds without changing state, restore bounds or fixed pin",
              flush=True)

    # Submitted fixed geometry must also survive replacement without retaining the cancelled pin.
    first = session.send(7, view, body=struct.pack("<iiii", 130, 120, 620, 380))
    second = session.send(7, view, body=struct.pack("<iiii", *normal))
    assert reply(session, second)[0] == 100
    cancelled = reply(session, first)
    assert cancelled[0] == 106 and text(cancelled[4]) == "geometry operation superseded", cancelled
    requests.extend((first, second))
    remains(session, normal, view)
    pinned(session, view, generation)

    # Interrupt a submitted operation, then issue an explicit retry. This tests shared failure
    # cleanup without assuming that an already submitted OS resize is atomically rolled back.
    pending = session.send(7, view, body=struct.pack("<iiii", 130, 120, 620, 380))
    session.command(9, view, body=struct.pack("<i", 1))  # Activate cancels geometry, but keeps normal state
    cancelled = reply(session, pending)
    assert cancelled[0] == 106 and text(cancelled[4]) == "geometry interrupted by window state change", cancelled
    requests.append(pending)
    set_bounds(session, normal, view)
    pinned(session, view, generation)
    if os.name != "nt":
        # Cancellation leaves a saved normal target AND the previous pin. Entering a state must
        # still release that pin; Restore must return to the saved target, not the submitted size.
        pending = session.send(7, view, body=struct.pack("<iiii", 130, 120, 620, 380))
        session.command(9, view, body=struct.pack("<i", 5))
        cancelled = reply(session, pending)
        assert cancelled[0] == 106 and text(cancelled[4]) == "geometry interrupted by window state change", cancelled
        requests.append(pending)
        assert actual(session, view)[4] & 4
        # Restore acknowledges the state before its internal geometry settles. Immediately
        # re-entering maximize can interrupt that repin; it must release any submitted hints again.
        session.command(9, view, body=struct.pack("<i", 6))
        session.command(9, view, body=struct.pack("<i", 5))
        assert actual(session, view)[4] & 4
        session.command(9, view, body=struct.pack("<i", 6))
        converged(session, normal, view)
        remains(session, normal, view)
        pinned(session, view, generation)
    for request in requests:
        responses = [event for event in session.events if event[1] == request and event[0] in (100, 106)]
        assert len(responses) == 1, (request, responses)
    assert session.evaluate(view, generation, "window.fixedWindowMarker") == 42
    print("PASS", label, "out-of-limit preserved-size moves:", out_of_limit_moves,
          "; resize limits, supersession, cancellation/retry and document state checked", flush=True)
    if not hints_checked:
        print("NOTE", label, "X11 pin hints not inspected (requires X11 and xprop); restored geometry checked",
              flush=True)
    pending = session.send(7, view, body=struct.pack("<iiii", 130, 120, 620, 380))
    session.command(3, view)
    cancelled = reply(session, pending)
    assert cancelled[0] == 106 and "closure" in text(cancelled[4]), cancelled
    reopened = session.create(view, flags=flags | FIXED_SIZE, limits=LIMITS, bounds=initial)
    assert reopened != generation
    converged(session, initial, view)
    remains(session, initial, view)
    pinned(session, view, reopened)
    session.command(3, view)


def resizable_window(session, view, flags, label):
    normal = (120, 110, 640, 400)
    generation = session.create(view, flags=flags, limits=LIMITS, bounds=normal)
    session.command(9, view, body=struct.pack("<i", 2))
    evidence = fullscreen_evidence(session, view, generation)
    observed = evidence["query_state"]
    assert outside_limits(observed[2:4]), ("desktop must exceed size limits", label, evidence)
    assert fullscreen_matches(evidence), ("fullscreen must cover its display and viewport", label, evidence)
    rejected(session, view, (observed[0] + 10, observed[1] + 10, *observed[2:4]), STATE_REFUSAL)
    session.command(9, view, body=struct.pack("<i", 3))
    converged(session, normal, view)
    assert not actual(session, view)[4] & 7
    restored_limits(session, view, generation, flags, normal)
    print("PASS", label, "constrained resizable fullscreen covers display/viewport; exit restores normal bounds and limits",
          flush=True)
    session.command(3, view)


def run():
    session = Session(scale=1.0)
    try:
        for view, flags, label in ((1, FRAMELESS, "frameless outer bounds"),
                                   (2, 0, "framed outer bounds"),
                                   (3, CONTENT_BOUNDS, "framed content bounds (direct native only)")):
            fixed_window(session, view, flags, label)
            resizable_window(session, view + 3, flags, label)
        session.close()
    finally:
        session.abort()


if __name__ == "__main__":
    run()
