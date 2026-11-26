using LocalControlsUi.Services;
using LocalControlsUi.Tests;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

// Dependency-free managed lifecycle checks. Run with dotnet run, not dotnet test.
// Controlled renderer/interop checks are managed fakes, not hybrid host or browser qualification.
var checks = new (string Name, Func<Task> Run)[]
{
    ("registration is idempotent; app backend survives retired document sessions", RetentionAsync),
    ("heartbeat updates readings without changing the edited setpoint", HeartbeatAsync),
    ("subscriptions are bounded and retiring one session leaves another usable", IsolationAsync),
    ("session retirement completes readers and rejects stale writes/subscriptions", RetirementAsync),
    ("concurrent retirement and writes leave balanced counters", ConcurrentRetirementAsync),
    ("backend disposal completes readers and retires every owned resource", BackendDisposalAsync),
    ("fake login accepts, rejects and observes cancellation", LoginAsync),
    ("actual App roots retire bindings and render retained backend geometry", RootRetirementAsync),
    ("repeated delayed dialog opens coalesce and close cancels actual component login", DialogRegressionChecks.RepeatedOpenAsync),
    ("dialog opening failure retires interop state and permits a clean retry", DialogRegressionChecks.OpenFailureAsync),
    ("dialog disposal during delayed initialization prevents opening and releases the module", DialogRegressionChecks.DisposeDuringInitializationAsync)
};

foreach (var (name, run) in checks)
{
    await run();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"PASS {checks.Length}/{checks.Length} managed checks");

static async Task RetentionAsync()
{
    var services = new ServiceCollection().AddLocalControlsUi().AddLocalControlsUi();
    await using var provider = services.BuildServiceProvider();
    var backend = provider.GetRequiredService<FakeControlBackend>();
    using (var scope = provider.CreateScope())
        Same(backend, scope.ServiceProvider.GetRequiredService<FakeControlBackend>());
    Equal(1, services.Count(s => s.ServiceType == typeof(FakeControlBackend)));
    Equal(50, backend.Read().Value);
    var previous = backend.OpenSession();
    using (var subscription = previous.Subscribe())
    {
        previous.WriteValue(75);
        Equal(75, backend.Read().Value);
        Throws<ArgumentOutOfRangeException>(() => previous.WriteValue(101));
        Throws<ArgumentOutOfRangeException>(() => previous.WriteValue(-1));
    }
    previous.Dispose();
    using var replacement = backend.OpenSession();
    using var replacementSubscription = replacement.Subscribe();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var reader = replacementSubscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
    True(await reader.MoveNextAsync());
    Equal(75, reader.Current.Value);
    True(replacement.Id != previous.Id);
    Equal(new ControlDiagnostics(1, 1, 2, 1, 1, 2, 1), backend.Diagnostics);
}

static async Task HeartbeatAsync()
{
    await using var backend = new FakeControlBackend();
    using var session = backend.OpenSession();
    session.WriteValue(23);
    using var subscription = session.Subscribe();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var reader = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
    True(await reader.MoveNextAsync());
    var first = reader.Current;
    do { True(await reader.MoveNextAsync()); } while (reader.Current.Heartbeat <= first.Heartbeat);
    Equal(23, reader.Current.Value);
    True(reader.Current.Temperature is >= 39 and <= 47);
    True(reader.Current.Flow is >= 55 and <= 70);
    True(reader.Current.Load is >= 58 and <= 72);
}

static async Task IsolationAsync()
{
    await using var backend = new FakeControlBackend();
    using var first = backend.OpenSession();
    using var second = backend.OpenSession();
    using var a = first.Subscribe();
    using var b = second.Subscribe();
    for (var i = 0; i < 10000; i++) first.WriteValue(i % 101);
    first.WriteValue(31);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var reader = b.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
    True(await reader.MoveNextAsync());
    // Slow readers see the latest value instead of a 10,000-item callback backlog.
    Equal(31, reader.Current.Value);
    first.Dispose();
    a.Dispose();
    a.Dispose();
    Equal(1, backend.Diagnostics.ActiveSessions);
    Equal(1, backend.Diagnostics.ActiveSubscriptions);
    second.WriteValue(62);
    do { True(await reader.MoveNextAsync()); } while (reader.Current.Value != 62);
    Equal(62, backend.Read().Value);
}

static async Task RetirementAsync()
{
    await using var backend = new FakeControlBackend();
    var session = backend.OpenSession();
    using var subscription = session.Subscribe();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var reader = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
    True(await reader.MoveNextAsync());
    session.Dispose();
    session.Dispose();
    // Completion can drain at most one final buffered snapshot.
    if (await reader.MoveNextAsync()) True(!await reader.MoveNextAsync());
    Throws<ObjectDisposedException>(() => session.WriteValue(75));
    Throws<ObjectDisposedException>(() => session.Subscribe());
    Equal(new ControlDiagnostics(1, 0, 1, 1, 0, 1, 1), backend.Diagnostics);
}

static async Task ConcurrentRetirementAsync()
{
    await using var backend = new FakeControlBackend();
    var sessions = Enumerable.Range(0, 32).Select(_ => backend.OpenSession()).ToArray();
    var subscriptions = sessions.Select(s => s.Subscribe()).ToArray();
    await Task.WhenAll(sessions.Select(session => Task.Run(() =>
    {
        Parallel.Invoke(
            () => { try { session.WriteValue(75); } catch (ObjectDisposedException) { } },
            () => session.Dispose(),
            () => session.Dispose());
    })));
    foreach (var subscription in subscriptions) subscription.Dispose();
    Equal(new ControlDiagnostics(1, 0, 32, 32, 0, 32, 32), backend.Diagnostics);
}

static async Task BackendDisposalAsync()
{
    var backend = new FakeControlBackend();
    var session = backend.OpenSession();
    var subscription = session.Subscribe();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await using var reader = subscription.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
    True(await reader.MoveNextAsync());
    await Task.WhenAll(backend.DisposeAsync().AsTask(), backend.DisposeAsync().AsTask());
    if (await reader.MoveNextAsync()) True(!await reader.MoveNextAsync());
    subscription.Dispose();
    session.Dispose();
    Equal(new ControlDiagnostics(0, 0, 1, 1, 0, 1, 1), backend.Diagnostics);
    Throws<ObjectDisposedException>(() => backend.OpenSession());
    Throws<ObjectDisposedException>(() => backend.Read());
    Throws<ObjectDisposedException>(() => session.WriteValue(75));
}

static async Task LoginAsync()
{
    var login = new FakeLoginService();
    True(await login.AcceptAsync("operator", "demo", CancellationToken.None));
    True(!await login.AcceptAsync("operator", "wrong", CancellationToken.None));
    using var cancellation = new CancellationTokenSource();
    var pending = login.AcceptAsync("operator", "demo", cancellation.Token);
    cancellation.Cancel();
    try { await pending; throw new InvalidOperationException("Expected cancellation."); }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
}

static async Task RootRetirementAsync()
{
    // Static HtmlRenderer runs real Razor/root disposal without a browser or interactive JS.
    // This tests the RCL root's ownership, not actual hybrid document reload/dispatcher cleanup.
    var services = new ServiceCollection().AddLogging().AddLocalControlsUi();
    services.AddSingleton<NavigationManager, TestNavigationManager>();
    services.AddSingleton<INavigationInterception, TestBrowserServices>();
    services.AddSingleton<IScrollToLocationHash, TestBrowserServices>();
    services.AddSingleton<IJSRuntime, NoJavaScriptRuntime>();
    await using var provider = services.BuildServiceProvider();
    var backend = provider.GetRequiredService<FakeControlBackend>();
    for (var index = 0; index < 5; index++)
    {
        var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var root = await renderer.RenderComponentAsync<LocalControlsUi.App>();
                var html = root.ToHtmlString();
                var width = index == 0 ? 100 : 150;
                True(html.Contains($"style=\"width: {width}px\"", StringComparison.Ordinal));
            });
            Equal(1, backend.Diagnostics.ActiveSessions);
            Equal(1, backend.Diagnostics.ActiveSubscriptions);
        }
        finally
        {
            await renderer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Equal(0, backend.Diagnostics.ActiveSessions);
        Equal(0, backend.Diagnostics.ActiveSubscriptions);
        Equal(backend.Diagnostics.CreatedSessions, backend.Diagnostics.RetiredSessions);
        Equal(backend.Diagnostics.CreatedSubscriptions, backend.Diagnostics.RetiredSubscriptions);
        // A later application write must not call a retired root's rendering binding.
        using var writer = backend.OpenSession();
        writer.WriteValue(75);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; actual {actual}.");
}
static void Same(object expected, object actual) => True(ReferenceEquals(expected, actual));
static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Assertion failed.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager() => Initialize("app://localhost/", "app://localhost/");
    protected override void NavigateToCore(string uri, bool forceLoad)
        => throw new InvalidOperationException("This static check does not perform browser navigation.");
}

sealed class TestBrowserServices : INavigationInterception, IScrollToLocationHash
{
    public Task EnableNavigationInterceptionAsync()
        => throw new InvalidOperationException("Static checks must not enable interactive navigation.");
    public Task RefreshScrollPositionForHash(string locationAbsolute)
        => throw new InvalidOperationException("Static checks must not perform browser scrolling.");
}

sealed class NoJavaScriptRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => throw new InvalidOperationException("Static checks must not invoke browser JavaScript.");
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => throw new InvalidOperationException("Static checks must not invoke browser JavaScript.");
}
