#include "protocol.h"
#include <algorithm>
#include <charconv>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <optional>
#ifdef _WIN32
#include <windows.h>
#include <bcrypt.h>
#include <fcntl.h>
#include <io.h>
#else
#include <cerrno>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <sys/random.h>
#include <unistd.h>
#endif

namespace infini {
void Diagnostic(const char* message) noexcept { std::fprintf(stderr, "InfiniFrame.Cef: %s\n", message); }
const char* Describe(ReadFailure failure) noexcept {
  switch (failure) {
    case ReadFailure::None: return "transport read succeeded";
    case ReadFailure::Eof: return "transport closed by the managed peer (EOF)";
    case ReadFailure::IdleTimeout: return "transport idle deadline exceeded before a frame started";
    case ReadFailure::PartialFrameTimeout: return "transport partial frame deadline exceeded";
    case ReadFailure::InvalidFrame: return "transport received an invalid frame";
    case ReadFailure::IoError: return "transport read failed";
    case ReadFailure::Stopped: return "transport stopped";
  }
  return "transport read failed";
}
void Number(Bytes& out, uint64_t value, size_t count) {
  for (size_t i = 0; i < count; ++i) out.push_back(static_cast<uint8_t>(value >> (i * 8)));
}
uint64_t Reader::Number(size_t count) {
  if (!valid || count > bytes.size() - offset) { valid = false; return 0; }
  uint64_t value = 0;
  for (size_t i = 0; i < count; ++i) value |= uint64_t(bytes[offset++]) << (i * 8);
  return value;
}
bool ValidUtf8(const std::string& s) {
  const auto* p = reinterpret_cast<const unsigned char*>(s.data());
  size_t i = 0;
  while (i < s.size()) {
    uint32_t c = p[i++]; unsigned n = 0; uint32_t min = 0;
    if (c < 128) continue;
    if (c >= 0xc2 && c <= 0xdf) { n = 1; c &= 31; min = 128; }
    else if (c >= 0xe0 && c <= 0xef) { n = 2; c &= 15; min = 2048; }
    else if (c >= 0xf0 && c <= 0xf4) { n = 3; c &= 7; min = 65536; }
    else return false;
    if (n > s.size() - i) return false;
    while (n--) { if ((p[i] & 0xc0) != 0x80) return false; c = (c << 6) | (p[i++] & 63); }
    if (c < min || c > 0x10ffff || (c >= 0xd800 && c <= 0xdfff)) return false;
  }
  return true;
}
Bytes Reader::Blob() {
  size_t n = static_cast<size_t>(Number(4));
  if (!valid || n > bytes.size() - offset) { valid = false; return {}; }
  Bytes out(bytes.begin() + offset, bytes.begin() + offset + n); offset += n; return out;
}
std::string Reader::String() {
  auto value = Blob();
  std::string result(value.begin(), value.end());
  if (!ValidUtf8(result)) valid = false;
  return result;
}
void String(Bytes& out, const std::string& value) {
  Number(out, value.size(), 4); out.insert(out.end(), value.begin(), value.end());
}
void Blob(Bytes& out, const Bytes& value) {
  Number(out, value.size(), 4); out.insert(out.end(), value.begin(), value.end());
}
uint64_t ParseId(const std::string& value) {
  uint64_t n = 0;
  auto result = std::from_chars(value.data(), value.data() + value.size(), n);
  return result.ec == std::errc{} && result.ptr == value.data() + value.size() ? n : 0;
}
uint64_t RandomId() {
  uint64_t n = 0;
#ifdef _WIN32
  if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&n), sizeof(n), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) return 0;
#else
  size_t done = 0;
  while (done < sizeof(n)) {
    auto got = getrandom(reinterpret_cast<char*>(&n) + done, sizeof(n) - done, 0);
    if (got < 0 && errno == EINTR) continue;
    if (got <= 0) return 0;
    done += static_cast<size_t>(got);
  }
#endif
  return n;
}
bool Transport::Open() {
#ifdef _WIN32
  auto process = GetCurrentProcess();
  auto in = GetStdHandle(STD_INPUT_HANDLE), out = GetStdHandle(STD_OUTPUT_HANDLE);
  if (GetFileType(in) != FILE_TYPE_PIPE || GetFileType(out) != FILE_TYPE_PIPE) return false;
  if (!DuplicateHandle(process, in, process, &input_, 0, FALSE, DUPLICATE_SAME_ACCESS) ||
      !DuplicateHandle(process, out, process, &output_, 0, FALSE, DUPLICATE_SAME_ACCESS)) return false;
  SetHandleInformation(in, HANDLE_FLAG_INHERIT, 0);
  SetHandleInformation(out, HANDLE_FLAG_INHERIT, 0);
  _dup2(_fileno(stderr), _fileno(stdout));
  SetStdHandle(STD_OUTPUT_HANDLE, GetStdHandle(STD_ERROR_HANDLE));
#else
  signal(SIGPIPE, SIG_IGN);
  input_ = fcntl(STDIN_FILENO, F_DUPFD_CLOEXEC, 3);
  output_ = fcntl(STDOUT_FILENO, F_DUPFD_CLOEXEC, 3);
  if (input_ < 0 || output_ < 0) return false;
  fcntl(STDIN_FILENO, F_SETFD, FD_CLOEXEC);
  dup2(STDERR_FILENO, STDOUT_FILENO);
  fcntl(output_, F_SETFL, fcntl(output_, F_GETFL) | O_NONBLOCK);
#endif
  return true;
}
bool Transport::ReadExact(uint8_t* data, size_t size, unsigned timeout_ms, ReadFailure timeout_failure) {
  const bool bounded = timeout_ms != kWaitForever;
  auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
  while (size && !stop_) {
    if (bounded && std::chrono::steady_clock::now() >= deadline) { last_read_failure_ = timeout_failure; return false; }
#ifdef _WIN32
    DWORD available = 0, n = 0;
    if (!PeekNamedPipe(input_, nullptr, 0, nullptr, &available, nullptr)) {
      last_read_failure_ = GetLastError() == ERROR_BROKEN_PIPE ? ReadFailure::Eof : ReadFailure::IoError; return false;
    }
    if (!available) { std::this_thread::sleep_for(std::chrono::milliseconds(10)); continue; }
    if (!ReadFile(input_, data, static_cast<DWORD>(std::min<size_t>(size, available)), &n, nullptr)) {
      last_read_failure_ = GetLastError() == ERROR_BROKEN_PIPE ? ReadFailure::Eof : ReadFailure::IoError; return false;
    }
    if (!n) { last_read_failure_ = ReadFailure::Eof; return false; }
#else
    pollfd fd{input_, POLLIN, 0};
    int polled = poll(&fd, 1, 100);
    if (polled < 0 && errno == EINTR) continue;
    if (polled < 0) { last_read_failure_ = ReadFailure::IoError; return false; }
    if (!polled) continue;
    auto n = read(input_, data, size);
    if (n < 0 && errno == EINTR) continue;
    if (n <= 0) { last_read_failure_ = n == 0 ? ReadFailure::Eof : ReadFailure::IoError; return false; }
#endif
    data += n; size -= n;
  }
  if (size) { last_read_failure_ = ReadFailure::Stopped; return false; }
  return true;
}
bool Transport::Read(Packet& packet, unsigned idle_ms) {
  last_read_failure_ = ReadFailure::None;
  last_rejected_version_ = 0;
  Bytes prefix(4);
  // Waiting for the first byte of the next frame is idle time: a live connection may stay idle
  // indefinitely (kWaitForever). Only the launch handshake bounds it.
  if (!ReadExact(prefix.data(), 1, idle_ms, ReadFailure::IdleTimeout)) return false;
  // A started frame that stops arriving is stalled, not idle: the remaining prefix and the body
  // share one partial-frame deadline measured from the first byte.
  const auto started = std::chrono::steady_clock::now();
  auto remaining = [&]() -> unsigned {
    auto spent = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
    return spent >= static_cast<long long>(partial_frame_ms_) ? 1u : static_cast<unsigned>(partial_frame_ms_ - spent);
  };
  if (!ReadExact(prefix.data() + 1, 3, remaining(), ReadFailure::PartialFrameTimeout)) return false;
  Reader length{prefix}; auto size = length.Number(4);
  if (size < 28 || size > kMaxFrame) { last_read_failure_ = ReadFailure::InvalidFrame; return false; }
  Bytes body(static_cast<size_t>(size));
  if (!ReadExact(body.data(), body.size(), remaining(), ReadFailure::PartialFrameTimeout)) return false;
  Reader r{body};
  if (const auto version = static_cast<uint16_t>(r.Number(2)); version != kProtocolVersion) {
    last_rejected_version_ = version; last_read_failure_ = ReadFailure::InvalidFrame; return false;
  }
  packet.kind = static_cast<uint16_t>(r.Number(2));
  packet.request = r.Number(8); packet.view = r.Number(8); packet.generation = r.Number(8);
  packet.body.assign(body.begin() + 28, body.end());
  return true;
}
#ifdef INFINIFRAME_CEF_TRANSPORT_TESTS
bool Transport::OpenForTesting(void* input, void* output) {
#ifdef _WIN32
  input_ = input; output_ = output;
#else
  input_ = static_cast<int>(reinterpret_cast<intptr_t>(input));
  output_ = static_cast<int>(reinterpret_cast<intptr_t>(output));
#endif
  return true;
}
#endif
bool Transport::WriteExact(const uint8_t* data, size_t size) {
  while (size && !stop_) {
#ifdef _WIN32
    DWORD n = 0;
    if (!WriteFile(output_, data, static_cast<DWORD>(size), &n, nullptr) || !n) return false;
#else
    pollfd fd{output_, POLLOUT, 0};
    int polled = poll(&fd, 1, 100);
    if (polled < 0 && errno == EINTR) continue;
    if (polled < 0) return false;
    if (!polled) continue;
    auto n = write(output_, data, size);
    if (n < 0 && (errno == EINTR || errno == EAGAIN)) continue;
    if (n <= 0) return false;
#endif
    data += n; size -= n;
  }
  return !size;
}
void Transport::Fail() noexcept {
  if (!failed_once_.exchange(true) && failed_) {
    try { failed_(); } catch (...) { Diagnostic("transport failure callback failed"); }
  }
}
void Transport::Start(std::function<void()> available, std::function<void()> failed) {
  available_ = std::move(available); failed_ = std::move(failed);
  writer_ = std::thread([this] {
    try {
      for (;;) {
        Bytes bytes;
        {
          std::unique_lock lock(mutex_);
          ready_.wait(lock, [this] { return stop_ || !outgoing_.empty(); });
          if (stop_) return;
          bytes = std::move(outgoing_.front()); outgoing_.pop_front();
          // Include the currently blocked write in the queue's byte budget.
        }
        if (!WriteExact(bytes.data(), bytes.size())) { Fail(); return; }
        { std::lock_guard lock(mutex_); outgoing_bytes_ -= bytes.size(); }
        ready_.notify_all();
      }
    } catch (...) { Fail(); }
  });
  reader_ = std::thread([this] {
    try {
      while (!stop_) {
        Packet packet;
        // Idle connections are allowed without a deadline; a started frame has the partial-frame
        // deadline. EOF from the managed peer is an orderly end of the session.
        if (!Read(packet, kWaitForever)) {
          if (!stop_) {
            if (last_read_failure_ == ReadFailure::Eof) orderly_end_ = true;
            Diagnostic(Describe(last_read_failure_)); Fail();
          }
          return;
        }
        bool notify;
        {
          std::lock_guard lock(mutex_);
          if (incoming_.size() >= kMaxQueuedFrames || incoming_bytes_ + packet.body.size() + 28 > kMaxQueuedBytes) {
            Fail(); return;
          }
          notify = !incoming_scheduled_; incoming_scheduled_ = true;
          incoming_bytes_ += packet.body.size() + 28;
          incoming_.push_back(std::move(packet));
        }
        if (notify) available_();
      }
    } catch (...) { Fail(); }
  });
}
bool Transport::Pop(Packet& packet) {
  std::lock_guard lock(mutex_);
  if (incoming_.empty()) return false;
  packet = std::move(incoming_.front()); incoming_.pop_front();
  incoming_bytes_ -= packet.body.size() + 28;
  return true;
}
bool Transport::FinishDrain() {
  std::lock_guard lock(mutex_);
  if (incoming_.empty()) { incoming_scheduled_ = false; return false; }
  return true;
}
bool Transport::Send(Packet packet) {
  if (packet.body.size() + 28 > kMaxFrame || stop_) { Fail(); return false; }
  Bytes bytes;
  bytes.reserve(packet.body.size() + 32);
  Number(bytes, packet.body.size() + 28, 4); Number(bytes, kProtocolVersion, 2); Number(bytes, packet.kind, 2);
  Number(bytes, packet.request, 8); Number(bytes, packet.view, 8); Number(bytes, packet.generation, 8);
  bytes.insert(bytes.end(), packet.body.begin(), packet.body.end());
  bool overflow = false;
  {
    std::lock_guard lock(mutex_);
    overflow = outgoing_.size() >= kMaxQueuedFrames - 1 || outgoing_bytes_ + bytes.size() > kMaxQueuedBytes;
    if (!overflow) { outgoing_bytes_ += bytes.size(); outgoing_.push_back(std::move(bytes)); }
  }
  if (overflow) { Fail(); return false; }
  ready_.notify_one(); return true;
}
void Transport::Stop() {
  if (writer_.joinable()) {
    std::unique_lock lock(mutex_);
    ready_.wait_for(lock, std::chrono::seconds(2), [this] { return !outgoing_bytes_; });
  }
  stop_ = true; ready_.notify_all();
#ifdef _WIN32
  if (writer_.joinable()) CancelSynchronousIo(writer_.native_handle());
#endif
  if (reader_.joinable()) reader_.join();
  if (writer_.joinable()) writer_.join();
}
Transport::~Transport() {
  Stop();
#ifdef _WIN32
  if (input_) CloseHandle(input_);
  if (output_) CloseHandle(output_);
#else
  if (input_ >= 0) close(input_);
  if (output_ >= 0) close(output_);
#endif
}
}
