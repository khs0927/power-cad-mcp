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
        Operating procedure: (1) cad_status, then cad_inspect / cad_layers to learn the drawing's own
        layers, text styles, dimension styles, linetypes and blocks - reuse them instead of inventing new ones.
        (2) Find targets with narrow cad_query filters (group_by for an overview) and note each handle +
        fingerprint. (3) For edits pass expect_fingerprint (and expect_text for text) so stale analyses are
        refused. (4) Preview risky or multi-entity edits with dry_run=true and show the diff. (5) Apply; every
        edit is verified on the touched entities and rolled back on any mismatch. (6) Check the result with
        cad_snapshot (a PNG of the model view) and report the returned before/after diff.
        Tools: cad_create (line, polyline, circle, arc, text, mtext, insert, point, dimension, hatch; color/
        linetype/lineweight/justify/style), cad_set_properties, cad_copy, cad_transform (rotate/scale/mirror),
        cad_move, cad_delete, cad_replace_text, cad_modify_opening, cad_set_layer, cad_zoom, cad_snapshot.
        Use cad_batch for multi-step edits that must succeed together. Never unlock layers, delete the user's
        existing entities or widen max_changes without asking the user.
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
