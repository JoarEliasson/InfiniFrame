using System.Collections.Concurrent;
using System.Diagnostics;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     10. The provider's callbacks must not run in the caller's contexts. The whole host lifecycle is driven from a
///     dedicated "application thread" that blocks on every provider task (the host contract allows InfiniFrame's
///     dispatcher thread to do exactly that). Execution context and synchronization context are tested separately:
///     clearing one does not clear the other, which is why the provider starts its loops under
///     <see cref="ExecutionContext.SuppressFlow" /> and awaits with <c>ConfigureAwait(false)</c>.
/// </summary>
[Category("Fake")]
public sealed class ContextIsolationTests {
    private const string ApplicationState = "application ambient state";
    private static readonly string[] Callbacks = [nameof(RecordingSink.Created), nameof(RecordingSink.DocumentAttached), nameof(RecordingSink.MessageReceived)];

    [Test]
    [Timeout(60_000)]
    public async Task SinkCallbacks_DoNotRunInTheCallersExecutionContext(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("ctx-exec", "normal");
        (int appThread, RecordingSink sink) = await DriveAsync(harness, context: null);
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            foreach (string callback in Callbacks) {
                IReadOnlyList<SinkCall> calls = sink.Named(callback);
                await Assert.That(calls.Count).IsGreaterThan(0).Because($"{callback} was raised");
                await Assert.That(calls.All(static call => call.AsyncLocalValue is null)).IsTrue()
                    .Because($"{callback} must not see the caller's AsyncLocal; saw [{string.Join(", ", calls.Select(static call => call.AsyncLocalValue ?? "null"))}]");
                await Assert.That(calls.All(call => call.ThreadId != appThread)).IsTrue()
                    .Because($"{callback} must not run on the application thread {appThread}; ran on [{string.Join(", ", calls.Select(static call => call.ThreadId))}]");
            }
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task ProviderContinuations_NeverPostToTheCallersSynchronizationContext(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("ctx-sync", "normal");
        var counting = new CountingSynchronizationContext();
        (int appThread, RecordingSink sink) = await DriveAsync(harness, counting);
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();
        Console.WriteLine($"[sync-context] posts {counting.Posts}, sends {counting.Sends}");
        foreach (string origin in counting.Origins.Take(3)) Console.WriteLine($"[sync-context] post from:\n{origin}");

        using (Assert.Multiple()) {
            await Assert.That(counting.Posts).IsEqualTo(0).Because("no provider continuation may be posted to the caller's context");
            await Assert.That(counting.Sends).IsEqualTo(0);
            foreach (string callback in Callbacks) {
                IReadOnlyList<SinkCall> calls = sink.Named(callback);
                await Assert.That(calls.Count).IsGreaterThan(0).Because($"{callback} was raised");
                await Assert.That(calls.All(static call => call.SynchronizationContextType is null)).IsTrue()
                    .Because($"{callback} must run without a synchronization context; saw [{string.Join(", ", calls.Select(static call => call.SynchronizationContextType ?? "null"))}]");
                await Assert.That(calls.All(call => call.ThreadId != appThread)).IsTrue()
                    .Because($"{callback} must not run on the application thread {appThread}; ran on [{string.Join(", ", calls.Select(static call => call.ThreadId))}]");
            }
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    /// <summary>
    ///     Runs start, create, an inbound message, a navigation, a resource request, an outbound send, close, shutdown
    ///     and disposal on the application thread with <see cref="RecordingSink.ApplicationState" /> set and, when given,
    ///     <paramref name="context" /> installed. Every provider task is waited for synchronously.
    /// </summary>
    private static Task<(int AppThread, RecordingSink Sink)> DriveAsync(HostHarness harness, SynchronizationContext? context) {
        var done = new TaskCompletionSource<(int, RecordingSink)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try {
                if (context is not null) SynchronizationContext.SetSynchronizationContext(context);
                RecordingSink.ApplicationState.Value = ApplicationState;
                Block(harness.StartAsync());
                (CefWindowHost window, RecordingSink sink) = Block(harness.CreateWindowAsync());
                Block(window.DirectAsync("message 0x5EED000000000001 hello from the document"));
                Block(sink.WaitForAsync(nameof(RecordingSink.MessageReceived), 1));
                InfiniFrameHostNavigation navigation = Block(window.NavigateAsync(new Uri("app://localhost/next.html"), CancellationToken.None).AsTask());
                Block(sink.WaitForAsync(nameof(RecordingSink.DocumentAttached), 2));
                Block(window.DirectAsync("resource 0x7201 GET app://localhost/app.css"));
                Block(harness.Fake.WaitForAsync(static entry => entry.IsCommand("resource-response"), "the resource response"));
                Block(window.SendMessageAsync(navigation.Document, "to the navigated document", CancellationToken.None).AsTask());
                Block(HostHarness.CloseWindowAsync(window, sink));
                Block(harness.ShutdownAsync());
                Block(harness.DisposeHostAsync());
                done.TrySetResult((Environment.CurrentManagedThreadId, sink));
            }
            catch (Exception exception) {
                done.TrySetException(exception);
            }
            finally {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }) { IsBackground = true, Name = "application thread (test)" };
        thread.Start();
        return Wait.WithinAsync(done.Task, "the application thread's run", TimeSpan.FromSeconds(50));
    }

    private static void Block(Task task) => task.WaitAsync(Wait.Step).GetAwaiter().GetResult();
    private static T Block<T>(Task<T> task) => task.WaitAsync(Wait.Step).GetAwaiter().GetResult();

    /// <summary>Counts posts and sends; posted work runs on the pool so a stray post can never deadlock the blocked thread.</summary>
    private sealed class CountingSynchronizationContext : SynchronizationContext {
        private readonly ConcurrentQueue<string> _origins = new();
        private int _posts;
        private int _sends;

        internal int Posts => Volatile.Read(ref _posts);
        internal int Sends => Volatile.Read(ref _sends);
        internal IReadOnlyList<string> Origins => [.. _origins];

        public override void Post(SendOrPostCallback d, object? state) {
            Interlocked.Increment(ref _posts);
            _origins.Enqueue(new StackTrace(1, false).ToString());
            ThreadPool.UnsafeQueueUserWorkItem(static item => {
                (SendOrPostCallback callback, object? value) = ((SendOrPostCallback, object?))item!;
                callback(value);
            }, (d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) {
            Interlocked.Increment(ref _sends);
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
