using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     The manifest check that guards an installed InfiniFrame CEF runtime (or an externally supplied app-local runtime): a runtime built
///     for another protocol, RID, CEF build or API is refused with every mismatch named, before any native code runs.
///     Synthetic payloads only; nothing is launched.
/// </summary>
[Category("Fake")]
public sealed class PayloadCompatibilityTests {
    [Test]
    public async Task ACompatibleVersionedRuntimeFolder_IsAccepted() {
        using var directory = new TestDirectory("pay");
        string runtime = SyntheticRuntime.Create(Path.Combine(directory.Root, "156.0.8-b1"));

        await Assert.That(CefPayload.Validate(runtime)).IsEqualTo(Path.GetFullPath(runtime));
    }

    [Test]
    [Arguments("protocol", 2, "protocol 2 (this provider requires protocol 3)")]
    [Arguments("apiVersion", 15500, "CEF API 15500 (this provider requires stable API 15600)")]
    public async Task AnIncompatibleNumber_IsRefusedAndNamed(string property, int value, string expected) {
        using var directory = new TestDirectory("pay");
        string runtime = SyntheticRuntime.Create(Path.Combine(directory.Root, "old"), manifest => manifest[property] = value);

        InvalidDataException? error = Refusal(runtime);
        using (Assert.Multiple()) {
            await Assert.That(error?.Message ?? "").Contains(expected);
            await Assert.That(error?.Message ?? "").Contains("InfiniFrame CEF runtime 156.0.8-test");
            await Assert.That(error?.Message ?? "").Contains("CefHostOptions.PayloadDirectory");
        }
    }

    [Test]
    public async Task AnotherCefBuildOrRid_IsRefusedAndNamed() {
        using var directory = new TestDirectory("pay");
        string runtime = SyntheticRuntime.Create(Path.Combine(directory.Root, "other"), manifest => {
            manifest["cefVersion"] = "157.0.1+gabcdef0+chromium-157.0.1.1";
            manifest["rid"] = "linux-riscv64";
        });

        InvalidDataException? error = Refusal(runtime);
        using (Assert.Multiple()) {
            await Assert.That(error?.Message ?? "").Contains("CEF 157.0.1+gabcdef0+chromium-157.0.1.1");
            await Assert.That(error?.Message ?? "").Contains("RID linux-riscv64");
        }
    }

    [Test]
    public async Task ADisabledSandbox_IsRefused() {
        using var directory = new TestDirectory("pay");
        string runtime = SyntheticRuntime.Create(Path.Combine(directory.Root, "nosandbox"), manifest => manifest["sandbox"] = false);

        InvalidDataException? error = Refusal(runtime);
        await Assert.That(error?.Message ?? "").Contains("sandbox");
    }

    [Test]
    public async Task AModifiedFile_IsRefused() {
        using var directory = new TestDirectory("pay");
        string runtime = SyntheticRuntime.Create(Path.Combine(directory.Root, "modified"));
        await File.AppendAllTextAsync(Path.Combine(runtime, "resources.pak"), "x");

        InvalidDataException? error = Refusal(runtime);
        await Assert.That(error?.Message ?? "").Contains("resources.pak");
    }

    [Test]
    public async Task AnUnlistedFile_IsRefused() {
        using var directory = new TestDirectory("pay");
        string runtime = SyntheticRuntime.Create(Path.Combine(directory.Root, "extra"));
        await File.WriteAllTextAsync(Path.Combine(runtime, "injected.dll"), "x");

        InvalidDataException? error = Refusal(runtime);
        await Assert.That(error?.Message ?? "").Contains("unlisted file: injected.dll");
    }

    [Test]
    public async Task SideBySideVersions_AreSelectedByPath() {
        using var directory = new TestDirectory("pay");
        string previous = SyntheticRuntime.Create(Path.Combine(directory.Root, "156.0.7-b1"), manifest => manifest["protocol"] = 2);
        string current = SyntheticRuntime.Create(Path.Combine(directory.Root, "156.0.8-b1"));

        using (Assert.Multiple()) {
            await Assert.That(Refusal(previous)).IsNotNull();
            await Assert.That(CefPayload.Validate(current)).IsEqualTo(Path.GetFullPath(current));
        }
    }

    [Test]
    [Arguments("missing")]
    [Arguments("protocol")]
    [Arguments("hash")]
    public async Task InvalidSelectedRuntime_IsRejectedByPublicHostStart_BeforeStateOrLaunch(string failure) {
        using var directory = new TestDirectory("sel");
        string selected = Path.Combine(directory.Root, "selected-runtime");
        if (failure != "missing") {
            SyntheticRuntime.Create(selected, failure == "protocol" ? manifest => manifest["protocol"] = 2 : null);
            if (failure == "hash") await File.AppendAllTextAsync(Path.Combine(selected, "resources.pak"), "changed");
        }
        var provider = new CefHostProvider(new CefHostOptions {
            PayloadDirectory = selected, StateRoot = directory.StateRoot
        });
        await using IInfiniFrameHost host = provider.CreateHost(new InfiniFrameHostCreateContext { ApplicationId = Guid.NewGuid() });
        Exception? error = null;
        try {
            await host.StartAsync(new InfiniFrameHostStartContext {
                Services = EmptyServices.Instance, LoggerFactory = NullLoggerFactory.Instance
            }, CancellationToken.None);
        }
        catch (Exception exception) { error = exception; }

        using (Assert.Multiple()) {
            await Assert.That(failure == "missing" ? error is FileNotFoundException : error is InvalidDataException).IsTrue();
            await Assert.That(error?.Message ?? "").Contains(failure switch {
                "missing" => "CefHostOptions.PayloadDirectory", "protocol" => "protocol 2", _ => "resources.pak"
            });
            await Assert.That(Directory.Exists(directory.StateRoot)).IsFalse();
            await Assert.That(provider.GetDiagnostics()!.CompanionProcessId).IsEqualTo(0);
        }
    }

    private static InvalidDataException? Refusal(string runtime) {
        try {
            CefPayload.Validate(runtime);
            return null;
        }
        catch (InvalidDataException exception) {
            return exception;
        }
    }
}

/// <summary>A minimal file set with a valid manifest and hash inventory, for manifest tests only (no runnable code).</summary>
internal static class SyntheticRuntime {
    internal static string Create(string root, Action<JsonObject>? edit = null) {
        Directory.CreateDirectory(root);
        string[] files = OperatingSystem.IsWindows()
            ? ["InfiniFrame.Cef.Host.exe", "InfiniFrame.Cef.Host.dll", "libcef.dll", "chrome_elf.dll"]
            : ["InfiniFrame.Cef.Host", "libcef.so"];
        files = [.. files, "icudtl.dat", "resources.pak", "chrome_100_percent.pak", "chrome_200_percent.pak",
            "v8_context_snapshot.bin", "locales/en-US.pak", "LICENSE.txt", "CREDITS.html"];
        var inventory = new JsonArray();
        foreach (string file in files) {
            string path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"synthetic {file}");
            byte[] bytes = File.ReadAllBytes(path);
            inventory.Add(new JsonObject {
                ["path"] = file, ["bytes"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            });
        }
        var manifest = new JsonObject {
            ["protocol"] = (int)CefProtocol.Version,
            ["rid"] = CefHostOptions.CurrentRid,
            ["cefVersion"] = CefHostOptions.CefVersion,
            ["apiVersion"] = CefHostOptions.ApiVersion,
            ["runtimeVersion"] = "156.0.8-test",
            ["sandbox"] = true,
            ["files"] = inventory
        };
        edit?.Invoke(manifest);
        File.WriteAllText(Path.Combine(root, "cef-backend.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return root;
    }
}
