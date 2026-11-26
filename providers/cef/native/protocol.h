#pragma once
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace infini {
// Protocol 3 adds host options to Hello and window style fields to Create.
constexpr uint16_t kProtocolVersion = 3;
// Hello host option flags (protocol 3). Unknown bits are rejected.
constexpr uint32_t kHostInlineStyleAttributes = 1;  // CSP adds style-src-attr 'unsafe-inline'
constexpr uint32_t kHostDisablePinchZoom = 2;
constexpr uint32_t kHostDisableOverscroll = 4;
constexpr uint32_t kHostKnownOptions = 7;
// Create window flags (protocol 3). Unknown bits are rejected.
constexpr uint32_t kWindowFrameless = 1;      // no native frame or title bar
constexpr uint32_t kWindowFixedSize = 2;      // the user cannot resize or maximize the window
constexpr uint32_t kWindowContentBounds = 4;  // bounds, queries and min/max describe the content area
constexpr uint32_t kWindowKnownFlags = 7;
constexpr size_t kMaxFrame = 8 * 1024 * 1024;
constexpr size_t kMaxQueuedBytes = 64 * 1024 * 1024;
constexpr size_t kMaxQueuedFrames = 256;
using Bytes = std::vector<uint8_t>;
struct Packet {
  uint16_t kind = 0;
  uint64_t request = 0, view = 0, generation = 0;
  Bytes body;
};
struct Reader {
  const Bytes& bytes;
  size_t offset = 0;
  bool valid = true;
  uint64_t Number(size_t count);
  int32_t Int() { return static_cast<int32_t>(Number(4)); }
  std::string String();
  Bytes Blob();
  bool Done() const { return valid && offset == bytes.size(); }
};
void Number(Bytes& bytes, uint64_t value, size_t count);
void String(Bytes& bytes, const std::string& value);
void Blob(Bytes& bytes, const Bytes& value);
bool ValidUtf8(const std::string& value);
uint64_t RandomId();
uint64_t ParseId(const std::string& value);
void Diagnostic(const char* fixed_message) noexcept;

// Why the most recent Read() failed. Fixed values only; no frame contents.
enum class ReadFailure { None, Eof, IdleTimeout, PartialFrameTimeout, InvalidFrame, IoError, Stopped };
const char* Describe(ReadFailure failure) noexcept;

// The duplicated transport handles are never inherited by CEF subprocesses.
class Transport {
 public:
  // Waits without a deadline for the first byte of a frame (idle connection).
  static constexpr unsigned kWaitForever = 0;
  // Once a frame has started, its remaining prefix and body must arrive within this deadline.
  static constexpr unsigned kPartialFrameDeadlineMs = 30000;
  bool Open();
  // idle_ms bounds only the wait for the first byte (kWaitForever: no bound); the rest of the
  // frame always has the partial-frame deadline.
  bool Read(Packet& packet, unsigned idle_ms);
  ReadFailure LastReadFailure() const noexcept { return last_read_failure_; }
  // The version field of the most recent frame rejected for its version (0 when none was).
  uint16_t LastRejectedVersion() const noexcept { return last_rejected_version_; }
  // True when the session ended through a transport failure other than the managed peer's EOF
  // (deadline, invalid frame, I/O error, queue overflow, write failure): not an orderly end.
  bool FailedAbnormally() const noexcept { return failed_once_.load() && !orderly_end_.load(); }
#ifdef INFINIFRAME_CEF_TRANSPORT_TESTS
  // Test-only seams, compiled only into the standalone transport tests (never into the payload).
  bool OpenForTesting(void* input, void* output);
  void SetPartialFrameDeadlineForTesting(unsigned milliseconds) { partial_frame_ms_ = milliseconds; }
#endif
  void Start(std::function<void()> available, std::function<void()> failed);
  bool Pop(Packet& packet);
  bool FinishDrain();
  bool Send(Packet packet);
  void Stop();
  ~Transport();
 private:
  // timeout_ms == kWaitForever waits until data, EOF, an error or Stop().
  bool ReadExact(uint8_t* data, size_t size, unsigned timeout_ms, ReadFailure timeout_failure);
  unsigned partial_frame_ms_ = kPartialFrameDeadlineMs;
  ReadFailure last_read_failure_ = ReadFailure::None;
  uint16_t last_rejected_version_ = 0;
  bool WriteExact(const uint8_t* data, size_t size);
  void Fail() noexcept;
  std::atomic<bool> stop_{false}, failed_once_{false}, orderly_end_{false};
  std::mutex mutex_;
  std::condition_variable ready_;
  std::deque<Bytes> outgoing_;
  std::deque<Packet> incoming_;
  size_t outgoing_bytes_ = 0, incoming_bytes_ = 0;
  bool incoming_scheduled_ = false;
  std::thread reader_, writer_;
  std::function<void()> available_, failed_;
#ifdef _WIN32
  void* input_ = nullptr;
  void* output_ = nullptr;
#else
  int input_ = -1, output_ = -1;
#endif
};
}
