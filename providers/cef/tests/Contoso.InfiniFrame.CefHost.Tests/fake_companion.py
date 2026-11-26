"""Scripted fake of the CEF companion (protocol 3) for Contoso.InfiniFrame.CefHost.Tests.

Standard library only. The tests launch it as

    python -I fake_companion.py --mode <mode> --log <path> [options]

It speaks the companion's stdin/stdout framing well enough for the provider's reliability tests:

* hello: echoes the 32-byte nonce, then RID, CEF version and the host option flags (as InfiniFrame.Cef.Host does);
* create/close/navigate/send/set-bounds/set-title/set-state/set-icon/query/shutdown are answered;
* events: document attached (101), message (102), resource request (103), closed (104), view failed (105),
  resource cancelled (107) and document retired (108).

Every frame it receives is logged as one JSON line ("type": "command"), every frame it writes as "sent", and its
own decisions as "action"/"lifecycle", so the tests can assert on exactly what the provider sent.

Step-by-step control: a set-title whose title starts with "fake:" is a directive (the provider treats titles as
opaque text). The fake performs the directive's emissions *before* it replies to that set-title, so when
SetTitleAsync returns, the provider's read loop has already read them (frames are read in order):

    fake:attach <generation> [url]                emit document-attached for this view
    fake:message <generation> <text...>           emit a message from that document generation
    fake:retire <generation>                      emit document-retired
    fake:resource <id> <method> <url> [gen]       emit a resource request with request id <id>
    fake:cancel-resource <id>                     emit resource-cancelled (107) for an earlier request
    fake:view-failed <reason...>                  emit view-failed
    fake:closed                                   the view closes natively (104)
    fake:stderr <text...>                         write one diagnostic line to stderr
    fake:stop-reading                             reply, then never read stdin again (a stuck companion)

Modes (lifecycle presets; options below refine them):

    normal              answer everything; exit 0 on stdin EOF
    slow-create         delay each create reply by --create-delay-ms (default 2000)
    stderr-flood        after hello, write about --flood-bytes of stderr (long lines, secrets, a final marker)
    corrupt-size-small  after hello, write a frame whose size is below the 28-byte header
    corrupt-size-large  after hello, write a frame whose size exceeds 8 MiB
    corrupt-kind        after hello, write a well-formed frame of unknown kind 999
    corrupt-version     after hello, write a frame of protocol version 4 (one above the implemented version)
    crash-after-hello   after hello, print a diagnostic to stderr and exit with --exit-code (default 3)
    tree-ignore-eof     after hello, start a sleeping grandchild (PID to --pid-file); ignore stdin EOF
    tree-orphan-on-eof  after hello, start a sleeping grandchild; on EOF exit 0 and leave it running
    tree-normal         after hello, start a grandchild that ends with the fake; on EOF end it, then exit 0
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
import subprocess
import sys
import threading
import time
import traceback

PROTOCOL_VERSION = 3
HEADER = struct.Struct('<HHQQQ')  # version, kind, request, view, document generation: 28 bytes
MAX_FRAME = 8 * 1024 * 1024
ORIGIN = 'app://localhost/'
CEF_VERSION = '156.0.8+g426920c+chromium-156.0.8078.48'
CREATE_NO_WINDOW = 0x08000000
AUTO_GENERATION_BASE = 0x5EED000000000000
NAVIGATION_GENERATION_BASE = 0xA11CE00000000000

KIND_NAMES = {
    1: 'hello', 2: 'create', 3: 'close', 4: 'navigate', 5: 'send', 7: 'set-bounds', 8: 'set-title',
    9: 'set-state', 10: 'shutdown', 11: 'resource-response', 12: 'query', 13: 'set-icon',
    100: 'ok', 101: 'document-attached', 102: 'message', 103: 'resource-request', 104: 'closed',
    105: 'view-failed', 106: 'error', 107: 'resource-cancelled', 108: 'document-retired',
}
KIND_IDS = {name: kind for kind, name in KIND_NAMES.items()}


class ProtocolError(Exception):
    pass


class Frame:
    __slots__ = ('kind', 'request', 'view', 'generation', 'payload')

    def __init__(self, kind, request, view, generation, payload):
        self.kind, self.request, self.view, self.generation, self.payload = kind, request, view, generation, payload


class Payload:
    def __init__(self, data: bytes):
        self.data = data
        self.offset = 0

    def take(self, count: int) -> bytes:
        if count < 0 or self.offset + count > len(self.data):
            raise ProtocolError('payload truncated')
        chunk = self.data[self.offset:self.offset + count]
        self.offset += count
        return chunk

    def i32(self) -> int:
        return struct.unpack('<i', self.take(4))[0]

    def blob(self) -> bytes:
        return self.take(self.i32())

    def string(self) -> str:
        return self.blob().decode('utf-8')


def encode(kind: int, request: int = 0, view: int = 0, generation: int = 0, payload: bytes = b'') -> bytes:
    body = HEADER.pack(PROTOCOL_VERSION, kind, request, view, generation) + payload
    return struct.pack('<I', len(body)) + body


def string(value: str) -> bytes:
    data = value.encode('utf-8')
    return struct.pack('<i', len(data)) + data


def describe(frame: Frame) -> dict:
    """Decodes a received command's payload into log fields (never more than a short prefix of bodies)."""
    payload = Payload(frame.payload)
    try:
        if frame.kind == 1:
            nonce = payload.take(32)
            origin = payload.string()
            options = struct.unpack('<I', payload.take(4))[0]
            scale = struct.unpack('<d', payload.take(8))[0]
            return {'nonce_bytes': len(nonce), 'origin': origin, 'host_options': options, 'device_scale_factor': scale}
        if frame.kind == 2:
            return {'title': payload.string(), 'x': payload.i32(), 'y': payload.i32(), 'width': payload.i32(),
                    'height': payload.i32(), 'url': payload.string(), 'bridge_bytes': len(payload.blob()),
                    'window_flags': struct.unpack('<I', payload.take(4))[0], 'min_width': payload.i32(),
                    'min_height': payload.i32(), 'max_width': payload.i32(), 'max_height': payload.i32()}
        if frame.kind == 4:
            return {'url': payload.string()}
        if frame.kind == 5:
            text = payload.string()
            return {'message_chars': len(text), 'message_prefix': text[:120]}
        if frame.kind == 7:
            return {'x': payload.i32(), 'y': payload.i32(), 'width': payload.i32(), 'height': payload.i32()}
        if frame.kind == 8:
            return {'title': payload.string()}
        if frame.kind == 9:
            return {'state': payload.i32()}
        if frame.kind == 11:
            status = payload.i32()
            mime = payload.string()
            body = payload.blob()
            return {'status': status, 'mime': mime, 'body_bytes': len(body),
                    'body_sha256': hashlib.sha256(body).hexdigest(), 'body_prefix': body[:64].decode('latin-1')}
        if frame.kind == 13:
            return {'png_bytes': len(payload.blob())}
    except (ProtocolError, UnicodeDecodeError, struct.error) as error:
        return {'decode_error': str(error)}
    return {}


class Fake:
    def __init__(self, options):
        self.options = options
        self.started = time.monotonic()
        self.write_lock = threading.Lock()
        self.log_lock = threading.Lock()
        self.state_lock = threading.RLock()
        self.sequence = 0
        self.log_file = open(options.log, 'a', encoding='utf-8') if options.log else None
        self.views = {}
        self.resources = {}
        self.held = []
        self.shutting_down = False
        self.reading = True
        self.navigations = 0
        self.grandchild = None

    # -- logging and transport -------------------------------------------------------------------------------------
    def log(self, record_type: str, **fields) -> None:
        with self.log_lock:
            self.sequence += 1
            entry = {'seq': self.sequence, 't_ms': round((time.monotonic() - self.started) * 1000, 1),
                     'type': record_type}
            entry.update(fields)
            if self.log_file is not None:
                self.log_file.write(json.dumps(entry, sort_keys=True) + '\n')
                self.log_file.flush()

    def write_raw(self, data: bytes) -> None:
        with self.write_lock:
            view = memoryview(data)
            while view:
                written = os.write(1, view)
                view = view[written:]

    def emit(self, kind: int, request: int = 0, view: int = 0, generation: int = 0, payload: bytes = b'') -> None:
        self.write_raw(encode(kind, request, view, generation, payload))
        self.log('sent', kind=KIND_NAMES.get(kind, str(kind)), kind_id=kind, request=request, view=view,
                 generation=generation, generation_hex=hex(generation), payload_bytes=len(payload))

    def reply_ok(self, frame: Frame, payload: bytes = b'', generation=None) -> None:
        self.emit(100, frame.request, frame.view, frame.generation if generation is None else generation, payload)

    def reply_error(self, frame: Frame, text: str) -> None:
        self.emit(106, frame.request, frame.view, frame.generation, string(text))

    def read_exact(self, count: int):
        data = bytearray()
        while len(data) < count:
            chunk = os.read(0, count - len(data))
            if not chunk:
                return bytes(data)
            data += chunk
        return bytes(data)

    def read_frame(self):
        prefix = self.read_exact(4)
        if not prefix:
            return None
        if len(prefix) < 4:
            raise ProtocolError('EOF inside a frame prefix')
        size = struct.unpack('<I', prefix)[0]
        if size < HEADER.size or size > MAX_FRAME:
            raise ProtocolError(f'invalid frame size {size}')
        body = self.read_exact(size)
        if len(body) < size:
            raise ProtocolError('EOF inside a frame body')
        version, kind, request, view, generation = HEADER.unpack_from(body)
        if version != PROTOCOL_VERSION:
            raise ProtocolError(f'protocol version {version}')
        return Frame(kind, request, view, generation, body[HEADER.size:])

    # -- lifecycle -------------------------------------------------------------------------------------------------
    def run(self) -> None:
        self.log('lifecycle', event='started', mode=self.options.mode, pid=os.getpid(), argv=sys.argv[1:])
        hello = self.read_frame()
        if hello is None or hello.kind != 1:
            self.log('lifecycle', event='hello-failed')
            self.exit(76, 'hello failed')
        self.log_command(hello)
        payload = Payload(hello.payload)
        nonce = payload.take(32)
        origin = payload.string()
        host_options = payload.take(4)
        payload.take(8)  # device scale factor
        if origin != ORIGIN or payload.offset != len(payload.data):
            self.exit(76, 'invalid application origin or host options')
        if self.options.echo_host_options is not None:
            host_options = struct.pack('<I', self.options.echo_host_options)
        self.emit(100, hello.request, 0, 0, nonce + string(self.options.rid) + string(CEF_VERSION) + host_options)
        self.after_hello()
        while True:
            if not self.reading:
                self.sleep_forever('stopped reading stdin')
            frame = self.read_frame()
            if frame is None:
                self.on_eof()
                return
            self.log_command(frame)
            self.dispatch(frame)

    def after_hello(self) -> None:
        mode = self.options.mode
        if mode == 'crash-after-hello':
            os.write(2, b'InfiniFrame.Cef: fake companion fatal error (simulated crash after hello)\n')
            self.exit(self.options.exit_code, 'simulated crash')
        corrupt = {
            'corrupt-size-small': struct.pack('<I', 5) + b'\x02\x00\x65\x00\x00',
            'corrupt-size-large': struct.pack('<I', MAX_FRAME + 1),
            'corrupt-kind': encode(999, 0, 0, 0),
            'corrupt-version': struct.pack('<I', HEADER.size) + HEADER.pack(PROTOCOL_VERSION + 1, 101, 0, 1, 1),
        }.get(mode)
        if corrupt is not None:
            self.write_raw(corrupt)
            self.log('action', action='wrote-corrupt-frame', variant=mode, bytes=len(corrupt))
        if mode == 'stderr-flood':
            threading.Thread(target=self.stderr_flood, name='stderr-flood', daemon=True).start()
        if mode in ('tree-ignore-eof', 'tree-orphan-on-eof', 'tree-normal'):
            self.spawn_grandchild(ends_with_fake=mode == 'tree-normal')

    def on_eof(self) -> None:
        mode = self.options.mode
        self.log('lifecycle', event='stdin-eof')
        if mode == 'tree-ignore-eof':
            self.sleep_forever('ignoring stdin EOF')
        if mode == 'tree-normal' and self.grandchild is not None:
            self.grandchild.stdin.close()
            self.grandchild.wait(timeout=10)
            self.log('lifecycle', event='grandchild-ended', code=self.grandchild.returncode)
        self.exit(0, 'stdin EOF')

    def sleep_forever(self, why: str) -> None:
        self.log('lifecycle', event='sleeping-forever', reason=why)
        while True:
            time.sleep(3600)

    def exit(self, code: int, why: str) -> None:
        self.log('lifecycle', event='exit', code=code, reason=why)
        if self.log_file is not None:
            self.log_file.flush()
        os._exit(code)

    # -- commands --------------------------------------------------------------------------------------------------
    def log_command(self, frame: Frame) -> None:
        fields = describe(frame)
        self.log('command', kind=KIND_NAMES.get(frame.kind, str(frame.kind)), kind_id=frame.kind,
                 request=frame.request, view=frame.view, generation=frame.generation,
                 generation_hex=hex(frame.generation), payload_bytes=len(frame.payload), **fields)

    def dispatch(self, frame: Frame) -> None:
        if frame.kind == 11:
            with self.state_lock:
                self.resources.pop(frame.request, None)
            return
        if frame.request == 0:
            self.log('action', action='ignored-command-without-request', kind_id=frame.kind)
            return
        handler = {2: self.on_create, 3: self.on_close, 4: self.on_navigate, 5: self.on_send, 7: self.on_bounds,
                   8: self.on_title, 9: self.on_state, 10: self.on_shutdown, 12: self.on_query,
                   13: self.on_icon}.get(frame.kind)
        if handler is None:
            self.reply_error(frame, 'unsupported command')
            return
        if self.shutting_down and frame.kind != 10:
            self.reply_error(frame, 'host shutting down')
            return
        if self.should_hold(frame):
            return
        handler(frame)

    def should_hold(self, frame: Frame) -> bool:
        if not self.options.hold or KIND_NAMES.get(frame.kind) != self.options.hold:
            return False
        if frame.kind == 8 and Payload(frame.payload).string().startswith('fake:'):
            return False
        with self.state_lock:
            self.held.append(frame)
        self.log('action', action='held', request=frame.request, kind=KIND_NAMES[frame.kind], view=frame.view,
                 release_on=self.options.release_on)
        return True

    def release_held(self) -> None:
        with self.state_lock:
            held, self.held = self.held, []
        for frame in held:
            self.log('action', action='releasing', request=frame.request, kind=KIND_NAMES[frame.kind],
                     reply=self.options.late_reply)
            if self.options.late_reply == 'error':
                self.reply_error(frame, self.options.late_error)
            else:
                self.reply_ok(frame)

    def view(self, view_id: int):
        with self.state_lock:
            view = self.views.get(view_id)
            return view if view is not None and view['open'] else None

    def attach(self, view_id: int, generation: int, url: str) -> None:
        with self.state_lock:
            view = self.views.get(view_id)
            if view is not None:
                view['generation'] = generation
                view['url'] = url
        self.emit(101, 0, view_id, generation, string(url))

    def on_create(self, frame: Frame) -> None:
        payload = Payload(frame.payload)
        title = payload.string()
        x, y, width, height = payload.i32(), payload.i32(), payload.i32(), payload.i32()
        url = payload.string()
        payload.string()  # bridge script
        with self.state_lock:
            if frame.view == 0 or frame.view in self.views:
                self.reply_error(frame, 'invalid create request')
                return
            self.views[frame.view] = {'open': True, 'generation': 0, 'url': url, 'title': title,
                                      'bounds': [x, y, width, height], 'flags': 0}

        def finish() -> None:
            self.log('action', action='create-reply', request=frame.request, view=frame.view)
            self.reply_ok(frame)
            if self.options.auto_attach:
                self.attach(frame.view, AUTO_GENERATION_BASE + frame.view, url)

        if self.options.create_delay_ms > 0:
            self.log('action', action='delaying-create-reply', request=frame.request, view=frame.view,
                     delay_ms=self.options.create_delay_ms)
            timer = threading.Timer(self.options.create_delay_ms / 1000.0, finish)
            timer.daemon = True
            timer.start()
        else:
            finish()

    def on_close(self, frame: Frame) -> None:
        with self.state_lock:
            view = self.views.get(frame.view)
            was_open = view is not None and view['open']
            if view is not None:
                view['open'] = False
                view['generation'] = 0
        # As the companion does: the close request is answered, then the closed event follows.
        self.reply_ok(frame)
        if was_open:
            self.emit(104, 0, frame.view, 0)

    def on_navigate(self, frame: Frame) -> None:
        url = Payload(frame.payload).string()
        view = self.view(frame.view)
        if view is None:
            self.reply_error(frame, 'unknown or closing view')
            return
        if not url.startswith(ORIGIN):
            self.reply_error(frame, 'navigation denied')
            return
        if view['generation'] and '#' in url and url.split('#')[0] == view['url'].split('#')[0]:
            view['url'] = url
            self.reply_ok(frame, b'\x01', generation=view['generation'])
            return
        with self.state_lock:
            self.navigations += 1
            generation = NAVIGATION_GENERATION_BASE + self.navigations
        # A document navigation succeeds only after its attach frame (the provider reads them in this order).
        self.attach(frame.view, generation, url)
        self.reply_ok(frame, b'\x00', generation=generation)

    def on_send(self, frame: Frame) -> None:
        view = self.view(frame.view)
        if view is None or frame.generation == 0 or frame.generation != view['generation']:
            self.reply_error(frame, 'CEF_DOCUMENT_RETIRED')
            return
        self.reply_ok(frame)

    def on_bounds(self, frame: Frame) -> None:
        payload = Payload(frame.payload)
        bounds = [payload.i32(), payload.i32(), payload.i32(), payload.i32()]
        view = self.view(frame.view)
        if view is None:
            self.reply_error(frame, 'unknown or closing view')
            return
        view['bounds'] = bounds
        self.reply_ok(frame)

    def on_state(self, frame: Frame) -> None:
        state = Payload(frame.payload).i32()
        view = self.view(frame.view)
        if view is None or state < 1 or state > 6:
            self.reply_error(frame, 'invalid state')
            return
        flags = view['flags']
        flags = {1: flags | 8, 2: flags | 1, 3: flags & ~1, 4: flags | 2, 5: flags | 4, 6: flags & ~6}[state]
        view['flags'] = flags
        self.reply_ok(frame)

    def on_icon(self, frame: Frame) -> None:
        Payload(frame.payload).blob()
        if self.view(frame.view) is None:
            self.reply_error(frame, 'unknown or closing view')
            return
        self.reply_ok(frame)

    def on_query(self, frame: Frame) -> None:
        view = self.view(frame.view)
        if view is None:
            self.reply_error(frame, 'unknown or closing view')
            return
        x, y, width, height = view['bounds']
        self.reply_ok(frame, struct.pack('<iiiii', x, y, width, height, view['flags'])
                      + string(view['title']) + string(view['url']))

    def on_shutdown(self, frame: Frame) -> None:
        self.shutting_down = True
        if self.options.release_on == 'shutdown':
            self.release_held()
        with self.state_lock:
            open_views = [view_id for view_id, view in self.views.items() if view['open']]
            for view_id in open_views:
                self.views[view_id]['open'] = False
                self.views[view_id]['generation'] = 0
        for view_id in open_views:
            self.emit(104, 0, view_id, 0)
        self.reply_ok(frame)

    def on_title(self, frame: Frame) -> None:
        title = Payload(frame.payload).string()
        if title.startswith('fake:'):
            self.directive(frame, title[len('fake:'):])
            return
        view = self.view(frame.view)
        if view is None:
            self.reply_error(frame, 'unknown or closing view')
            return
        view['title'] = title
        self.reply_ok(frame)

    def directive(self, frame: Frame, text: str) -> None:
        name, _, rest = text.partition(' ')
        arguments = rest.split(' ') if rest else []
        self.log('action', action='directive', directive=name, arguments=rest, view=frame.view)
        try:
            if name == 'attach':
                url = arguments[1] if len(arguments) > 1 else ORIGIN + 'index.html'
                self.attach(frame.view, int(arguments[0], 0), url)
            elif name == 'message':
                self.emit(102, 0, frame.view, int(arguments[0], 0), string(rest.partition(' ')[2]))
            elif name == 'retire':
                self.emit(108, 0, frame.view, int(arguments[0], 0))
            elif name == 'resource':
                request = int(arguments[0], 0)
                with self.state_lock:
                    current = self.views.get(frame.view, {}).get('generation', 0)
                generation = int(arguments[3], 0) if len(arguments) > 3 else current
                with self.state_lock:
                    self.resources[request] = (frame.view, generation)
                self.emit(103, request, frame.view, generation, string(arguments[1]) + string(arguments[2]))
            elif name == 'cancel-resource':
                request = int(arguments[0], 0)
                with self.state_lock:
                    view_id, generation = self.resources.get(request, (frame.view, 0))
                self.emit(107, request, view_id, generation)
            elif name == 'view-failed':
                self.emit(105, 0, frame.view, 0, string(rest))
            elif name == 'closed':
                with self.state_lock:
                    view = self.views.get(frame.view)
                    if view is not None:
                        view['open'] = False
                        view['generation'] = 0
                self.emit(104, 0, frame.view, 0)
            elif name == 'stderr':
                os.write(2, (rest + '\n').encode('utf-8'))
            elif name == 'stop-reading':
                self.reply_ok(frame)
                self.reading = False
                self.log('lifecycle', event='stop-reading')
                return
            else:
                self.reply_error(frame, f'unknown directive {name}')
                return
        except (IndexError, ValueError) as error:
            self.reply_error(frame, f'bad directive: {error}')
            return
        self.reply_ok(frame)

    # -- mode behaviours -------------------------------------------------------------------------------------------
    def spawn_grandchild(self, ends_with_fake: bool) -> None:
        # The grandchild never inherits the transport pipes (as CEF's children do not): its standard handles are
        # either DEVNULL or a private pipe from this fake.
        code = 'import sys; sys.stdin.buffer.read()' if ends_with_fake else 'import time; time.sleep(120)'
        child = subprocess.Popen([sys.executable, '-I', '-c', code],
                                 stdin=subprocess.PIPE if ends_with_fake else subprocess.DEVNULL,
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                 creationflags=CREATE_NO_WINDOW)
        self.grandchild = child
        if self.options.pid_file:
            temporary = self.options.pid_file + '.tmp'
            with open(temporary, 'w', encoding='utf-8') as handle:
                json.dump({'pid': child.pid, 'companion_pid': os.getpid()}, handle)
            os.replace(temporary, self.options.pid_file)
        self.log('lifecycle', event='grandchild-started', pid=child.pid, ends_with_fake=ends_with_fake)

    def stderr_flood(self) -> None:
        words = 'lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt '
        long_body = (words * (12000 // len(words) + 2))[:12000]
        lines = []
        total = 0
        index = 0
        while total < self.options.flood_bytes:
            if index % 3 == 0:
                line = f'InfiniFrame.Cef: flood {index:06d} short diagnostic line'
            else:
                line = f'InfiniFrame.Cef: flood {index:06d} long {long_body}'
            lines.append(line)
            total += len(line) + 1
            index += 1
        lines.append('InfiniFrame.Cef: control characters \x07 \x1b[31m end')
        lines.append('InfiniFrame.Cef: request failed token=abc123 retrying')
        lines.append('InfiniFrame.Cef: upstream header Authorization: Bearer xyz')
        lines.append('InfiniFrame.Cef: login password=hunter2 rejected')
        if self.options.secret_path:
            lines.append(f'InfiniFrame.Cef: cannot open {self.options.secret_path}\\AppData\\Local\\Contoso\\cache.bin')
        lines.extend(f'InfiniFrame.Cef: trailing long line {n} {long_body}' for n in range(3))
        lines.append('InfiniFrame.Cef: fake stderr flood complete')
        data = ('\r\n'.join(lines) + '\r\n').encode('utf-8')
        view = memoryview(data)
        while view:
            written = os.write(2, view)
            view = view[written:]
        self.log('action', action='stderr-flood-written', bytes=len(data), lines=len(lines))


def parse_arguments(argv):
    parser = argparse.ArgumentParser(description='Scripted CEF companion fake (protocol 3).')
    parser.add_argument('--mode', default='normal')
    parser.add_argument('--log', default=os.environ.get('FAKE_COMPANION_LOG'))
    parser.add_argument('--rid', default='win-x64')
    parser.add_argument('--auto-attach', dest='auto_attach', action='store_true', default=True)
    parser.add_argument('--no-auto-attach', dest='auto_attach', action='store_false')
    parser.add_argument('--create-delay-ms', type=int, default=None)
    parser.add_argument('--hold', choices=['set-title', 'send'], default=None)
    parser.add_argument('--release-on', choices=['shutdown', 'never'], default='shutdown')
    parser.add_argument('--late-reply', choices=['ok', 'error'], default='ok')
    parser.add_argument('--late-error', default='fake: rejected after the caller stopped waiting')
    parser.add_argument('--pid-file', default=None)
    parser.add_argument('--exit-code', type=int, default=3)
    parser.add_argument('--secret-path', default=None)
    parser.add_argument('--flood-bytes', type=int, default=8_000_000)
    parser.add_argument('--echo-host-options', type=int, default=None,
                        help='confirm these host option flags in the hello reply instead of the requested ones')
    options = parser.parse_args(argv)
    if options.create_delay_ms is None:
        options.create_delay_ms = 2000 if options.mode == 'slow-create' else 0
    return options


def main() -> None:
    if sys.platform == 'win32':
        import msvcrt
        for descriptor in (0, 1, 2):
            msvcrt.setmode(descriptor, os.O_BINARY)
    fake = Fake(parse_arguments(sys.argv[1:]))
    try:
        fake.run()
    except ProtocolError as error:
        fake.log('lifecycle', event='protocol-error', error=str(error))
        os.write(2, f'InfiniFrame.Cef: fake protocol error: {error}\n'.encode('utf-8'))
        fake.exit(65, 'protocol error')
    except OSError as error:
        fake.log('lifecycle', event='transport-error', error=str(error))
        fake.exit(74, 'transport error')
    except Exception:  # noqa: BLE001 - report anything unexpected, then fail visibly
        fake.log('lifecycle', event='crash', error=traceback.format_exc())
        os.write(2, b'InfiniFrame.Cef: fake companion failed unexpectedly\n')
        fake.exit(70, 'unexpected failure')


if __name__ == '__main__':
    main()
