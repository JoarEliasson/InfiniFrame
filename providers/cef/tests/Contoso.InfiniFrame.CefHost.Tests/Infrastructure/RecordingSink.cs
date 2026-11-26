using System.Drawing;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

/// <summary>One call the provider made on a window's event sink, with the context it was made in.</summary>
internal sealed record SinkCall(
    string Name,
    ulong Document,
    string? Text,
    int ThreadId,
    string? AsyncLocalValue,
    string? SynchronizationContextType);

/// <summary>
///     The test's <see cref="IInfiniFrameWindowHostEvents" />: records every call (never blocks, never throws, as
///     InfiniFrame's sink does) together with the managed thread, the "application" <see cref="AsyncLocal{T}" /> value
///     and the <see cref="SynchronizationContext" /> current at the time of the call.
/// </summary>
internal sealed class RecordingSink : IInfiniFrameWindowHostEvents {
    /// <summary>Stands for application or InfiniFrame ambient state; tests set it before calling the provider.</summary>
    internal static readonly AsyncLocal<string?> ApplicationState = new();

    private readonly object _gate = new();
    private readonly List<SinkCall> _calls = [];

    /// <summary>Resolves resource requests; the default answers 404 without a body.</summary>
    internal Func<InfiniFrameHostResourceRequest, CancellationToken, ValueTask<InfiniFrameHostResourceResponse>>? Resources { get; set; }

    internal IReadOnlyList<SinkCall> Calls {
        get {
            lock (_gate) return [.. _calls];
        }
    }

    internal IReadOnlyList<SinkCall> Named(string name) => [.. Calls.Where(call => call.Name == name)];

    internal Task WaitForAsync(string name, int count, TimeSpan? timeout = null) =>
        Wait.UntilAsync(() => Named(name).Count >= count, $"{count} sink call(s) of {name}", timeout);

    private void Record(string name, ulong document = 0, string? text = null) {
        var call = new SinkCall(name, document, text, Environment.CurrentManagedThreadId, ApplicationState.Value,
            SynchronizationContext.Current?.GetType().FullName);
        lock (_gate) _calls.Add(call);
    }

    public void Created() => Record(nameof(Created));
    public void DocumentAttached(InfiniFrameDocumentId document, Uri url) => Record(nameof(DocumentAttached), document.Value, url.AbsoluteUri);
    public void DocumentRetired(InfiniFrameDocumentId document) => Record(nameof(DocumentRetired), document.Value);
    public void MessageReceived(InfiniFrameDocumentId document, string message) => Record(nameof(MessageReceived), document.Value, message);

    public async ValueTask<InfiniFrameHostResourceResponse> GetResourceAsync(InfiniFrameHostResourceRequest request, CancellationToken cancellationToken) {
        Record(nameof(GetResourceAsync), 0, $"{request.Method} {request.Url.AbsoluteUri}");
        if (Resources is not {} resources) return new InfiniFrameHostResourceResponse { StatusCode = 404 };
        return await resources(request, cancellationToken).ConfigureAwait(false);
    }

    // Not declared by the provider's capabilities: any call is recorded and fails the test that looks for it.
    public void BoundsChanged(Rectangle bounds) => Record(nameof(BoundsChanged), 0, bounds.ToString());
    public void StateChanged(InfiniFrameHostWindowState state) => Record(nameof(StateChanged), 0, state.ToString());

    public ValueTask<bool> CloseRequestedAsync(CancellationToken cancellationToken) {
        Record(nameof(CloseRequestedAsync));
        return ValueTask.FromResult(true);
    }

    public void Faulted(Exception exception) => Record(nameof(Faulted), 0, $"{exception.GetType().Name}: {exception.Message}");
    public void Closed() => Record(nameof(Closed));
}
