import ctypes
import json
import os
import queue
import re
import stat
import struct
import subprocess
import threading
import time
import tempfile
import zlib
from pathlib import Path
from ctypes import wintypes

ROOT = Path(__file__).resolve().parent
EXE = Path(os.environ["INFINIFRAME_CEF_SMOKE_HOST"]).resolve()
HOST_CWD = Path(os.environ.get("INFINIFRAME_CEF_SMOKE_CWD", str(EXE.parent)))
LOGS = Path(os.environ.get("INFINIFRAME_CEF_SMOKE_LOGS", tempfile.gettempdir()))
LOGS.mkdir(parents=True, exist_ok=True)
PROTOCOL = 3
# Create (protocol 3) window style: flags, then minimum and maximum width/height (0: unconstrained).
FRAMELESS, FIXED_SIZE, CONTENT_BOUNDS = 1, 2, 4
def window_style(flags=0, limits=(0, 0, 0, 0)):
    return struct.pack("<Iiiii", flags, *limits)
def hello_body(nonce, options=0, scale=0.0):
    return nonce + string("app://localhost/") + struct.pack("<Id", options, scale)
def string(s):
    b = s.encode("utf-8")
    return struct.pack("<I", len(b)) + b
def text(b):
    n, = struct.unpack_from("<I", b)
    assert n == len(b) - 4
    return b[4:].decode("utf-8")

def parse_state(b):
    values = struct.unpack_from("<iiiii", b)
    n, = struct.unpack_from("<I", b, 20)
    return (*values, b[24:24+n].decode("utf-8"), text(b[24+n:]))

def renderer_integrities(parent):
    k=ctypes.windll.kernel32
    a=ctypes.windll.advapi32
    n=ctypes.windll.ntdll
    class Entry(ctypes.Structure):
        _fields_=[("size",wintypes.DWORD),("usage",wintypes.DWORD),("pid",wintypes.DWORD),
                  ("heap",ctypes.c_size_t),("module",wintypes.DWORD),("threads",wintypes.DWORD),
                  ("parent",wintypes.DWORD),("priority",wintypes.LONG),("flags",wintypes.DWORD),
                  ("exe",wintypes.WCHAR*260)]
    class UString(ctypes.Structure):
        _fields_=[("length",wintypes.USHORT),("maximum",wintypes.USHORT),("buffer",ctypes.c_void_p)]
    k.CreateToolhelp32Snapshot.restype=ctypes.c_void_p
    k.OpenProcess.restype=ctypes.c_void_p
    k.Process32FirstW.argtypes=[ctypes.c_void_p,ctypes.POINTER(Entry)]
    k.Process32NextW.argtypes=[ctypes.c_void_p,ctypes.POINTER(Entry)]
    k.CloseHandle.argtypes=[ctypes.c_void_p]
    a.OpenProcessToken.argtypes=[ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(ctypes.c_void_p)]
    a.GetTokenInformation.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD)]
    a.GetSidSubAuthorityCount.argtypes=[ctypes.c_void_p]
    a.GetSidSubAuthorityCount.restype=ctypes.POINTER(ctypes.c_ubyte)
    a.GetSidSubAuthority.argtypes=[ctypes.c_void_p,wintypes.DWORD]
    a.GetSidSubAuthority.restype=ctypes.POINTER(wintypes.DWORD)
    n.NtQueryInformationProcess.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_void_p,wintypes.ULONG,ctypes.POINTER(wintypes.ULONG)]
    snapshot=k.CreateToolhelp32Snapshot(2,0)
    entry=Entry();entry.size=ctypes.sizeof(entry)
    found=[]
    try:
        valid=k.Process32FirstW(snapshot,ctypes.byref(entry))
        while valid:
            if entry.parent==parent and entry.exe=="InfiniFrame.Cef.Host.exe":
                handle=k.OpenProcess(0x1000,False,entry.pid)
                try:
                    size=wintypes.ULONG()
                    n.NtQueryInformationProcess(handle,60,None,0,ctypes.byref(size))
                    if 0<size.value<65536:
                        buffer=ctypes.create_string_buffer(size.value)
                        if n.NtQueryInformationProcess(handle,60,buffer,size,ctypes.byref(size))==0:
                            u=UString.from_buffer(buffer)
                            command=ctypes.wstring_at(u.buffer,u.length//2)
                            if "--type=renderer" in command:
                                assert "--no-sandbox" not in command
                                token=ctypes.c_void_p()
                                assert a.OpenProcessToken(handle,8,ctypes.byref(token))
                                try:
                                    need=wintypes.DWORD()
                                    a.GetTokenInformation(token,25,None,0,ctypes.byref(need))
                                    info=ctypes.create_string_buffer(need.value)
                                    assert a.GetTokenInformation(token,25,info,need,ctypes.byref(need))
                                    sid=ctypes.c_void_p.from_buffer(info).value
                                    count=a.GetSidSubAuthorityCount(sid)[0]
                                    found.append(a.GetSidSubAuthority(sid,count-1)[0])
                                finally:k.CloseHandle(token)
                finally:
                    if handle:k.CloseHandle(handle)
            valid=k.Process32NextW(snapshot,ctypes.byref(entry))
    finally:k.CloseHandle(snapshot)
    # Active document renderers lower to Untrusted (0); Chromium may also have
    # a not-yet-bound spare renderer at Low (4096).
    assert 0 in found and all(value<=4096 for value in found),found
    print("renderer sandbox integrity levels:",found,flush=True)

def renderer_sandbox(parent):
    if os.name == "nt":
        return renderer_integrities(parent)
    pending = [parent]
    seen = set()
    renderers = []
    platforms = set()
    while pending and len(seen) < 128:
        pid = pending.pop()
        if pid in seen: continue
        seen.add(pid)
        proc = Path("/proc") / str(pid)
        try:
            for task in (proc / "task").iterdir():
                try: pending.extend(int(child) for child in (task / "children").read_text().split())
                except FileNotFoundError: pass
            arguments = (proc / "cmdline").read_bytes().split(b"\0")
            command = b" ".join(arguments)
            platforms.update(match.group().decode() for match in re.finditer(rb"--ozone-platform=[^\s\0]+", command))
            if b"--type=renderer" in command:
                assert b"--no-sandbox" not in command
                status = dict(line.split(":", 1) for line in (proc / "status").read_text().splitlines() if ":" in line)
                assert status["Seccomp"].strip() == "2", status
                assert status["NoNewPrivs"].strip() == "1", status
                renderers.append(pid)
        except (FileNotFoundError, ProcessLookupError): pass
    assert renderers, ("no renderer descendants", seen)
    assert not (EXE.parent / "chrome-sandbox").exists()
    print("renderer sandbox Seccomp=2 NoNewPrivs=1; Ozone arguments:", sorted(platforms), flush=True)

class Session:
    def __init__(self, env=None, options=0, scale=0.0):
        self.err = (LOGS / ("smoke-stderr-" + str(time.time_ns()) + ".log")).open("wb")
        self.p = subprocess.Popen([str(EXE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=self.err, cwd=HOST_CWD, env=env)
        self.q = queue.Queue(256)
        self.events = []
        self.sequence = 1
        self.resource_count = 0
        self.hold = False
        threading.Thread(target=self.read, daemon=True).start()
        self.nonce = os.urandom(32)
        req = self.send(1, body=hello_body(self.nonce, options, scale))
        try:
            reply = self.until(lambda f: f[1] == req and f[0] in (100, 106))
        except BaseException:
            self.abort()
            print("hello startup exit",self.p.returncode,flush=True)
            raise
        assert reply[0] == 100 and reply[4][:32] == self.nonce
        identity = reply[4][32:]
        length, = struct.unpack_from("<I", identity)
        rid = identity[4:4+length].decode()
        rest = identity[4+length:]
        version_length, = struct.unpack_from("<I", rest)
        version = rest[4:4+version_length].decode()
        assert struct.unpack("<I", rest[4+version_length:]) == (options,), "Hello must confirm the host options"
        assert rid == os.environ.get("INFINIFRAME_CEF_SMOKE_RID","win-x64")
        assert version == "156.0.8+g426920c+chromium-156.0.8078.48"
        print("hello ok", self.p.pid, flush=True)
    def exact(self, n):
        out = b""
        while len(out) < n:
            data = self.p.stdout.read(n - len(out))
            if not data: raise EOFError
            out += data
        return out
    def read(self):
        try:
            while True:
                n, = struct.unpack("<I", self.exact(4))
                assert 28 <= n <= 8 * 1024 * 1024
                b = self.exact(n)
                version, kind, req, view, gen = struct.unpack_from("<HHQQQ", b)
                assert version == PROTOCOL
                self.q.put((kind, req, view, gen, b[28:]), timeout=5)
        except BaseException as error:
            self.q.put(error)
    def send(self, kind, view=0, gen=0, body=b"", req=None, fragmented=False):
        if req is None:
            self.sequence += 1
            req = self.sequence
        b = struct.pack("<HHQQQ", PROTOCOL, kind, req, view, gen) + body
        b = struct.pack("<I", len(b)) + b
        if fragmented:
            for byte in b:
                self.p.stdin.write(bytes([byte]))
                self.p.stdin.flush()
        else:
            self.p.stdin.write(b)
            self.p.stdin.flush()
        return req
    def service(self, f):
        if f[0] != 103: return
        self.resource_count += 1
        b = f[4]
        n, = struct.unpack_from("<I", b); method = b[4:4+n].decode()
        b = b[4+n:]; n, = struct.unpack_from("<I", b); url = b[4:4+n].decode()
        if "/hold" in url and self.hold: return
        status = 200
        mime = "text/html"
        data = b"""<!doctype html><html><head><meta charset="utf-8"></head><body>CEF native smoke
<script>
infiniframe.host.postData('page-loaded');
infiniframe.host.receiveCallback(function(s){infiniframe.host.postData('echo:'+s);});
</script></body></html>"""
        if "/binary" in url:
            mime = "application/octet-stream"; data = bytes([0,1,128,255,0,42])
        elif "/empty" in url:
            mime = "text/plain"; data = b""
        elif "/missing" in url:
            status = 404; mime = "text/plain"; data = b"not found"
        elif "/query" in url:
            mime = "text/plain"; data = url.encode()
        elif "/module.js" in url:
            mime = "text/javascript"; data = b"export const answer = 42;"
        elif "/frame.html" in url:
            mime = "text/html"; data = b"<!doctype html><body>isolated subframe</body>"
        body = struct.pack("<i", status) + string(mime) + struct.pack("<I", len(data)) + data
        self.send(11, f[2], f[3], body, f[1])
    def until(self, predicate, timeout=25):
        deadline = time.monotonic() + timeout
        while True:
            remain = deadline-time.monotonic()
            if remain <= 0: raise TimeoutError("waiting, events=" + repr(self.events[-20:]))
            try:
                f = self.q.get(timeout=remain)
            except queue.Empty:
                raise TimeoutError("events="+repr(self.events[-20:])) from None
            if isinstance(f, BaseException): raise f
            self.events.append((f[0],f[1],f[2],f[3],b"<hello redacted>") if f[1]==2 and f[2]==0 else f)
            self.service(f)
            if f[0] == 105:
                print("fault", text(f[4]), flush=True)
            if predicate(f): return f
    def command(self, kind, view=0, gen=0, body=b"", error=False, fragmented=False):
        req = self.send(kind, view, gen, body, fragmented=fragmented)
        f = self.until(lambda f: f[1] == req and f[0] in (100,106))
        assert f[0] == (106 if error else 100), f
        return f
    def create(self, view, flags=0, limits=(0, 0, 0, 0), bounds=None):
        boot = "infiniframe.host.postData('bootstrap-before-page');"
        start = len(self.events)
        bounds = bounds or (100+view*30,100,640,480)
        self.command(2, view, body=string("Native CEF smoke "+str(view))+
                     struct.pack("<iiii",*bounds)+
                     string("app://localhost/index.html")+string(boot)+window_style(flags, limits))
        # Match public creation: native allocation is followed by a confirmed
        # initial-bounds command before the consumer exposes WindowCreated.
        self.command(7, view, body=struct.pack("<iiii",*bounds))
        assert parse_state(self.command(12, view)[4])[:4] == bounds
        predicate = lambda f: f[0] == 101 and f[2] == view
        f = next((f for f in self.events[start:] if predicate(f)),None)
        if f is None: f = self.until(predicate)
        gen = f[3]; assert gen
        predicate = lambda f: f[0] == 102 and f[2] == view and text(f[4]) == "page-loaded"
        if not any(predicate(f) for f in self.events[start:]): self.until(predicate)
        # Every opaque event must follow Document for exactly that window and immutable generation.
        known = set()
        for f in self.events:
            if f[0] == 101: known.add((f[2],f[3]))
            if f[0] == 102: assert (f[2],f[3]) in known
        return gen
    def evaluate(self, view, gen, script):
        return json.loads(text(self.command(6,view,gen,string(script))[4]))
    def close(self):
        self.command(10)
        self.p.stdin.close()
        assert self.p.wait(timeout=15) == 0
        self.err.close()
    def abort(self):
        try: self.p.stdin.close()
        except Exception: pass
        try: self.p.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.p.kill(); self.p.wait()
        self.err.close()

def long_linux_profile_path():
    if os.name == "nt":
        return
    with tempfile.TemporaryDirectory(prefix="if-cef-profile-", dir="/tmp") as folder:
        state = Path(folder) / ("long-valid-profile-location-" * 5) / "state"
        state.mkdir(parents=True)
        assert len(str(state)) > 120
        previous = os.environ.get("XDG_STATE_HOME")
        os.environ["XDG_STATE_HOME"] = str(state)
        session = None
        try:
            session = Session()
            generation = session.create(3)
            assert session.evaluate(3, generation, "6*7") == 42
            renderer_sandbox(session.p.pid)
            profiles = list((state / "InfiniFrame/Cef/Profiles").iterdir())
            assert len(profiles) == 1, profiles
            profile = profiles[0]
            assert profile.is_dir() and not profile.is_symlink()
            assert stat.S_IMODE(profile.stat().st_mode) == 0o700
            # Identify the actual temporary socket directory from this browser's
            # open Unix sockets, rather than guessing which /tmp entry is ours.
            inodes = set()
            for descriptor in (Path("/proc") / str(session.p.pid) / "fd").iterdir():
                try:
                    match = re.fullmatch(r"socket:\[(\d+)\]", os.readlink(descriptor))
                    if match:
                        inodes.add(match.group(1))
                except FileNotFoundError:
                    pass
            runtimes = set()
            for line in Path("/proc/net/unix").read_text().splitlines()[1:]:
                fields = line.split(maxsplit=7)
                if len(fields) == 8 and fields[6] in inodes and fields[7].startswith("/tmp/ifc-"):
                    runtimes.add(Path("/tmp") / fields[7].split("/")[2])
            assert len(runtimes) == 1, (runtimes, inodes)
            runtime = runtimes.pop()
            assert re.fullmatch(r"ifc-[A-Za-z0-9]{6}", runtime.name), runtime
            assert runtime.is_dir() and not runtime.is_symlink()
            metadata = runtime.stat()
            assert metadata.st_uid == os.getuid() and stat.S_IMODE(metadata.st_mode) == 0o700
            assert runtime.parent == Path("/tmp") and len(str(runtime)) < 30
            assert not runtime.is_relative_to(state)
            session.close()
            assert not runtime.exists(), runtime
            assert not profile.exists(), profile
            print("long XDG profile: Hello/browser sandbox, short owned 0700 IPC directory and clean shutdown verified", flush=True)
        finally:
            if session is not None:
                session.abort()
            if previous is None:
                os.environ.pop("XDG_STATE_HOME", None)
            else:
                os.environ["XDG_STATE_HOME"] = previous

def windows_profile_cleanup():
    if os.name != "nt":
        return
    # Keep Chromium's nested cache filenames below Windows path limits, and
    # affect only the launched child's environment. Never clean a user profile.
    temporary = Path(tempfile.gettempdir()).resolve()
    scratch = Path(tempfile.mkdtemp(prefix="ifc-", dir=temporary)).resolve()
    assert scratch.parent == temporary
    try:
        for mode in ("graceful", "eof"):
            local = scratch / mode
            local.mkdir()
            child_env = os.environ.copy()
            child_env["LOCALAPPDATA"] = str(local)
            session = None
            try:
                session = Session(env=child_env)
                generation = session.create(3)
                assert session.evaluate(3, generation, "6*7") == 42
                renderer_sandbox(session.p.pid)
                profiles = local / "InfiniFrame/Cef/Profiles"
                entries = list(profiles.iterdir())
                assert len(entries) == 1, entries
                profile = entries[0]
                assert re.fullmatch(r"[1-9][0-9]*", profile.name), profile
                assert profile.is_dir() and not profile.is_symlink()
                assert not (profile.stat(follow_symlinks=False).st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT)
                if mode == "graceful":
                    session.close()
                else:
                    session.p.stdin.close()
                    assert session.p.wait(timeout=15) == 0
                # Check the original path before any test-owned directory cleanup.
                # The old file logger leaves this directory despite host exit0.
                assert not profile.exists(), (mode, "native profile survived exit", profile)
                assert not list(profiles.iterdir()), (mode, "unexpected profile remains")
                print("PASS Windows private profile removed after", mode, flush=True)
            finally:
                if session is not None:
                    session.abort()
            # Only empty, known test-created ancestors are removed. A failure
            # preserves the scratch tree for investigation; no recursive delete.
            for directory in (profiles, profiles.parent, profiles.parent.parent, local):
                directory.rmdir()
        scratch.rmdir()
    except BaseException:
        print("Windows profile regression scratch preserved:", scratch, flush=True)
        raise


def run():
    s = Session()
    try:
        gen1 = s.create(1); gen2 = s.create(2)
        assert gen1 != gen2
        renderer_sandbox(s.p.pid)
        assert s.evaluate(1,gen1,"({answer:6*7,url:location.href,bridge:typeof infiniframe.host.postData})")["answer"] == 42
        assert text(s.command(5,1,gen2,string("wrong-window"),error=True)[4])=="CEF_DOCUMENT_RETIRED"
        assert text(s.command(6,1,gen2,string("42"),error=True)[4])=="CEF_DOCUMENT_RETIRED"
        for kind in (5,6):
            assert text(s.command(kind,9999,gen1,string("missing-context"),error=True)[4])=="CEF_DOCUMENT_RETIRED"
            assert text(s.command(kind,1,0,string("missing-generation"),error=True)[4])=="CEF_DOCUMENT_RETIRED"
        opaque = "'; throw new Error('must not run'); //\n\u2603\x00tail"
        s.command(5,1,gen1,string(opaque),fragmented=True)
        echo=next((f for f in s.events if f[0]==102 and f[2]==1 and text(f[4])=="echo:"+opaque),None)
        if echo is None: echo=s.until(lambda f:f[0]==102 and f[2]==1 and text(f[4])=="echo:"+opaque)
        assert text(echo[4])=="echo:"+opaque
        s.command(6,1,gen1,string("throw new Error('private detail')"),error=True)
        s.command(6,1,gen1,string("Promise.resolve(42)"),error=True)
        s.command(4,1,body=string("https://example.invalid/"),error=True)
        assert s.evaluate(1,gen1,"window.open('app://localhost/index.html')===null") is True
        for v,g in ((1,gen1),(2,gen2)):
            s.command(7,v,body=struct.pack("<iiii",120,150,700,500))
            s.command(8,v,body=string("Updated native title "+str(v)))
            for state in (1,2,3,4,5,6):
                unobservable=state==4 and os.environ.get("INFINIFRAME_CEF_EXPECT_MINIMIZE")=="unobservable"
                result=s.command(9,v,body=struct.pack("<i",state),error=unobservable)
                if unobservable:
                    assert "did not report minimized state" in text(result[4])
                    print("EXPECTED_FAILURE: minimized state unobservable; bounded Error106 (minimization NOT QUALIFIED)",flush=True)
                state_packet=s.command(12,v)
                x,y,w,h,flags,title,url=parse_state(state_packet[4])
                if state!=4: assert w>=64 and h>=64,(state,x,y,w,h,flags)
                assert title=="Updated native title "+str(v)
                assert url=="app://localhost/index.html"
                if state==2: assert flags&1
                if state==3: assert not flags&1
        def chunk(kind,data):
            return struct.pack(">I",len(data))+kind+data+struct.pack(">I",zlib.crc32(kind+data))
        png=b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",16,16,8,6,0,0,0))+chunk(b"IDAT",zlib.compress((b"\x00"+bytes([24,100,180,255])*16)*16))+chunk(b"IEND",b"")
        s.command(13,1,body=struct.pack("<I",len(png))+png)
        s.command(13,1,body=struct.pack("<I",4)+b"nope",error=True)
        script = """Promise.all([
fetch('/binary').then(r=>r.arrayBuffer()).then(b=>Array.from(new Uint8Array(b))),
fetch('/missing').then(async r=>[r.status,await r.text()]),
fetch('/empty').then(r=>r.text()),
fetch('/binary',{method:'HEAD'}).then(async r=>[r.status,r.headers.get('content-length'),(await r.arrayBuffer()).byteLength]),
fetch('/query?value=exact%20query').then(r=>r.text())
]).then(x=>infiniframe.host.postData('resources:'+JSON.stringify(x))); 'started'"""
        assert s.evaluate(1,gen1,script) == "started"
        f = s.until(lambda f:f[0]==102 and text(f[4]).startswith("resources:"))
        results = json.loads(text(f[4])[10:])
        assert results[0] == [0,1,128,255,0,42], results
        assert results[1] == [404,"not found"] and results[2] == "" and results[3] == [200,"6",0], results
        assert results[4].endswith("/query?value=exact%20query"), results
        print("binary/404/empty/HEAD/query resources ok", flush=True)
        assert s.evaluate(1,gen1,"import('/module.js').then(m=>infiniframe.host.postData('module:'+m.answer)); 'importing'")=="importing"
        s.until(lambda f:f[0]==102 and text(f[4])=="module:42")
        assert s.evaluate(1,gen1,"var isolatedFrame=document.createElement('iframe'); isolatedFrame.src='/frame.html'; isolatedFrame.onload=()=>infiniframe.host.postData('subframe:'+typeof isolatedFrame.contentWindow.infiniframe); document.body.appendChild(isolatedFrame); 'framing'")=="framing"
        s.until(lambda f:f[0]==102 and text(f[4])=="subframe:undefined")
        print("custom scheme ES modules and main-frame-only bridge ok",flush=True)
        s.hold=True
        assert s.evaluate(1,gen1,"fetch('/hold').catch(()=>{}); 'holding'") == "holding"
        held = next((f for f in reversed(s.events) if f[0]==103 and b"/hold" in f[4]),None)
        if held is None: held=s.until(lambda f:f[0]==103 and b"/hold" in f[4])
        start=len(s.events)
        navigation=s.command(4,1,body=string("app://localhost/reloaded"))
        document=next((f for f in s.events[start:] if f[0]==101 and f[2]==1 and f[3]==navigation[3]),None)
        if document is None: document=s.until(lambda f:f[0]==101 and f[2]==1 and f[3]==navigation[3])
        gen3 = [f[3] for f in s.events if f[0]==101 and f[2]==1][-1]
        assert gen3 != gen1
        assert any(f[0]==107 and f[1]==held[1] for f in s.events)
        s.send(11,held[2],held[3],struct.pack("<i",200)+string("text/plain")+struct.pack("<I",4)+b"late",held[1])
        assert text(s.command(5,1,gen1,string("stale"),error=True)[4])=="CEF_DOCUMENT_RETIRED"
        assert text(s.command(6,1,gen1,string("42"),error=True)[4])=="CEF_DOCUMENT_RETIRED"
        assert s.evaluate(2,gen2,"41+1") == 42
        # Graceful native user-close via normal top-level WM_CLOSE, not protocol close.
        if os.name == "nt":
            user32=ctypes.windll.user32
            enumproc=ctypes.WINFUNCTYPE(ctypes.c_bool,ctypes.c_void_p,ctypes.c_void_p)
            windows=[]
            @enumproc
            def enum(hwnd,lparam):
                pid=ctypes.c_ulong()
                user32.GetWindowThreadProcessId(ctypes.c_void_p(hwnd),ctypes.byref(pid))
                if pid.value==s.p.pid and user32.IsWindowVisible(ctypes.c_void_p(hwnd)):
                    windows.append(hwnd)
                return True
            user32.EnumWindows(enum,0)
            assert len(windows)==2,windows
            user32.PostMessageW(ctypes.c_void_p(windows[0]),0x0010,0,0)
            closed=s.until(lambda f:f[0]==104)
        else:
            start=len(s.events)
            s.command(3,2)
            closed=next((f for f in s.events[start:] if f[0]==104 and f[2]==2),None)
            if closed is None: closed=s.until(lambda f:f[0]==104 and f[2]==2)
        s.command(3,closed[2])
        # Reopen the same managed view ID after closure.
        again=s.create(closed[2])
        assert again not in (gen1,gen2,gen3)
        print("generation retirement, cancellation, close and multiwindow reopen ok", flush=True)
        s.close()
        print("shutdown exit=0, resources="+str(s.resource_count),flush=True)
    finally:
        s.abort()
    # Pipe EOF must close a real window and terminate without Shutdown.
    s=Session()
    try:
        s.create(9)
        s.p.stdin.close()
        assert s.p.wait(timeout=15)==0
        print("EOF with live browser exits=0",flush=True)
    finally:s.abort()
    long_linux_profile_path()
    windows_profile_cleanup()

if __name__=="__main__":
    run()
