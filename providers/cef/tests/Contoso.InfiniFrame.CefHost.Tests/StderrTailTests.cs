using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     3. About 8 MB of companion stderr, with lines over 10,000 characters and lines carrying secrets. The expectations
///     follow <see cref="CefStderrTail" />: at most <see cref="CefStderrTail.MaxLines" /> lines; each keeps at most
///     <see cref="CefStderrTail.MaxLineChars" /> characters and a truncated line is marked " [truncated]"; redaction
///     replaces the value of token/secret/password/passwd/pwd/api-key/authorization/cookie/nonce assignments with
///     "[redacted]", "Bearer &lt;token&gt;" with "Bearer [redacted]", a "X:\Users\&lt;name&gt;\" prefix with
///     "%USERPROFILE%\", and runs of 48+ token characters with "[redacted-token]"; control characters become '?'.
/// </summary>
[Category("Fake")]
public sealed class StderrTailTests {
    private const string Marker = "fake stderr flood complete";
    private const string TruncatedSuffix = " [truncated]";

    [Test]
    [Timeout(90_000)]
    public async Task StderrFlood_IsBoundedAndRedacted_AndTheSessionStaysHealthy(CancellationToken cancellationToken) {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        await using HostHarness harness = HostHarness.Create("stderr-flood", "stderr-flood", ["--secret-path", profile]);
        await harness.StartAsync();

        await Wait.UntilAsync(() => harness.Diagnostics.CompanionStderrTail.Any(static line => line.Contains(Marker, StringComparison.Ordinal)),
            "the flood's final marker line in the retained tail", TimeSpan.FromSeconds(45));
        CefHostDiagnostics flooded = harness.Diagnostics;
        FakeLogEntry written = await harness.Fake.WaitForAsync(static entry => entry.IsAction("stderr-flood-written"), "the flood summary");

        // The session must still work after the flood.
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();
        Exception? titleFailure = await Wait.ExceptionAsync(() => window.SetTitleAsync("after the flood", CancellationToken.None).AsTask(), "SetTitleAsync after the flood");
        bool completedEarly = harness.Host.Completion.IsCompleted;
        await HostHarness.CloseWindowAsync(window, sink);
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        IReadOnlyList<string> tail = flooded.CompanionStderrTail;
        string joined = string.Join("\n", tail);
        string[] overlong = [.. tail.Where(static line => Content(line).Length > CefStderrTail.MaxLineChars)];
        string[] truncated = [.. tail.Where(static line => line.EndsWith(TruncatedSuffix, StringComparison.Ordinal))];
        string[] leaks = [.. new[] { "abc123", "hunter2", "xyz" }.Where(secret => joined.Contains(secret, StringComparison.Ordinal))];
        string[] secretLines = [.. tail.Where(static line => line.Contains("token=", StringComparison.Ordinal) || line.Contains("Authorization", StringComparison.Ordinal)
            || line.Contains("password=", StringComparison.Ordinal) || line.Contains("cannot open", StringComparison.Ordinal))];

        using (Assert.Multiple()) {
            await Assert.That(written.Int("bytes") ?? 0).IsGreaterThan(8_000_000);
            await Assert.That(tail.Count).IsEqualTo(CefStderrTail.MaxLines);
            await Assert.That(overlong.Length).IsEqualTo(0).Because($"retained lines longer than {CefStderrTail.MaxLineChars} characters: {overlong.Length}");
            await Assert.That(truncated.Length).IsGreaterThan(0).Because("the trailing >10,000-character lines are retained, truncated");
            await Assert.That(flooded.StderrLinesDropped).IsGreaterThan(0);
            await Assert.That(flooded.StderrLinesTruncated).IsGreaterThan(0);
            await Assert.That(tail.Any(static line => line.Contains('\u0007') || line.Contains('\u001b'))).IsFalse().Because("control characters become '?'");
            // Redaction (CefStderrTail.Redact), asserted on the secret lines the fake wrote last.
            await Assert.That(leaks.Length).IsEqualTo(0).Because($"secret values left in the retained tail: [{string.Join(", ", leaks)}]; secret lines: [{string.Join(" | ", secretLines)}]");
            await Assert.That(joined).Contains("token=[redacted]");
            await Assert.That(joined).Contains("password=[redacted]");
            await Assert.That(joined.Contains(profile, StringComparison.OrdinalIgnoreCase)).IsFalse().Because("the user profile path is redacted");
            await Assert.That(joined).Contains(@"%USERPROFILE%\AppData\Local\Contoso\cache.bin");
            // Health.
            await Assert.That(titleFailure).IsNull();
            await Assert.That(completedEarly).IsFalse();
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0);
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    private static string Content(string line) => line.EndsWith(TruncatedSuffix, StringComparison.Ordinal) ? line[..^TruncatedSuffix.Length] : line;
}
