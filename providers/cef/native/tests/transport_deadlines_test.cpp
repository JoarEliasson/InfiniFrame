// Standalone transport deadline tests. No CEF is loaded and this executable is never part of the payload:
// it is built only with INFINIFRAME_CEF_TRANSPORT_TESTS, which enables the test seams in protocol.h.
// The partial-frame deadline is shortened to 300 ms so that idle versus stalled framing is testable quickly.
#include "../protocol.h"
#include <chrono>
#include <cstdio>
#include <string>
#include <thread>
#ifdef _WIN32
#include <windows.h>
#else
#include <cstdint>
#include <unistd.h>
#endif

using namespace infini;
using Clock = std::chrono::steady_clock;
using namespace std::chrono_literals;

namespace {
constexpr unsigned kPartialMs = 300;

struct Pipe {
#ifdef _WIN32
  HANDLE read = nullptr, write = nullptr;
  Pipe() {
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), nullptr, FALSE};
    CreatePipe(&read, &write, &attributes, 1 << 16);
  }
  void Write(const Bytes& bytes, size_t count) {
    DWORD written = 0;
    WriteFile(write, bytes.data(), static_cast<DWORD>(count), &written, nullptr);
  }
  void CloseWrite() { if (write) { CloseHandle(write); write = nullptr; } }
  void* ReadEnd() const { return read; }
  void* WriteEnd() const { return write; }
#else
  int fds[2] = {-1, -1};
  Pipe() { if (pipe(fds) != 0) fds[0] = fds[1] = -1; }
  void Write(const Bytes& bytes, size_t count) { (void)!::write(fds[1], bytes.data(), count); }
  void CloseWrite() { if (fds[1] >= 0) { close(fds[1]); fds[1] = -1; } }
  void* ReadEnd() const { return reinterpret_cast<void*>(static_cast<intptr_t>(fds[0])); }
  void* WriteEnd() const { return reinterpret_cast<void*>(static_cast<intptr_t>(fds[1])); }
#endif
};

Bytes Frame(uint16_t kind) {
  Bytes bytes;
  Number(bytes, 28, 4); Number(bytes, kProtocolVersion, 2); Number(bytes, kind, 2);
  Number(bytes, 7, 8); Number(bytes, 0, 8); Number(bytes, 0, 8);
  return bytes;
}

long long Since(Clock::time_point start) {
  return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - start).count();
}

int failures = 0;
void Report(const char* name, bool pass, long long ms, ReadFailure failure) {
  if (!pass) ++failures;
  std::printf("{\"test\":\"%s\",\"pass\":%s,\"elapsedMs\":%lld,\"failure\":\"%s\"}\n", name, pass ? "true" : "false", ms,
              Describe(failure));
  std::fflush(stdout);
}

// Runs one scenario: the writer thread feeds `input` while the transport reads with `idle_ms`.
template <typename Writer, typename Check>
void Scenario(const char* name, unsigned idle_ms, Writer writer, Check check) {
  Pipe input, output;
  Transport transport;
  transport.OpenForTesting(input.ReadEnd(), output.WriteEnd());
  transport.SetPartialFrameDeadlineForTesting(kPartialMs);
  std::thread feeder([&] { writer(input); });
  auto start = Clock::now();
  Packet packet;
  bool read = transport.Read(packet, idle_ms);
  long long ms = Since(start);
  feeder.join();
  Report(name, check(read, packet, ms, transport.LastReadFailure()), ms, transport.LastReadFailure());
}
}  // namespace

int main() {
  // 1. An idle live connection waits for the first byte without a deadline, here four times the partial deadline.
  Scenario("idle-beyond-partial-deadline-then-frame", Transport::kWaitForever,
      [](Pipe& pipe) { std::this_thread::sleep_for(1200ms); pipe.Write(Frame(42), 32); },
      [](bool read, const Packet& packet, long long ms, ReadFailure) { return read && packet.kind == 42 && ms >= 1100; });
  // 2. The launch handshake bounds its idle wait explicitly; that still times out as an idle timeout.
  Scenario("bounded-idle-handshake-times-out", 300,
      [](Pipe&) { std::this_thread::sleep_for(900ms); },
      [](bool read, const Packet&, long long ms, ReadFailure failure) {
        return !read && failure == ReadFailure::IdleTimeout && ms >= 250 && ms < 850; });
  // 3. A frame whose prefix stops after two bytes is stalled: it fails at the partial deadline, not later.
  Scenario("stalled-partial-prefix-fails-at-deadline", Transport::kWaitForever,
      [](Pipe& pipe) { pipe.Write(Frame(42), 2); std::this_thread::sleep_for(1500ms); },
      [](bool read, const Packet&, long long ms, ReadFailure failure) {
        return !read && failure == ReadFailure::PartialFrameTimeout && ms >= 250 && ms < 1200; });
  // 4. A complete prefix with a stalled body fails at the same frame deadline.
  Scenario("stalled-partial-body-fails-at-deadline", Transport::kWaitForever,
      [](Pipe& pipe) { pipe.Write(Frame(42), 14); std::this_thread::sleep_for(1500ms); },
      [](bool read, const Packet&, long long ms, ReadFailure failure) {
        return !read && failure == ReadFailure::PartialFrameTimeout && ms >= 250 && ms < 1200; });
  // 5. EOF while idle is reported as EOF (the orderly end of a session), immediately.
  Scenario("eof-while-idle", Transport::kWaitForever,
      [](Pipe& pipe) { std::this_thread::sleep_for(100ms); pipe.CloseWrite(); },
      [](bool read, const Packet&, long long ms, ReadFailure failure) { return !read && failure == ReadFailure::Eof && ms < 1000; });
  // 6. EOF in the middle of a frame is EOF too, not a timeout.
  Scenario("eof-mid-frame", Transport::kWaitForever,
      [](Pipe& pipe) { pipe.Write(Frame(42), 3); std::this_thread::sleep_for(100ms); pipe.CloseWrite(); },
      [](bool read, const Packet&, long long ms, ReadFailure failure) { return !read && failure == ReadFailure::Eof && ms < 1000; });
  // 7. A size outside the protocol bounds is an invalid frame.
  Scenario("invalid-frame-size", Transport::kWaitForever,
      [](Pipe& pipe) { Bytes bytes; Number(bytes, 5, 4); pipe.Write(bytes, 4); std::this_thread::sleep_for(100ms); },
      [](bool read, const Packet&, long long, ReadFailure failure) { return !read && failure == ReadFailure::InvalidFrame; });
  // 8. A frame delivered in small pieces inside the deadline is reassembled.
  Scenario("fragmented-frame-within-deadline", Transport::kWaitForever,
      [](Pipe& pipe) {
        Bytes frame = Frame(43);
        for (size_t offset = 0; offset < frame.size(); offset += 4) {
          Bytes piece(frame.begin() + static_cast<std::ptrdiff_t>(offset), frame.begin() + static_cast<std::ptrdiff_t>(offset + 4));
          pipe.Write(piece, piece.size());
          std::this_thread::sleep_for(15ms);
        }
      },
      [](bool read, const Packet& packet, long long, ReadFailure) { return read && packet.kind == 43; });
  std::printf("{\"summary\":\"transport-deadlines\",\"failures\":%d}\n", failures);
  return failures == 0 ? 0 : 1;
}
