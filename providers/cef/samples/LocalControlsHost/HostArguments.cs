namespace LocalControlsHost;

internal sealed record HostArguments(string? PayloadDirectory, string? StateRoot, bool Help) {
    internal static HostArguments Parse(string[] args) {
        string? payload = null, state = null;
        bool help = false;
        for (int i = 0; i < args.Length; i++) {
            string ReadPath() {
                string option = args[i];
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{option} requires a path.");
                return Path.GetFullPath(args[i]);
            }
            switch (args[i]) {
                case "--payload" when payload is null: payload = ReadPath(); break;
                case "--state-root" when state is null: state = ReadPath(); break;
                case "--help": help = true; break;
                default: throw new ArgumentException($"Unknown or repeated argument: {args[i]}");
            }
        }
        return new(payload, state, help);
    }
}
