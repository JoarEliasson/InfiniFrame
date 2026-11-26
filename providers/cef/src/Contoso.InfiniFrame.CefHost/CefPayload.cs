using System.Security.Cryptography;
using System.Text.Json;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     Validates the installed companion payload before launching native code: protocol, RID, CEF version, stable API,
///     sandbox flag and a bounded file/hash inventory. This detects incomplete or mixed deployments; it is not a
///     signature or a trust boundary against someone who can replace both the payload and its manifest.
/// </summary>
internal static class CefPayload {
    internal static string Validate(string payloadDirectory) {
        string root = Path.GetFullPath(payloadDirectory);
        string manifestPath = Path.Combine(root, "cef-backend.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("The CEF runtime manifest is missing. Select the complete installed runtime directory with CefHostOptions.PayloadDirectory; the managed provider package contains no native runtime.", manifestPath);
        if (new FileInfo(manifestPath).Length > 1024 * 1024)
            throw new InvalidDataException("The CEF payload manifest exceeds the 1 MiB limit.");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement value = manifest.RootElement;
        ValidateIdentity(value, root);
        string[] required = OperatingSystem.IsWindows()
            ? ["InfiniFrame.Cef.Host.exe", "InfiniFrame.Cef.Host.dll", "libcef.dll", "chrome_elf.dll"]
            : ["InfiniFrame.Cef.Host", "libcef.so"];
        required = [.. required, "icudtl.dat", "resources.pak", "chrome_100_percent.pak", "chrome_200_percent.pak",
            "v8_context_snapshot.bin", "locales/en-US.pak", "LICENSE.txt", "CREDITS.html"];
        foreach (string file in required) {
            string path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new FileNotFoundException($"The CEF payload is incomplete: {file}. No default-engine fallback is performed.", path);
        }
        ValidateInventory(root, value, required);
        return root;
    }

    /// <summary>
    ///     Refuses a payload (a separately installed runtime or externally deployed app-local runtime) built for another protocol,
    ///     RID, CEF build or API version, naming every mismatch and the runtime version found, so a side-by-side runtime
    ///     root can be pointed at the compatible version instead.
    /// </summary>
    private static void ValidateIdentity(JsonElement value, string root) {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The CEF payload manifest is not a JSON object.");
        var mismatches = new List<string>();
        int? protocol = Integer(value, "protocol");
        if (protocol != CefProtocol.Version)
            mismatches.Add($"protocol {Describe(protocol)} (this provider requires protocol {CefProtocol.Version})");
        string? rid = Text(value, "rid");
        if (rid != CefHostOptions.CurrentRid) mismatches.Add($"RID {rid ?? "missing"} (this process requires {CefHostOptions.CurrentRid})");
        string? cef = Text(value, "cefVersion");
        if (cef != CefHostOptions.CefVersion) mismatches.Add($"CEF {cef ?? "missing"} (this provider requires CEF {CefHostOptions.CefVersion})");
        int? api = Integer(value, "apiVersion");
        if (api != CefHostOptions.ApiVersion) mismatches.Add($"CEF API {Describe(api)} (this provider requires stable API {CefHostOptions.ApiVersion})");
        if (!value.TryGetProperty("sandbox", out JsonElement sandbox) || sandbox.ValueKind != JsonValueKind.True)
            mismatches.Add("sandbox disabled or missing (sandbox=true is required)");
        if (mismatches.Count == 0) return;
        string runtime = Text(value, "runtimeVersion") is {} version ? $"InfiniFrame CEF runtime {version}" : "CEF payload";
        throw new InvalidDataException(
            $"The {runtime} at '{root}' is incompatible with this provider: {string.Join("; ", mismatches)}. "
            + "Install or select a compatible runtime version (CefHostOptions.PayloadDirectory); no default-engine fallback is performed.");
    }

    private static int? Integer(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out int number) ? number : null;

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static string Describe(int? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing";

    private static void ValidateInventory(string root, JsonElement manifest, string[] required) {
        if (!manifest.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() is < 1 or > 4096)
            throw new InvalidDataException("The CEF payload requires a bounded file/hash inventory.");
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (JsonElement file in files.EnumerateArray()) {
            string? relative = file.GetProperty("path").GetString();
            if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':')
                || relative.Split('/').Any(static segment => segment is "" or "." or "..")
                || !seen.Add(relative) || relative.Equals("cef-backend.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The CEF payload inventory contains an unsafe or duplicate path.");
            string path = root;
            foreach (string segment in relative.Split('/')) {
                path = Path.Combine(path, segment);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The CEF payload inventory must not traverse symbolic links or reparse points.");
            }
            long bytes = file.GetProperty("bytes").GetInt64();
            string? expected = file.GetProperty("sha256").GetString();
            if (bytes < 0 || new FileInfo(path).Length != bytes || expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new InvalidDataException($"CEF payload file metadata mismatch: {relative}.");
            using FileStream stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"CEF payload checksum mismatch: {relative}.");
        }
        foreach (string file in required)
            if (!seen.Contains(file)) throw new InvalidDataException($"A required CEF payload file is absent from its inventory: {file}.");
        var directories = new Stack<string>();
        directories.Push(root);
        int entries = 0;
        while (directories.TryPop(out string? directory)) {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)) {
                if (++entries > 8192) throw new InvalidDataException("The CEF payload exceeds the inventory entry limit.");
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The CEF payload must not contain symbolic links or reparse points.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                else {
                    string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                    if (relative != "cef-backend.json" && !seen.Contains(relative))
                        throw new InvalidDataException($"The CEF payload contains an unlisted file: {relative}.");
                }
            }
        }
    }
}
