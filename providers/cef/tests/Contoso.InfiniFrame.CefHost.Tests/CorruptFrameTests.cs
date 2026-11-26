using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>5. A corrupt frame from the companion faults the whole session as invalid data.</summary>
[Category("Fake")]
public sealed class CorruptFrameTests {
    [Test]
    [Timeout(60_000)]
    [Arguments("corrupt-size-small", "Invalid CEF frame size.")]
    [Arguments("corrupt-size-large", "Invalid CEF frame size.")]
    [Arguments("corrupt-kind", "Unknown CEF frame kind 999.")]
    [Arguments("corrupt-version", "CEF protocol version mismatch: the companion sent protocol 4; this provider implements protocol 3.")]
    public async Task CorruptFrame_FaultsTheSessionAsInvalidData(string variant, string expectedMessage, CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("corrupt", variant);
        await harness.StartAsync();

        Exception? completion = await Wait.ExceptionAsync(() => harness.Host.Completion, "host.Completion to fault");
        CefHostDiagnostics faulted = harness.Diagnostics;
        // Callers are refused from then on, with the protocol violation as the cause.
        Exception? later = await Wait.ExceptionAsync(
            () => harness.Host.CreateWindowAsync(Requests.Window(), new RecordingSink(), CancellationToken.None).AsTask(), "a create after the fault");
        await harness.ShutdownAsync();
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            await Assert.That(faulted.SessionFaults.Count).IsEqualTo(1);
            await Assert.That(faulted.SessionFaults.FirstOrDefault()).IsEqualTo($"InvalidDataException: {expectedMessage}");
            await Assert.That(completion is InfiniFrameHostOperationException).IsTrue().Because($"Completion: {completion?.GetType().Name}: {completion?.Message}");
            await Assert.That(completion?.InnerException is InvalidDataException).IsTrue();
            await Assert.That(later is InfiniFrameHostOperationException).IsTrue().Because($"later create threw {later?.GetType().Name}: {later?.Message}");
            await Assert.That(later?.InnerException is InvalidDataException).IsTrue();
            await Assert.That(final.ExitEscalated).IsFalse().Because("the fake still exits on EOF");
            await Assert.That(disposal).IsLessThan(TimeSpan.FromSeconds(2) + harness.Options.ExitTimeout + TimeSpan.FromSeconds(3));
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }
}
