using System.Drawing;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     Host contract 1.1 window style: the provider declares Chromeless, Resizable and SizeConstraints, and Create carries
///     the requested frame, user resizing and size limits to the companion (protocol 3).
/// </summary>
[Category("Fake")]
public sealed class WindowStyleTests {
    [Test]
    public async Task TheProvider_DeclaresTheWindowStyleCapabilities_AtContract11() {
        var provider = new CefHostProvider(new CefHostOptions { StateRoot = Path.GetTempPath() });

        using (Assert.Multiple()) {
            await Assert.That(provider.ContractVersion).IsEqualTo(new Version(1, 1));
            await Assert.That(provider.Capabilities.HasFlag(InfiniFrameHostCapabilities.Chromeless)).IsTrue();
            await Assert.That(provider.Capabilities.HasFlag(InfiniFrameHostCapabilities.Resizable)).IsTrue();
            await Assert.That(provider.Capabilities.HasFlag(InfiniFrameHostCapabilities.SizeConstraints)).IsTrue();
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task AChromelessFixedSizeWindow_SendsItsStyleAndLimitsInCreate(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("wstyle", "normal");
        await harness.StartAsync();
        var sink = new RecordingSink();
        IInfiniFrameWindowHost window = await Wait.WithinAsync(harness.Host.CreateWindowAsync(
            Requests.Window(chromeless: true, resizable: false, minimumSize: new Size(800, 480),
                maximumSize: new Size(int.MaxValue, 720), size: new Size(1024, 600)), sink, CancellationToken.None).AsTask(), "create");
        FakeLogEntry create = harness.Fake.Received("create").Single();
        await HostHarness.CloseWindowAsync((CefWindowHost)window, sink);
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();

        using (Assert.Multiple()) {
            await Assert.That(create.Int("window_flags")).IsEqualTo(CefWindowStyle.FramelessFlag | CefWindowStyle.FixedSizeFlag);
            await Assert.That(create.Int("width")).IsEqualTo(1024);
            await Assert.That(create.Int("height")).IsEqualTo(600);
            await Assert.That(create.Int("min_width")).IsEqualTo(800);
            await Assert.That(create.Int("min_height")).IsEqualTo(480);
            await Assert.That(create.Int("max_width")).IsEqualTo(0).Because("an int.MaxValue maximum is unconstrained");
            await Assert.That(create.Int("max_height")).IsEqualTo(720);
        }
    }

    [Test]
    public async Task TheStyleMapping_KeepsOuterBoundsAndUnconstrainedDimensions() {
        CefWindowStyle style = CefWindowStyle.From(Requests.Window(chromeless: true, resizable: true,
            minimumSize: new Size(0, 300), maximumSize: new Size(20000, int.MaxValue)));
        CefWindowStyle framed = CefWindowStyle.From(Requests.Window());

        using (Assert.Multiple()) {
            await Assert.That(style.Flags).IsEqualTo(CefWindowStyle.FramelessFlag);
            await Assert.That(style.ContentBounds).IsFalse().Because("contract 1.1 sizes are outer sizes");
            await Assert.That(style.Minimum).IsEqualTo(new Size(0, 300));
            await Assert.That(style.Maximum).IsEqualTo(Size.Empty);
            await Assert.That(framed).IsEqualTo(CefWindowStyle.Default);
        }
    }

    [Test]
    public async Task TheProviderDefaultSize_IsClampedIntoTheLimits() {
        CefHost.Validate(Requests.Window(minimumSize: new Size(1280, 800), providerDefaultSize: true), out Rectangle grown);
        List<string> shrunk = CefHost.Validate(Requests.Window(maximumSize: new Size(800, 600), providerDefaultSize: true), out Rectangle small);

        using (Assert.Multiple()) {
            await Assert.That(grown.Size).IsEqualTo(new Size(1280, 800));
            await Assert.That(small.Size).IsEqualTo(new Size(800, 600));
            await Assert.That(shrunk.Count).IsEqualTo(0).Because(string.Join("; ", shrunk));
        }
    }
    [Test]
    public async Task UnrepresentableLimits_AreRejectedBeforeCreation() {
        List<string> tooLarge = CefHost.Validate(Requests.Window(minimumSize: new Size(20000, 100)), out _);
        List<string> outside = CefHost.Validate(Requests.Window(maximumSize: new Size(800, 600), size: new Size(900, 700)), out _);
        List<string> valid = CefHost.Validate(Requests.Window(chromeless: true, resizable: false, minimumSize: new Size(640, 480),
            maximumSize: new Size(1920, 1080), size: new Size(1024, 600)), out _);

        using (Assert.Multiple()) {
            await Assert.That(tooLarge.Any(static violation => violation.StartsWith("minimum size 20000x100", StringComparison.Ordinal))).IsTrue();
            await Assert.That(outside.Any(static violation => violation.Contains("outside its size constraints", StringComparison.Ordinal))).IsTrue();
            await Assert.That(valid.Count).IsEqualTo(0).Because(string.Join("; ", valid));
        }
    }
}
