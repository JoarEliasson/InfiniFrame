using LocalControlsUi.Components;
using LocalControlsUi.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

// Renderer frame/event APIs are used only by this dependency-free test harness.
#pragma warning disable BL0006

namespace LocalControlsUi.Tests;

internal static class DialogRegressionChecks
{
    public static async Task RepeatedOpenAsync()
    {
        await using var fixture = new DialogFixture();
        var mount = fixture.Renderer.StartAsync();
        await fixture.JS.ImportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task first = Task.CompletedTask;
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            first = fixture.Renderer.Dialog.ShowAsync();
            Require(!first.IsCompleted, "Opening must wait for the held module import.");
            Require(ReferenceEquals(first, fixture.Renderer.Dialog.ShowAsync()), "Repeated initialization-time opens must share one task.");
        });
        fixture.JS.Import.TrySetResult(fixture.JS.Module);
        await fixture.JS.Module.CreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            Require(ReferenceEquals(first, fixture.Renderer.Dialog.ShowAsync()), "Creating JS helpers must still coalesce opens."));
        fixture.JS.Module.Create.TrySetResult(fixture.JS.Dialogs);
        await fixture.JS.Dialogs.ShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            Require(ReferenceEquals(first, fixture.Renderer.Dialog.ShowAsync()), "Unacknowledged showParent must still coalesce opens."));
        fixture.JS.Dialogs.ShowAcknowledged.TrySetResult();
        await Task.WhenAll(first, mount).WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.JS.Dialogs.ShowCalls == 1, "Only one showParent call may be issued.");
        var firstGeneration = fixture.JS.Dialogs.Generation;

        await ClosePendingLoginAsync(fixture);
        // Reopening must acquire a fresh generation; a stale close must not cancel its login.
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.Dialog.ShowAsync());
        Require(fixture.JS.Dialogs.Generation > firstGeneration, "A closed dialog must permit a fresh opening.");
        await ClosePendingLoginAsync(fixture, firstGeneration);

        // A close may beat the show acknowledgement. Completing that await must not resurrect 'open'.
        fixture.JS.Dialogs.ShowAcknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task opening = Task.CompletedTask;
        Task closed = Task.CompletedTask;
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            opening = fixture.Renderer.Dialog.ShowAsync();
            Require(!opening.IsCompleted, "The show acknowledgement must remain held.");
            closed = fixture.JS.Dialogs.CloseFromBrowserAsync();
        });
        await closed.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.JS.Dialogs.ShowAcknowledged.TrySetResult();
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.Dialog.ShowAsync());
        Require(fixture.JS.Dialogs.ShowCalls == 4, "An acknowledged-but-already-closed opening must not block a later open.");
    }

    private static async Task ClosePendingLoginAsync(DialogFixture fixture, long? staleGeneration = null)
    {
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.ChangePasswordAsync("demo"));
        Task login = Task.CompletedTask;
        Task closed = Task.CompletedTask;
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            login = fixture.Renderer.SubmitAsync();
            Require(!login.IsCompleted && fixture.Renderer.IsBusy, "The actual fake-login event must be pending.");
            Require(fixture.Renderer.Password == "demo", "The actual input binding must contain the entered password.");
            Require(fixture.Renderer.Dialog.ShowAsync().IsCompletedSuccessfully, "An already-open request must be a no-op.");
            Require(fixture.Renderer.Password == "demo" && fixture.Renderer.IsBusy, "Repeated opens must not reset an active login.");
            if (staleGeneration is { } stale)
            {
                Require(fixture.Renderer.Dialog.ParentClosedAsync(stale).IsCompletedSuccessfully, "A stale close must be ignored.");
                Require(fixture.Renderer.Password == "demo" && fixture.Renderer.IsBusy, "A stale close must not clear a newer login.");
            }
            closed = fixture.JS.Dialogs.CloseFromBrowserAsync();
        });
        await Task.WhenAll(closed, login).WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            Require(fixture.Renderer.Password == "", "A matching close must clear the actual bound password.");
            Require(!fixture.Renderer.IsBusy, "A matching close must end the actual login operation.");
            Require(fixture.Renderer.Status == "Pending work cancelled. Dialog closed.", "A cancelled login must not publish a late result.");
        });
    }

    public static async Task OpenFailureAsync()
    {
        await using var fixture = new DialogFixture();
        fixture.JS.ReleaseInitialization();
        await fixture.Renderer.StartAsync();
        fixture.JS.Dialogs.FailNextShow = true;
        Task opening = Task.CompletedTask;
        Task login = Task.CompletedTask;
        await fixture.Renderer.Dispatcher.InvokeAsync(() => { opening = fixture.Renderer.Dialog.ShowAsync(); });
        await fixture.JS.Dialogs.ShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.ChangePasswordAsync("demo"));
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
        {
            login = fixture.Renderer.SubmitAsync();
            Require(!login.IsCompleted && fixture.Renderer.IsBusy, "Login must be pending while show acknowledgement is held.");
        });
        fixture.JS.Dialogs.ShowAcknowledged.TrySetResult();
        try
        {
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Expected the controlled interop failure.");
        }
        catch (JSException exception) when (exception.Message == "Controlled show acknowledgement failure") { }
        await login.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            Require(fixture.Renderer.Password == "" && !fixture.Renderer.IsBusy, "Failed opening must also retire a login started before its acknowledgement."));
        Require(!fixture.JS.Dialogs.IsOpen && fixture.JS.Dialogs.CloseCalls == 1, "A failed opening must retire any JS dialog it already opened.");
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Renderer.Dialog.ShowAsync());
        Require(fixture.JS.Dialogs.IsOpen && fixture.JS.Dialogs.ShowCalls == 2, "The failed opening must not leave the opening guard held.");
        await ClosePendingLoginAsync(fixture);
    }

    public static async Task DisposeDuringInitializationAsync()
    {
        await using var fixture = new DialogFixture();
        var mount = fixture.Renderer.StartAsync();
        await fixture.JS.ImportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task opening = Task.CompletedTask;
        await fixture.Renderer.Dispatcher.InvokeAsync(() => { opening = fixture.Renderer.Dialog.ShowAsync(); });
        var disposal = fixture.DisposeAsync().AsTask();
        fixture.JS.Import.TrySetResult(fixture.JS.Module);
        await Task.WhenAll(mount, opening, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.JS.Module.CreateCalls == 0 && fixture.JS.Dialogs.ShowCalls == 0, "Disposal must prevent helper creation and opening after initialization resumes.");
        Require(fixture.JS.Module.Released, "Disposal must release the imported module.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class DialogFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private Task? _disposal;
        public ControlledJSRuntime JS { get; } = new();
        public DialogRenderer Renderer { get; }
        public DialogFixture()
        {
            _provider = new ServiceCollection().AddLogging().AddLocalControlsUi()
                .AddSingleton<IJSRuntime>(JS).BuildServiceProvider();
            Renderer = new(_provider, _provider.GetRequiredService<ILoggerFactory>());
        }
        public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());
        private async Task DisposeCoreAsync()
        {
            try
            {
                var disposal = Renderer.DisposeAsync();
                // A failed assertion must not strand the harness behind one of its own held gates.
                JS.ReleaseInitialization();
                JS.Dialogs.ShowAcknowledged.TrySetResult();
                await disposal.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { await _provider.DisposeAsync(); }
            if (Renderer.Errors.Count != 0) throw new AggregateException(Renderer.Errors);
        }
    }

    private sealed class DialogRenderer(IServiceProvider services, ILoggerFactory logging) : Renderer(services, logging)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public ControlDialog Dialog { get; private set; } = default!;
        public List<Exception> Errors { get; } = [];
        private int _root;
        public Task StartAsync() => Dispatcher.InvokeAsync(() =>
        {
            Dialog = (ControlDialog)InstantiateComponent(typeof(ControlDialog));
            _root = AssignRootComponentId(Dialog);
            return RenderRootComponentAsync(_root);
        });
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        public Task ChangePasswordAsync(string value) => DispatchEventAsync(EventId("input", "id", "lc-password", "onchange"), null, new ChangeEventArgs { Value = value });
        public Task SubmitAsync() => DispatchEventAsync(EventId("form", null, null, "onsubmit"), null, EventArgs.Empty);
        public string Password => Attribute("input", "id", "lc-password", "value")?.ToString() ?? "";
        public bool IsBusy => Attribute("button", "type", "submit", "disabled") is true;
        public string Status
        {
            get
            {
                var frames = GetCurrentRenderTreeFrames(_root);
                var index = Element("p", "class", "lc-login-result");
                return string.Concat(frames.Array.Skip(index + 1).Take(frames.Array[index].ElementSubtreeLength - 1)
                    .Where(f => f.FrameType == RenderTreeFrameType.Text).Select(f => f.TextContent));
            }
        }
        private int Element(string name, string? key, string? value)
        {
            var frames = GetCurrentRenderTreeFrames(_root);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames.Array[i].FrameType != RenderTreeFrameType.Element || frames.Array[i].ElementName != name) continue;
                if (key is null) return i;
                for (var a = i + 1; a < frames.Count && frames.Array[a].FrameType == RenderTreeFrameType.Attribute; a++)
                    if (frames.Array[a].AttributeName == key && Equals(frames.Array[a].AttributeValue, value)) return i;
            }
            throw new InvalidOperationException($"Element {name} {key}={value} not found.");
        }
        private RenderTreeFrame AttributeFrame(string name, string? key, string? value, string attribute)
        {
            var frames = GetCurrentRenderTreeFrames(_root);
            for (var i = Element(name, key, value) + 1; i < frames.Count && frames.Array[i].FrameType == RenderTreeFrameType.Attribute; i++)
                if (frames.Array[i].AttributeName == attribute) return frames.Array[i];
            return default;
        }
        private object? Attribute(string name, string? key, string? value, string attribute) => AttributeFrame(name, key, value, attribute).AttributeValue;
        private ulong EventId(string name, string? key, string? value, string attribute)
        {
            var id = AttributeFrame(name, key, value, attribute).AttributeEventHandlerId;
            if (id == 0) throw new InvalidOperationException($"Actual event handler {attribute} not found.");
            return id;
        }
    }

    private sealed class ControlledJSRuntime : IJSRuntime
    {
        public TaskCompletionSource<IJSObjectReference> Import { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ImportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ControlledModule Module { get; } = new();
        public ControlledDialogs Dialogs { get; } = new();
        public void ReleaseInitialization()
        {
            Import.TrySetResult(Module);
            Module.Create.TrySetResult(Dialogs);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "import") throw new InvalidOperationException($"Unexpected JS call {identifier}");
            ImportStarted.TrySetResult();
            return new(CastAsync<TValue>(Import.Task));
        }
    }

    private sealed class ControlledModule : IJSObjectReference
    {
        public TaskCompletionSource<IJSObjectReference> Create { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CreateStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CreateCalls { get; private set; }
        public bool Released { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "createDialogs") throw new InvalidOperationException($"Unexpected module call {identifier}");
            CreateCalls++;
            CreateStarted.TrySetResult();
            var dialogs = (ControlledDialogs)await Create.Task;
            dialogs.Callback = (DotNetObjectReference<ControlDialog>)args![2]!;
            return (TValue)(object)dialogs;
        }
        public ValueTask DisposeAsync() { Released = true; return ValueTask.CompletedTask; }
    }

    private sealed class ControlledDialogs : IJSObjectReference
    {
        public DotNetObjectReference<ControlDialog> Callback { get; set; } = default!;
        public TaskCompletionSource ShowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShowAcknowledged { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Generation { get; private set; }
        public int ShowCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public bool IsOpen { get; private set; }
        public bool FailNextShow { get; set; }
        public Task CloseFromBrowserAsync()
        {
            IsOpen = false;
            return Callback.Value.ParentClosedAsync(Generation);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            switch (identifier)
            {
                case "showParent":
                    ShowCalls++;
                    IsOpen = true;
                    var accepted = Generation = (long)args![0]!;
                    ShowStarted.TrySetResult();
                    await ShowAcknowledged.Task;
                    if (FailNextShow) { FailNextShow = false; throw new JSException("Controlled show acknowledgement failure"); }
                    return (TValue)(object)accepted;
                case "closeParent":
                    CloseCalls++;
                    if (IsOpen) await CloseFromBrowserAsync();
                    break;
                case "dispose": IsOpen = false; break;
                default: throw new InvalidOperationException($"Unexpected dialog call {identifier}");
            }
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<T> CastAsync<T>(Task<IJSObjectReference> task) => (T)(object)await task;
}
