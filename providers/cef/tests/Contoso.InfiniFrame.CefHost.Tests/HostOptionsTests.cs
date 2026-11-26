using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     Protocol 3 host options: the style policy and panel rendering options travel in Hello, the companion confirms
///     them, and the defaults keep the strict policy and the platform's rendering.
/// </summary>
[Category("Fake")]
public sealed class HostOptionsTests {
    [Test]
    [Timeout(60_000)]
    public async Task Defaults_SendNoHostOptions_AndTheDefaultWindowStyle(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("hopt", "normal");
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();
        FakeLogEntry hello = harness.Fake.Received("hello").Single();
        FakeLogEntry create = harness.Fake.Received("create").Single();
        await HostHarness.CloseWindowAsync(window, sink);
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();

        using (Assert.Multiple()) {
            await Assert.That(hello.Int("host_options")).IsEqualTo(0);
            await Assert.That(hello.Json.GetProperty("device_scale_factor").GetDouble()).IsEqualTo(0d);
            await Assert.That(create.Int("window_flags")).IsEqualTo(0);
            await Assert.That(create.Int("min_width")).IsEqualTo(0);
            await Assert.That(create.Int("min_height")).IsEqualTo(0);
            await Assert.That(create.Int("max_width")).IsEqualTo(0);
            await Assert.That(create.Int("max_height")).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task PanelOptions_AreSentInHello_AndConfirmed(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("hopt", "normal", configure: static options => {
            options.AllowInlineStyleAttributes = true;
            options.DeviceScaleFactor = 1;
            options.DisablePinchZoom = true;
            options.DisableOverscroll = true;
        });
        await harness.StartAsync();
        FakeLogEntry hello = harness.Fake.Received("hello").Single();
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();

        using (Assert.Multiple()) {
            await Assert.That(hello.Int("host_options")).IsEqualTo(7);
            await Assert.That(hello.Json.GetProperty("device_scale_factor").GetDouble()).IsEqualTo(1d);
            await Assert.That(harness.Diagnostics.SessionFaults.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task OnlyTheStylePolicy_SetsOnlyItsFlag(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("hopt", "normal", configure: static options => options.AllowInlineStyleAttributes = true);
        await harness.StartAsync();
        FakeLogEntry hello = harness.Fake.Received("hello").Single();
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();

        await Assert.That(hello.Int("host_options")).IsEqualTo(1);
    }

    [Test]
    [Timeout(60_000)]
    public async Task AnUnconfirmedHostOption_FailsTheHandshake(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("hopt", "normal", ["--echo-host-options", "0"],
            static options => options.AllowInlineStyleAttributes = true);
        Exception? start = await Wait.ExceptionAsync(harness.StartAsync, "host.StartAsync to fail");
        await harness.DisposeHostAsync();

        using (Assert.Multiple()) {
            await Assert.That(start is InvalidDataException).IsTrue().Because($"StartAsync threw {start?.GetType().Name}: {start?.Message}");
            await Assert.That(start?.Message ?? "").Contains("did not confirm the requested host options");
        }
    }

    [Test]
    [Arguments(0.25)]
    [Arguments(4.5)]
    [Arguments(double.NaN)]
    [Arguments(double.PositiveInfinity)]
    public async Task DeviceScaleFactor_OutsideItsRange_IsRejected(double scale) {
        var options = new CefHostOptions { StateRoot = Path.GetTempPath(), DeviceScaleFactor = scale };
        Exception? error = null;
        try {
            options.Snapshot();
        }
        catch (Exception exception) {
            error = exception;
        }
        await Assert.That(error is ArgumentOutOfRangeException).IsTrue().Because($"Snapshot threw {error?.GetType().Name}");
    }

    [Test]
    public async Task Snapshot_CopiesTheHostOptions() {
        CefHostOptions snapshot = new CefHostOptions {
            StateRoot = Path.GetTempPath(), AllowInlineStyleAttributes = true, DeviceScaleFactor = 1.5, DisablePinchZoom = true,
            DisableOverscroll = true
        }.Snapshot();

        using (Assert.Multiple()) {
            await Assert.That(snapshot.AllowInlineStyleAttributes).IsTrue();
            await Assert.That(snapshot.DeviceScaleFactor).IsEqualTo(1.5);
            await Assert.That(snapshot.DisablePinchZoom).IsTrue();
            await Assert.That(snapshot.DisableOverscroll).IsTrue();
            await Assert.That(snapshot.HostOptionFlags).IsEqualTo(7u);
            await Assert.That(new CefHostOptions().HostOptionFlags).IsEqualTo(0u);
        }
    }
}
