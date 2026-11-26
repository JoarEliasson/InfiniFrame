using System.Security.Cryptography;
using System.Text;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     9. Resource requests from the companion are relayed to InfiniFrame's sink and answered with the sink's status,
///     or with the provider's own containment status (502 relay error, 504 deadline, 403 foreign origin). The fake logs
///     the status, content type and body digest of every response frame it receives. No case may fault the session.
/// </summary>
[Category("Fake")]
public sealed class ResourceRelayTests {
    private const ulong RequestId = 0x7001;
    private const string DefaultMime = "text/plain; charset=utf-8";
    private static readonly byte[] Page = Encoding.UTF8.GetBytes("<!doctype html><title>relay</title><h1>relayed unchanged</h1>");
    private static readonly byte[] Missing = Encoding.UTF8.GetBytes("missing");

    [Test]
    [Timeout(60_000)]
    [Arguments("ok", 200, "")]
    [Arguments("not-found", 404, "")]
    [Arguments("status-out-of-range", 502, "status-out-of-range")]
    [Arguments("invalid-mime", 502, "invalid-mime")]
    [Arguments("read-failure", 502, "read-failure")]
    [Arguments("oversize", 502, "oversize")]
    [Arguments("oversize-unknown-length", 502, "oversize")]
    [Arguments("timeout", 504, "timeout")]
    [Arguments("outside-app-origin", 403, "")]
    public async Task ResourceRequest_IsAnsweredWithItsMappedStatus(string scenario, int expectedStatus, string expectedRelayError,
        CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("res-" + scenario[..Math.Min(12, scenario.Length)], "normal", [],
            options => options.ResourceTimeout = scenario == "timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30));
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();
        var bodies = new List<TrackingStream>();
        sink.Resources = (request, token) => Respond(scenario, token, bodies);
        string url = scenario == "outside-app-origin" ? "https://example.com/app.js" : $"app://localhost/assets/{scenario}.bin";

        await window.DirectAsync($"resource 0x{RequestId:X} GET {url}");
        FakeLogEntry response = await harness.Fake.WaitForAsync(static entry => entry.IsCommand("resource-response") && entry.Request == RequestId,
            "the provider's response to the resource request");
        FakeLogEntry request = harness.Fake.ReadLog().Single(static entry => entry.IsSent("resource-request"));
        // Disposal of the body may finish just after the response frame was queued; the assertion below reports it.
        await Wait.ExceptionAsync(() => Wait.UntilAsync(() => Disposed(bodies), "the response body to be disposed", TimeSpan.FromSeconds(5)), "body disposal");
        CefHostDiagnostics state = harness.Diagnostics;

        // The session is still healthy: a later command succeeds and nothing faulted.
        Exception? later = await Wait.ExceptionAsync(() => window.SetTitleAsync("healthy after the resource", CancellationToken.None).AsTask(), "a later command");
        bool completed = harness.Host.Completion.IsCompleted;
        IReadOnlyList<SinkCall> served = sink.Named(nameof(RecordingSink.GetResourceAsync));
        await HostHarness.CloseWindowAsync(window, sink);
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        var expectedErrors = expectedRelayError.Length == 0 ? new Dictionary<string, long>() : new Dictionary<string, long> { [expectedRelayError] = 1 };
        using (Assert.Multiple()) {
            await Assert.That(response.Int("status")).IsEqualTo(expectedStatus);
            await Assert.That(response.View).IsEqualTo(request.View).Because("a response echoes its request's view");
            await Assert.That(response.Generation).IsEqualTo(request.Generation).Because("a response echoes its request's document generation");
            await Assert.That(Render(state.ResourceRelayErrors)).IsEqualTo(Render(expectedErrors));
            await Assert.That(Render(state.ResourceStatuses)).IsEqualTo($"{expectedStatus}=1");
            await Assert.That(served.Count).IsEqualTo(scenario == "outside-app-origin" ? 0 : 1).Because("a foreign origin never reaches InfiniFrame");
            if (served.Count == 1) await Assert.That(served[0].Text).IsEqualTo($"GET {url}");
            switch (scenario) {
                case "ok":
                    await Assert.That(response.Str("mime")).IsEqualTo("text/html; charset=utf-8");
                    await Assert.That(response.Int("body_bytes")).IsEqualTo(Page.Length);
                    await Assert.That(response.Str("body_sha256")).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(Page)));
                    break;
                case "not-found":
                    await Assert.That(response.Str("mime")).IsEqualTo(DefaultMime);
                    await Assert.That(response.Int("body_bytes")).IsEqualTo(Missing.Length);
                    break;
                default:
                    await Assert.That(response.Str("mime")).IsEqualTo(DefaultMime);
                    await Assert.That(response.Int("body_bytes")).IsEqualTo(0);
                    break;
            }
            await Assert.That(Disposed(bodies)).IsTrue().Because("the provider owns and disposes the response body");
            await Assert.That(later).IsNull();
            await Assert.That(completed).IsFalse();
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0);
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task RequestCancelledByTheCompanion_GetsNoResponse(CancellationToken cancellationToken) {
        const ulong cancelled = 0x7100, after = 0x7101;
        await using HostHarness harness = HostHarness.Create("res-cancelled", "normal");
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.Resources = async (request, token) => {
            if (!request.Url.AbsolutePath.EndsWith("cancelled.bin", StringComparison.Ordinal))
                return new InfiniFrameHostResourceResponse { StatusCode = 200, ContentType = DefaultMime, ContentLength = Missing.Length, Body = new MemoryStream(Missing) };
            entered.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) {
                observedCancellation.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("unreachable");
        };

        await window.DirectAsync($"resource 0x{cancelled:X} GET app://localhost/assets/cancelled.bin");
        await Wait.WithinAsync(entered.Task, "InfiniFrame's handler to start serving the request");
        await window.DirectAsync($"cancel-resource 0x{cancelled:X}");
        await Wait.WithinAsync(observedCancellation.Task, "the handler's token to be cancelled by the companion's cancellation");
        // The resource worker is sequential and responses leave in order: once the next request's response arrived,
        // the cancelled request would have been answered before it if it were answered at all.
        await window.DirectAsync($"resource 0x{after:X} GET app://localhost/assets/after.txt");
        await harness.Fake.WaitForAsync(static entry => entry.IsCommand("resource-response") && entry.Request == after, "the next request's response");
        IReadOnlyList<FakeLogEntry> responses = harness.Fake.Received("resource-response");
        CefHostDiagnostics state = harness.Diagnostics;
        await HostHarness.CloseWindowAsync(window, sink);
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;

        using (Assert.Multiple()) {
            await Assert.That(responses.Any(static entry => entry.Request == cancelled)).IsFalse().Because("the protocol forbids answering a request the companion cancelled");
            await Assert.That(responses.Count).IsEqualTo(1);
            await Assert.That(Render(state.ResourceRelayErrors)).IsEqualTo("").Because("a companion cancellation is not a relay error (not a 504)");
            await Assert.That(Render(state.ResourceStatuses)).IsEqualTo("200=1");
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0);
        }
    }

    private static async ValueTask<InfiniFrameHostResourceResponse> Respond(string scenario, CancellationToken token, List<TrackingStream> bodies) {
        TrackingStream Track(TrackingStream stream) {
            lock (bodies) bodies.Add(stream);
            return stream;
        }
        switch (scenario) {
            case "ok":
                return new InfiniFrameHostResourceResponse {
                    StatusCode = 200, ContentType = "text/html; charset=utf-8", ContentLength = Page.Length, Body = Track(new TrackingStream(Page, seekable: true))
                };
            case "not-found":
                return new InfiniFrameHostResourceResponse {
                    StatusCode = 404, ContentType = DefaultMime, ContentLength = Missing.Length, Body = Track(new TrackingStream(Missing, seekable: true))
                };
            case "status-out-of-range":
                return new InfiniFrameHostResourceResponse { StatusCode = 700, ContentType = DefaultMime, ContentLength = 1, Body = Track(new TrackingStream("x"u8.ToArray(), seekable: true)) };
            case "invalid-mime":
                return new InfiniFrameHostResourceResponse {
                    StatusCode = 200, ContentType = "text/html\r\nX-Injected: yes", ContentLength = 1, Body = Track(new TrackingStream("x"u8.ToArray(), seekable: true))
                };
            case "read-failure":
                return new InfiniFrameHostResourceResponse {
                    StatusCode = 200, ContentType = "application/octet-stream", Body = Track(new TrackingStream(new byte[100_000], seekable: false, failAfterBytes: 4096))
                };
            case "oversize":
                return new InfiniFrameHostResourceResponse {
                    StatusCode = 200, ContentType = "application/octet-stream", ContentLength = 9 * 1024 * 1024,
                    Body = Track(new TrackingStream(new byte[9 * 1024 * 1024], seekable: true))
                };
            case "oversize-unknown-length":
                return new InfiniFrameHostResourceResponse {
                    StatusCode = 200, ContentType = "application/octet-stream", Body = Track(new TrackingStream(new byte[9 * 1024 * 1024], seekable: false))
                };
            case "timeout":
                await Task.Delay(Timeout.InfiniteTimeSpan, token);// a handler slower than the provider's deadline (it honours its token)
                throw new InvalidOperationException("unreachable");
            default:
                throw new InvalidOperationException($"No sink response is defined for scenario {scenario}.");
        }
    }

    private static bool Disposed(List<TrackingStream> bodies) {
        lock (bodies) return bodies.All(static body => body.Disposed);
    }

    private static string Render<TKey>(IReadOnlyDictionary<TKey, long> values) where TKey : notnull =>
        string.Join(",", values.OrderBy(static pair => pair.Key.ToString(), StringComparer.Ordinal).Select(static pair => $"{pair.Key}={pair.Value}"));

    /// <summary>A body stream that records its disposal; optionally non-seekable, optionally failing mid-read.</summary>
    private sealed class TrackingStream(byte[] data, bool seekable, int failAfterBytes = -1) : Stream {
        private readonly MemoryStream _inner = new(data, writable: false);
        private int _disposed;

        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? _inner.Length : throw new NotSupportedException();

        public override long Position {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            if (failAfterBytes >= 0 && _inner.Position >= failAfterBytes) throw new IOException("simulated body read failure");
            return _inner.Read(failAfterBytes >= 0 ? buffer[..(int)Math.Min(buffer.Length, failAfterBytes - _inner.Position)] : buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() {}
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            Volatile.Write(ref _disposed, 1);
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
