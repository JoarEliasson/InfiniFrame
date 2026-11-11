// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------
using System.Drawing;
using InfiniFrame;
using InfiniFrame.Hosting;
using InfiniFrame.Window.Hosting;

namespace InfiniTests.InfiniFrame.Hosting;
// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
// Window frame (contract 1.1): chromeless, resizability and size constraints reach the provider only when it declares
// the matching capability, are rejected otherwise, and keep the default engine's size semantics.
[NotInParallel]
public sealed class HostedWindowFrameTests {
    private const InfiniFrameHostCapabilities FrameCapabilities =
        InfiniFrameHostCapabilities.Chromeless | InfiniFrameHostCapabilities.Resizable | InfiniFrameHostCapabilities.SizeConstraints;

    private static ScriptedHostProvider FrameProvider(InfiniFrameHostCapabilities frame = FrameCapabilities) =>
        new(ScriptedHostProvider.AllCapabilities | frame);

    [Test]
    [Timeout(30_000)]
    public async Task DeclaredFrameCapabilities_AreMappedIntoTheRequest_AndReportedByTheGetters(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider());
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAsync(builder => builder
            .SetChromeless(true)
            .SetResizable(false)
            .SetSize(1280, 800)
            .SetMinSize(640, 480)
            .SetMaxSize(1920, 1080));
        await window.WaitForReadyAsync(ct);

        InfiniFrameHostWindowRequest request = host.Request;
        await Assert.That(request.Chromeless).IsTrue();
        await Assert.That(request.Resizable).IsFalse();
        await Assert.That(request.InitialSize).IsEqualTo(new Size(1280, 800));
        await Assert.That(request.MinimumSize).IsEqualTo(new Size(640, 480));
        await Assert.That(request.MaximumSize).IsEqualTo(new Size(1920, 1080));

        await Assert.That(window.IsChromeless).IsTrue();
        await Assert.That(window.IsResizable).IsFalse();
        await Assert.That(window.MinSize).IsEqualTo(new Size(640, 480));
        await Assert.That(window.MaxSize).IsEqualTo(new Size(1920, 1080));
        await Assert.That(window.Size).IsEqualTo(new Size(1280, 800));
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DefaultFrame_SendsTheDefaults_WithOrWithoutFrameCapabilities(bool declareCapabilities, CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider(declareCapabilities ? FrameCapabilities : InfiniFrameHostCapabilities.None));
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAsync(builder => builder.SetSize(800, 600));
        await window.WaitForReadyAsync(ct);

        await Assert.That(host.Request.Chromeless).IsFalse();
        await Assert.That(host.Request.Resizable).IsTrue();
        await Assert.That(host.Request.MinimumSize).IsNull();
        await Assert.That(host.Request.MaximumSize).IsNull();
        await Assert.That(window.IsChromeless).IsFalse();
        await Assert.That(window.IsResizable).IsTrue();
        await Assert.That(window.MinSize).IsEqualTo(Size.Empty);
        await Assert.That(window.MaxSize).IsEqualTo(new Size(int.MaxValue, int.MaxValue));
        window.SetSize(900, 700);
        await Assert.That(window.Size).IsEqualTo(new Size(900, 700));
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(InfiniFrameHostCapabilities.Chromeless, "Chromeless (needs Chromeless)")]
    [Arguments(InfiniFrameHostCapabilities.Resizable, "Resizable (needs Resizable)")]
    [Arguments(InfiniFrameHostCapabilities.SizeConstraints, "MinWidth, MinHeight, MaxWidth, MaxHeight) (needs SizeConstraints)")]
    public async Task EachFrameSetting_IsRejectedWithoutItsCapability(InfiniFrameHostCapabilities missing, string expected, CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider(FrameCapabilities & ~missing));
        NotSupportedException exception = await Expect.ThrowsAsync<NotSupportedException>(() => Task.Run(() => harness.Build(builder => builder
            .SetChromeless(true)
            .SetResizable(false)
            .SetMaxSize(1920, 1080)), ct));

        await Assert.That(exception.Message).Contains(expected);
        await Assert.That(exception.Message.Split("(needs ").Length).IsEqualTo(2);
        await Assert.That(harness.Host.Windows.Count).IsEqualTo(0);
    }

    [Test]
    [Timeout(30_000)]
    public async Task TopmostStaysRejected_EvenWithEveryFrameCapability(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider());
        NotSupportedException exception = await Expect.ThrowsAsync<NotSupportedException>(() => Task.Run(() =>
            harness.Build(builder => builder.SetChromeless(true).SetTopMost(true)), ct));

        await Assert.That(exception.Message).Contains("Topmost");
        await Assert.That(exception.Message).DoesNotContain("Chromeless");
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(800, 600, 640, 480)]
    [Arguments(-1, 0, 640, 480)]
    [Arguments(0, -1, 640, 480)]
    [Arguments(0, 0, 0, 480)]
    [Arguments(0, 0, 640, 0)]
    public async Task InvalidSizeConstraints_AreRejected(int minWidth, int minHeight, int maxWidth, int maxHeight, CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider());
        NotSupportedException exception = await Expect.ThrowsAsync<NotSupportedException>(() => Task.Run(() =>
            harness.Build(builder => builder.SetMinSize(minWidth, minHeight).SetMaxSize(maxWidth, maxHeight)), ct));

        await Assert.That(exception.Message).Contains($"size constraints {minWidth}x{minHeight} to {maxWidth}x{maxHeight}");
        await Assert.That(harness.Host.Windows.Count).IsEqualTo(0);
    }

    [Test]
    [Timeout(30_000)]
    public async Task PartialConstraints_PreserveUnconstrainedDimensions_AndProviderDefaultSize(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider());
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAsync(builder => builder
            .SetMinSize(0, 400)
            .SetMaxSize(1600, int.MaxValue));
        await window.WaitForReadyAsync(ct);

        await Assert.That(host.Request.InitialSize).IsNull();
        await Assert.That(host.Request.MinimumSize).IsEqualTo(new Size(0, 400));
        await Assert.That(host.Request.MaximumSize).IsEqualTo(new Size(1600, int.MaxValue));
        window.SetSize(2000, 100);
        await Assert.That(window.Size).IsEqualTo(new Size(1600, 400));
        window.SetSize(1, 5000);
        await Assert.That(window.Size).IsEqualTo(new Size(1, 5000));
    }

    [Test]
    [Timeout(30_000)]
    public async Task InitialAndProgrammaticSizes_AreClampedIntoTheConstraints_LikeTheDefaultEngine(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider());
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAsync(builder => builder
            .SetSize(2000, 300)
            .SetMinSize(400, 400)
            .SetMaxSize(1600, 0x7FFFFFFF));
        await window.WaitForReadyAsync(ct);

        await Assert.That(host.Request.InitialSize).IsEqualTo(new Size(1600, 400));
        await Assert.That(host.Request.MaximumSize).IsEqualTo(new Size(1600, int.MaxValue));

        window.SetSize(100, 5000);
        await Assert.That(window.Size).IsEqualTo(new Size(400, 5000));
    }

    [Test]
    [Timeout(30_000)]
    public async Task Moving_KeepsTheObservedSize_EvenOutsideTheConstraints(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider());
        // No initial size: the scripted provider picks 800x600, outside the maximum.
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAsync(builder => builder.SetMaxSize(640, 480));
        await window.WaitForReadyAsync(ct);

        await Assert.That(host.Request.InitialSize).IsNull();
        window.SetLocation(5, 6);
        await Assert.That(window.Location).IsEqualTo(new Point(5, 6));
        await Assert.That(window.Size).IsEqualTo(new Size(800, 600));
        window.SetLeft(7);
        window.SetTop(8);
        window.Offset(2, 3);
        await Assert.That(window.Location).IsEqualTo(new Point(9, 11));
        await Assert.That(window.Size).IsEqualTo(new Size(800, 600));
    }

    [Test]
    [Timeout(30_000)]
    public async Task RuntimeFrameChanges_StayUnsupported(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(FrameProvider());
        (InfiniFrameHostedWindow window, _) = await harness.CreateAsync(builder => builder
            .SetChromeless(true).SetResizable(false).SetMinSize(400, 300).SetMaxSize(1000, 800));
        await window.WaitForReadyAsync(ct);

        window.SetSize(1200, 200);
        await Assert.That(window.Size).IsEqualTo(new Size(1000, 300));
        window.SetMaximized();
        await Assert.That(window.IsMaximized).IsTrue();
        await Assert.That(() => window.SetResizable(true)).Throws<NotSupportedException>();
        await Assert.That(() => window.SetMinSize(1, 1)).Throws<NotSupportedException>();
        await Assert.That(() => window.SetMaxSize(1, 1)).Throws<NotSupportedException>();
        await Assert.That(() => window.SetTopMost(true)).Throws<NotSupportedException>();
        await Assert.That(window.LifecycleState).IsEqualTo(InfiniFrameWindowLifecycleState.Ready);
    }
}
