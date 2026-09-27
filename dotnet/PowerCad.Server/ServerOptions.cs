namespace PowerCad.Server;

/// <summary>CLI flags override environment variables (POWER_CAD_SIMULATE, POWER_CAD_TARGET, POWER_CAD_READ_ONLY, POWER_CAD_HOME).</summary>
public sealed record ServerOptions(bool Simulate, string? Target, bool ReadOnly, string? Home, bool ShowHelp, bool ShowVersion)
{
    public const string Usage = """
        power-cad-server [--simulate] [--target <autocad-2027-PID|PID>] [--read-only] [--home <dir>]

          --simulate    use the built-in sample drawing instead of AutoCAD (try the tools anywhere)
          --target      pin one AutoCAD session when several are running
          --read-only   hide nothing, but refuse every edit
          --home        discovery directory (default %LOCALAPPDATA%\PowerCad)
        """;

    public const string Instructions = """
        Power CAD controls a live AutoCAD 2027 drawing through an in-process plugin.
        Operating procedure: (1) cad_status. (2) Find targets with narrow cad_query filters and note each
        handle + fingerprint. (3) For edits pass expect_fingerprint (and expect_text for text) so stale
        analyses are refused. (4) Preview risky or multi-entity edits with dry_run=true and show the diff.
        (5) Apply; every edit is verified on the touched entities and rolled back on any mismatch.
        (6) Report the returned before/after diff. Use cad_batch for multi-step edits that must succeed
        together. Never unlock layers or widen max_changes without asking the user.
        """;

    public static ServerOptions Parse(string[] args, Func<string, string?> env)
    {
        static bool Flag(string? v) => v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

        var simulate = Flag(env("POWER_CAD_SIMULATE"));
        var readOnly = Flag(env("POWER_CAD_READ_ONLY"));
        var target = env("POWER_CAD_TARGET");
        var home = env("POWER_CAD_HOME");
        bool help = false, version = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--simulate":
                    simulate = true;
                    break;
                case "--read-only":
                    readOnly = true;
                    break;
                case "--target" when i + 1 < args.Length:
                    target = args[++i];
                    break;
                case "--home" when i + 1 < args.Length:
                    home = args[++i];
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                case "--version":
                    version = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.\n{Usage}");
            }
        }

        return new ServerOptions(simulate, string.IsNullOrWhiteSpace(target) ? null : target, readOnly, home, help, version);
    }
}
