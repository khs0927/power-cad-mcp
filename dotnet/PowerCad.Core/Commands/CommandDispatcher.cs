using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PowerCad.Core.Harness;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

/// <summary>
/// Executes protocol commands against an <see cref="ICadDocument"/>. The same dispatcher runs inside the
/// AutoCAD plugin (real document) and in the simulator/tests (in-memory document).
/// Read commands never open a write transaction; every mutating command goes through
/// <see cref="ChangeSession"/> and supports <c>dry_run</c> (execute, verify, report, then roll back).
/// </summary>
public sealed class CommandDispatcher(ICadDocument document, DispatcherOptions? options = null)
{
    public const int MaxBatch = 20;
    public const int MaxResults = 1000;

    private readonly DispatcherOptions _options = options ?? new DispatcherOptions();

    public static readonly IReadOnlyList<string> Commands =
        ["status", "query", "get", "replace_text", "move", "modify_opening", "create", "batch"];

    private static readonly HashSet<string> Mutating = ["replace_text", "move", "modify_opening", "create", "batch"];

    public JsonNode Execute(string command, JsonObject? parameters)
    {
        var p = new Params(parameters);
        if (Mutating.Contains(command) && _options.ReadOnly)
        {
            throw new CadException(ErrorCodes.ReadOnly, "The server runs in read-only mode.", "Restart without --read-only to allow edits.");
        }

        return command switch
        {
            "status" => Status(),
            "query" => document.Execute(tx => Query(tx, p), commit: false),
            "get" => document.Execute(tx => Get(tx, p), commit: false),
            "replace_text" or "move" or "modify_opening" or "create" => RunChange(p, (s, q) => Apply(command, s, q)),
            "batch" => RunChange(p, Batch),
            _ => throw new CadException(ErrorCodes.UnknownCommand, $"Unknown command '{command}'.", $"Known: {string.Join(", ", Commands)}."),
        };
    }

    private JsonObject Status()
    {
        var info = document.Describe();
        info["commands"] = new JsonArray(Commands.Select(c => (JsonNode)c).ToArray());
        info["read_only"] = _options.ReadOnly;
        info["protocol_version"] = Protocol.Version;
        return info;
    }

    private JsonObject RunChange(Params p, Action<ChangeSession, Params> apply)
    {
        var dryRun = p.Bool("dry_run");
        p.Node.Remove("dry_run");
        var report = document.Execute(
            tx =>
            {
                var session = new ChangeSession(tx);
                apply(session, p);
                return session.VerifyAndReport();
            },
            commit: !dryRun);
        report["dry_run"] = dryRun;
        report["committed"] = !dryRun;
        return report;
    }

    private void Apply(string command, ChangeSession s, Params p)
    {
        switch (command)
        {
            case "replace_text":
                ReplaceText(s, p);
                break;
            case "move":
                Move(s, p);
                break;
            case "modify_opening":
                ModifyOpening(s, p);
                break;
            case "create":
                Create(s, p);
                break;
            default:
                throw new CadException(ErrorCodes.UnknownCommand, $"'{command}' cannot run inside a batch.");
        }
    }

    // ------------------------------------------------------------------ reads
    private JsonObject Query(ICadTransaction tx, Params p)
    {
        p.AllowOnly("types", "layers", "handles", "text_contains", "text_regex", "block_name", "within", "max_results");
        var types = p.Strings("types").Select(NormalizeType).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layers = p.Strings("layers").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var handles = p.Strings("handles").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contains = p.OptString("text_contains");
        var regex = p.OptString("text_regex") is { } rx ? MakeRegex(rx, ignoreCase: true) : null;
        var block = p.OptString("block_name") is { } bn ? MakeWildcard(bn) : null;
        var within = ParseWindow(p.Node["within"]);
        var max = p.Int("max_results", 100, 1, MaxResults);

        var matched = new JsonArray();
        var total = 0;
        foreach (var e in tx.ScanModelSpace())
        {
            if ((types.Count > 0 && !types.Contains(e.Type))
                || (layers.Count > 0 && !layers.Contains(e.Layer))
                || (handles.Count > 0 && !handles.Contains(e.Handle))
                || (contains is not null && !(e.Text ?? AttributeText(e)).Contains(contains, StringComparison.OrdinalIgnoreCase))
                || (regex is not null && !regex.IsMatch(e.Text ?? AttributeText(e)))
                || (block is not null && !(e.Type == EntityTypes.Insert && block.IsMatch(e.Props["name"]?.GetValue<string>() ?? "")))
                || (within is { } w && !(e.Anchor is { } a && a.X >= w.Min.X && a.X <= w.Max.X && a.Y >= w.Min.Y && a.Y <= w.Max.Y)))
            {
                continue;
            }

            total++;
            if (matched.Count < max)
            {
                matched.Add(e.ToJson());
            }
        }

        return new JsonObject
        {
            ["total"] = total,
            ["returned"] = matched.Count,
            ["truncated"] = total > matched.Count,
            ["entities"] = matched,
        };
    }

    private static string AttributeText(EntityState e) =>
        e.Props["attributes"] is JsonObject attrs ? string.Join("\n", attrs.Select(a => a.Value?.ToString())) : "";

    private static JsonObject Get(ICadTransaction tx, Params p)
    {
        p.AllowOnly("handles");
        var found = new JsonArray();
        var missing = new JsonArray();
        foreach (var h in p.Strings("handles", required: true).Take(MaxResults))
        {
            try
            {
                found.Add(tx.Read(h).ToJson());
            }
            catch (CadException e) when (e.Code == ErrorCodes.NotFound)
            {
                missing.Add(h);
            }
        }

        return new JsonObject { ["entities"] = found, ["missing"] = missing };
    }

    // ------------------------------------------------------------ text change
    private static void ReplaceText(ChangeSession s, Params p)
    {
        p.AllowOnly("targets", "new_text", "find", "replace", "regex", "case_sensitive", "layers", "max_changes");
        if (p.Has("targets"))
        {
            // Mode A: explicit targets, each optionally pinned to the text/fingerprint the agent saw.
            var newText = p.String("new_text");
            foreach (var t in p.Objects("targets", MaxResults))
            {
                var tp = new Params(t);
                tp.AllowOnly("handle", "expect_text", "expect_fingerprint");
                var state = s.Capture(tp.String("handle"), tp.OptString("expect_fingerprint"));
                RequireTextLike(state);
                if (tp.OptString("expect_text") is { } expected && expected != state.Text)
                {
                    throw new CadException(
                        ErrorCodes.StaleTarget,
                        $"Entity {state.Handle} contains '{state.Text}', not the expected '{expected}'.",
                        "Re-query the text before replacing it.");
                }

                SetAndExpect(s, state.Handle, newText);
            }

            return;
        }

        // Mode B: find/replace across TEXT/MTEXT, bounded by max_changes.
        var find = p.String("find");
        var replace = p.OptString("replace") ?? "";
        var caseSensitive = p.Bool("case_sensitive", true);
        var useRegex = p.Bool("regex");
        var layers = p.Strings("layers").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxChanges = p.Int("max_changes", 50, 1, MaxResults);
        var pattern = useRegex ? MakeRegex(find, !caseSensitive) : MakeRegex(Regex.Escape(find), !caseSensitive);

        var planned = new List<(EntityState State, string NewText)>();
        foreach (var e in s.Tx.ScanModelSpace())
        {
            if (!EntityTypes.TextLike.Contains(e.Type) || (layers.Count > 0 && !layers.Contains(e.Layer)))
            {
                continue;
            }

            var text = e.Text ?? "";
            string updated;
            try
            {
                updated = pattern.Replace(text, useRegex ? replace : replace.Replace("$", "$$"));
            }
            catch (RegexMatchTimeoutException)
            {
                throw CadException.Invalid("The regular expression is too slow.", "Simplify the pattern.");
            }

            if (updated != text)
            {
                planned.Add((e, updated));
            }
        }

        if (planned.Count > maxChanges)
        {
            throw new CadException(
                ErrorCodes.TooManyMatches,
                $"'{find}' matches {planned.Count} text entities, more than max_changes={maxChanges}. Nothing was changed.",
                "Narrow with 'layers' or explicit 'targets', or raise max_changes after confirming with the user.");
        }

        if (planned.Count == 0)
        {
            throw new CadException(ErrorCodes.NotFound, $"No TEXT/MTEXT contains '{find}'.", "Check spelling, case_sensitive, or query the drawing first.");
        }

        foreach (var (state, newText) in planned)
        {
            s.Capture(state.Handle);
            SetAndExpect(s, state.Handle, newText);
        }
    }

    private static void SetAndExpect(ChangeSession s, string handle, string newText)
    {
        s.Tx.SetText(handle, newText);
        s.Expect(handle, $"text == \"{Truncate(newText)}\"", after => after.Text == newText);
    }

    private static void RequireTextLike(EntityState state)
    {
        if (!EntityTypes.TextLike.Contains(state.Type))
        {
            throw new CadException(
                ErrorCodes.Unsupported,
                $"Entity {state.Handle} is {state.Type}, not TEXT/MTEXT.",
                "For block attributes use modify_opening with 'attributes'.");
        }
    }

    // ----------------------------------------------------------------- moving
    private static void Move(ChangeSession s, Params p)
    {
        p.AllowOnly("targets", "handles", "displacement", "from", "to");
        Vec3 d;
        if (p.Has("displacement"))
        {
            d = p.Point("displacement");
        }
        else if (p.Has("from") && p.Has("to"))
        {
            d = p.Point("to") - p.Point("from");
        }
        else
        {
            throw CadException.Invalid("Give 'displacement' or both 'from' and 'to'.");
        }

        if (d.Length < 1e-12)
        {
            throw CadException.Invalid("The displacement is zero.");
        }

        foreach (var (handle, fingerprint) in Targets(p))
        {
            var before = s.Capture(handle, fingerprint);
            var anchor = before.Anchor ?? throw new CadException(
                ErrorCodes.Unsupported, $"Entity {handle} ({before.Type}) has no verifiable reference point.");
            s.Tx.Move(before.Handle, d);
            var expected = anchor + d;
            s.Expect(before.Handle, $"reference point moved to {Fmt(expected)}", after => after.Anchor is { } a && a.IsClose(expected));
        }
    }

    // -------------------------------------------------------- doors/openings
    private void ModifyOpening(ChangeSession s, Params p)
    {
        p.AllowOnly("handle", "expect_fingerprint", "width", "position", "slide", "rotation", "flip_hand", "flip_facing", "attributes");
        var before = s.Capture(p.String("handle"), p.OptString("expect_fingerprint"));
        if (before.Type != EntityTypes.Insert)
        {
            throw new CadException(
                ErrorCodes.Unsupported,
                $"Entity {before.Handle} is {before.Type}; doors, windows and openings must be block references (INSERT).",
                "Query with types=[\"INSERT\"] and block_name to find the opening block.");
        }

        var name = before.Props["name"]?.GetValue<string>() ?? "";
        var pos = before.Point("position") ?? new Vec3(0, 0);
        var rotation = before.Number("rotation") ?? 0;
        var scale = before.Props["scale"] is JsonArray sc ? Vec3.FromJson(sc, "scale") : new Vec3(1, 1, 1);
        var edit = new InsertEdit();
        var sx = scale.X;
        var sy = scale.Y;

        if (p.Has("position") && p.Has("slide"))
        {
            throw CadException.Invalid("Use either 'position' or 'slide', not both.");
        }

        Vec3? newPos = null;
        if (p.OptPoint("position") is { } target)
        {
            newPos = target;
        }
        else if (p.OptNumber("slide") is { } slide)
        {
            // Slide along the opening's own X axis, i.e. along the host wall.
            var r = rotation * Math.PI / 180;
            newPos = pos + new Vec3(Math.Cos(r), Math.Sin(r)) * slide;
        }

        edit.Position = newPos;

        if (p.OptNumber("rotation") is { } rot)
        {
            edit.RotationDegrees = rot;
        }

        double? width = null;
        string? widthProperty = null;
        if (p.Has("width"))
        {
            width = p.Positive("width");
            widthProperty = FindWidthProperty(before);
            if (widthProperty is not null)
            {
                edit.Dynamic[widthProperty] = width.Value;
            }
            else
            {
                var defWidth = s.Tx.BlockDefinitionWidth(name);
                if (defWidth is not > 0)
                {
                    throw new CadException(
                        ErrorCodes.Unsupported,
                        $"Block '{name}' has no width parameter and its definition width is unknown.",
                        "Use a dynamic block with a Width/Distance parameter, or scale it explicitly.");
                }

                sx = Math.Sign(sx == 0 ? 1 : sx) * width.Value / defWidth.Value;
            }
        }

        if (p.Bool("flip_hand"))
        {
            sx = -sx;
        }

        if (p.Bool("flip_facing"))
        {
            sy = -sy;
        }

        if (sx != scale.X)
        {
            edit.ScaleX = sx;
        }

        if (sy != scale.Y)
        {
            edit.ScaleY = sy;
        }

        if (p.OptObject("attributes") is { } attrs)
        {
            var existing = before.Props["attributes"] as JsonObject ?? [];
            foreach (var (tag, value) in attrs)
            {
                if (!existing.ContainsKey(tag) && !existing.Any(kv => string.Equals(kv.Key, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    throw CadException.Invalid($"Block '{name}' has no attribute '{tag}'.", $"Existing: {string.Join(", ", existing.Select(k => k.Key))}.");
                }

                edit.Attributes[tag] = value?.ToString() ?? "";
            }
        }

        if (edit.Position is null && edit.RotationDegrees is null && edit.ScaleX is null && edit.ScaleY is null
            && edit.Dynamic.Count == 0 && edit.Attributes.Count == 0)
        {
            throw CadException.Invalid("Nothing to change.", "Pass width, position/slide, rotation, flip_hand, flip_facing or attributes.");
        }

        s.Tx.EditInsert(before.Handle, edit);

        var h = before.Handle;
        if (width is { } w)
        {
            s.Expect(h, $"opening width == {JsonFmt(w)}", after => CreateSpec.Near(after.Number("width"), w));
        }

        if (edit.Position is { } np)
        {
            s.Expect(h, $"position == {Fmt(np)}", after => after.Point("position") is { } a && a.IsClose(np));
        }

        if (edit.RotationDegrees is { } er)
        {
            s.Expect(h, $"rotation == {JsonFmt(er)}", after => AngleClose(after.Number("rotation"), er));
        }

        if (p.Bool("flip_hand") || p.Bool("flip_facing"))
        {
            s.Expect(h, "mirror state applied", after => after.Props["scale"] is JsonArray a
                && Math.Sign(a[0]!.GetValue<double>()) == Math.Sign(sx) && Math.Sign(a[1]!.GetValue<double>()) == Math.Sign(sy));
        }

        foreach (var (tag, value) in edit.Attributes)
        {
            s.Expect(h, $"attribute {tag} == \"{Truncate(value)}\"", after =>
                after.Props["attributes"] is JsonObject a
                && a.FirstOrDefault(kv => string.Equals(kv.Key, tag, StringComparison.OrdinalIgnoreCase)).Value?.ToString() == value);
        }
    }

    private string? FindWidthProperty(EntityState state)
    {
        if (state.Props["dynamic"] is not JsonObject dyn)
        {
            return null;
        }

        foreach (var candidate in _options.WidthPropertyNames)
        {
            var hit = dyn.FirstOrDefault(kv => string.Equals(kv.Key, candidate, StringComparison.OrdinalIgnoreCase)
                && kv.Value is JsonValue v && v.TryGetValue<double>(out _));
            if (hit.Key is not null)
            {
                return hit.Key;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- create
    private static void Create(ChangeSession s, Params p)
    {
        p.AllowOnly("entities");
        foreach (var node in p.Objects("entities", 200))
        {
            var spec = CreateSpec.Parse(node);
            if (spec.Type == EntityTypes.Insert && !s.Tx.BlockExists(spec.BlockName))
            {
                throw new CadException(ErrorCodes.NotFound, $"Block '{spec.BlockName}' is not defined in this drawing.");
            }

            if (spec.Layer is { } layer && s.Tx.IsLayerLocked(layer))
            {
                throw new CadException(ErrorCodes.LockedLayer, $"Layer '{layer}' is locked.");
            }

            var handle = s.Tx.Create(spec);
            s.RecordCreated(handle);
            s.Expect(handle, $"created {spec.Type} matches the request", spec.Matches);
        }
    }

    // ----------------------------------------------------------------- batch
    private void Batch(ChangeSession s, Params p)
    {
        p.AllowOnly("steps");
        var steps = p.Objects("steps", MaxBatch);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = new Params(steps[i]);
            step.AllowOnly("command", "params");
            var command = step.String("command");
            if (command is "batch" or "status" or "query" or "get")
            {
                throw CadException.Invalid($"steps[{i}]: '{command}' is not allowed inside a batch.");
            }

            try
            {
                Apply(command, s, new Params(step.OptObject("params")?.DeepClone() as JsonObject));
            }
            catch (CadException e)
            {
                throw new CadException(e.Code, $"steps[{i}] ({command}): {e.Message} The whole batch was rolled back.", e.Hint);
            }
        }
    }

    // --------------------------------------------------------------- helpers
    private static IEnumerable<(string Handle, string? Fingerprint)> Targets(Params p)
    {
        if (p.Has("targets"))
        {
            foreach (var t in p.Objects("targets", MaxResults))
            {
                var tp = new Params(t);
                tp.AllowOnly("handle", "expect_fingerprint");
                yield return (tp.String("handle"), tp.OptString("expect_fingerprint"));
            }
        }
        else
        {
            foreach (var h in p.Strings("handles", required: true))
            {
                yield return (h, null);
            }
        }
    }

    private static string NormalizeType(string t)
    {
        var u = t.Trim().ToUpperInvariant();
        return u switch
        {
            "POLYLINE" => EntityTypes.Polyline,
            "BLOCK" or "BLOCKREF" or "DOOR" or "WINDOW" or "OPENING" => EntityTypes.Insert,
            _ => u.StartsWith("ACDB", StringComparison.Ordinal) ? u[4..] : u,
        };
    }

    private static Regex MakeRegex(string pattern, bool ignoreCase)
    {
        try
        {
            return new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException e)
        {
            throw CadException.Invalid($"Invalid regular expression: {e.Message}");
        }
    }

    private static Regex MakeWildcard(string glob) =>
        MakeRegex("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", ignoreCase: true);

    private static (Vec3 Min, Vec3 Max)? ParseWindow(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray { Count: 2 } arr)
        {
            throw CadException.Invalid("'within' must be [[xmin, ymin], [xmax, ymax]].");
        }

        var a = Vec3.FromJson(arr[0], "within[0]");
        var b = Vec3.FromJson(arr[1], "within[1]");
        return (new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }

    private static bool AngleClose(double? actual, double expected)
    {
        if (actual is not { } a)
        {
            return false;
        }

        var diff = Math.Abs(((a - expected) % 360 + 540) % 360 - 180);
        return diff <= 1e-6;
    }

    private static string Fmt(Vec3 v) => $"[{JsonFmt(v.X)}, {JsonFmt(v.Y)}, {JsonFmt(v.Z)}]";

    private static string JsonFmt(double d) => CadJson.Format(d);

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..37] + "...";
}

public sealed class DispatcherOptions
{
    public bool ReadOnly { get; init; }

    /// <summary>Dynamic-block parameter names treated as the opening width, in priority order.</summary>
    public IReadOnlyList<string> WidthPropertyNames { get; init; } = Openings.DefaultWidthPropertyNames;
}
