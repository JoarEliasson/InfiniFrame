using System.Collections.Concurrent;
using System.Text.Json;
using InfiniFrame.Hosting;

namespace Contoso.CoreGapRepro;

// In-process fake provider implementing ONLY the public host contract (InfiniFrame.Hosting). Every sink event is raised
// from one provider-owned ordered loop thread, never inline from a facade call. It also plays the page side of the
// Blazor IPC for whatever document is current, like InfiniFrame's own HostedBlazorTests do.

/// <summary>One SendMessageAsync call InfiniFrame made: the document it named and the payload.</summary>
public sealed record SentMessage(int Seq, ulong Document, bool Delivered, string Message) {
    /// <summary>The Blazor IPC message type (for example RenderBatch, BeginInvokeJS, Navigate).</summary>
    public string Type => Message.StartsWith("__bwv:[\"", StringComparison.Ordinal)
        ? Message[8..Message.IndexOf('"', 8)]
        : "(not a Blazor message)";

    /// <summary>The first argument as a number: the batch id of a RenderBatch, the task id of a BeginInvokeJS.</summary>
    public long? Arg1 {
        get {
            try {
                using var json = JsonDocument.Parse(Message.AsMemory(6));
                return json.RootElement[1].TryGetInt64(out long value) ? value : null;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or IndexOutOfRangeException) {
                return null;
            }
        }
    }

    public string Short => Message.Length <= 220 ? Message : Message[..220] + "...";
}

public sealed class FakeHostProvider : IInfiniFrameHostProvider {
    public string DisplayName => "in-process fake provider (Contoso.CoreGapRepro)";
    public Version ContractVersion { get; } = new(InfiniFrameHostContract.MajorVersion, InfiniFrameHostContract.MinorVersion);
    public InfiniFrameHostCapabilities Capabilities => InfiniFrameHostCapabilities.None;
    public FakeHost? Host { get; private set; }

    public IInfiniFrameHost CreateHost(InfiniFrameHostCreateContext context) => Host = new FakeHost();
}

public sealed class FakeHost : IInfiniFrameHost {
    private readonly BlockingCollection<Action> _loop = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeHost() {
        _thread = new Thread(() => {
            foreach (Action action in _loop.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "fake provider loop" };
        _thread.Start();
    }

    public TaskCompletionSource<FakeWindowHost> Window { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _completion.Task;

    /// <summary>Runs <paramref name="action" /> on the provider loop; completes after it returned.</summary>
    public Task OnLoop(Action action) {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => {
            try {
                action();
                done.TrySetResult();
            }
            catch (Exception exception) {
                done.TrySetException(exception);
            }
        }, done);
        return done.Task;
    }

    public void Post(Action action, TaskCompletionSource? orFail = null) {
        try { _loop.Add(action); }
        catch (InvalidOperationException) { orFail?.TrySetException(new ObjectDisposedException(nameof(FakeHost))); }
    }

    public ValueTask StartAsync(InfiniFrameHostStartContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask<IInfiniFrameWindowHost> CreateWindowAsync(InfiniFrameHostWindowRequest request,
        IInfiniFrameWindowHostEvents events, CancellationToken cancellationToken) {
        var window = new FakeWindowHost(this, events);
        await OnLoop(events.Created).ConfigureAwait(false);
        Window.TrySetResult(window);
        return window;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken) {
        _completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() {
        _loop.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeWindowHost(FakeHost host, IInfiniFrameWindowHostEvents events) : IInfiniFrameWindowHost {
    private ulong _current;// provider loop only
    private Uri? _url;// provider loop only
    private int _seq;

    public ConcurrentQueue<SentMessage> Sent { get; } = new();

    /// <summary>Every message the scripted page posted (MessageReceived), numbered in the same sequence as the sends.</summary>
    public ConcurrentQueue<(int Seq, ulong Document, string Message)> Inbound { get; } = new();

    public int LastSeq => Volatile.Read(ref _seq);

    /// <summary>When set, the page executes every probe.stamp call it receives and answers it (EndInvokeJS), as a real page does.</summary>
    public volatile bool AnswerStampCalls;

    public int Delivered(ulong document, string type) => Sent.Count(m => m.Delivered && m.Document == document && m.Type == type);

    // ---- contract: InfiniFrame -> provider -------------------------------------------------------------------------
    public ValueTask RequestCloseAsync(CancellationToken cancellationToken) {
        host.Post(events.Closed);
        return ValueTask.CompletedTask;
    }

    public ValueTask<InfiniFrameHostNavigation> NavigateAsync(Uri url, CancellationToken cancellationToken) =>
        ValueTask.FromException<InfiniFrameHostNavigation>(
            new InfiniFrameHostOperationException("The fake provider performs no native navigation (unused by this repro)."));

    /// <summary>Records every call; refuses a document that is not current, as a real provider must.</summary>
    public ValueTask SendMessageAsync(InfiniFrameDocumentId document, string message, CancellationToken cancellationToken) =>
        new(host.OnLoop(() => {
            bool current = document.Value != 0 && document.Value == _current;
            var sent = new SentMessage(Interlocked.Increment(ref _seq), document.Value, current, message);
            Sent.Enqueue(sent);
            if (!current) throw new InfiniFrameHostDocumentRetiredException(document);
            PlayPage(sent);
        }));

    public async ValueTask<Uri?> GetCurrentUrlAsync(CancellationToken cancellationToken) {
        Uri? url = null;
        await host.OnLoop(() => url = _url).ConfigureAwait(false);
        return url;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- the page side, scripted (always on the provider loop) -------------------------------------------------------
    private void PlayPage(SentMessage sent) {
        string? reply = sent.Type switch {
            "RenderBatch" => $"__bwv:[\"OnRenderCompleted\",{sent.Arg1},null]",
            "BeginInvokeJS" when AnswerStampCalls && sent.Message.Contains("\"probe.stamp\"", StringComparison.Ordinal)
                => $"__bwv:[\"EndInvokeJS\",{sent.Arg1},true,\"[{sent.Arg1},true,null]\"]",
            _ => null
        };
        if (reply is null) return;
        ulong document = sent.Document;
        // A later loop item, from the document that received the message, and only while that document is still live.
        host.Post(() => {
            if (_current == document) Raise(document, reply);
        });
    }

    private void Raise(ulong document, string message) {
        Inbound.Enqueue((Interlocked.Increment(ref _seq), document, message));
        events.MessageReceived(new InfiniFrameDocumentId(document), message);
    }

    public Task AttachAsync(ulong document, Uri url) => host.OnLoop(() => {
        _current = document;
        _url = url;
        events.DocumentAttached(new InfiniFrameDocumentId(document), url);
    });

    public Task RetireAsync(ulong document) => host.OnLoop(() => {
        if (_current == document) (_current, _url) = (0, null);
        events.DocumentRetired(new InfiniFrameDocumentId(document));
    });

    public Task PostAsync(ulong document, string message) => host.OnLoop(() => Raise(document, message));
}
