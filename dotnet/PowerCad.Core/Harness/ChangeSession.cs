using System.Text.Json.Nodes;
using PowerCad.Core.Model;

namespace PowerCad.Core.Harness;

/// <summary>
/// Verified-change pipeline, applied to every mutating command:
/// <list type="number">
/// <item>Capture: read each target once, compare against the agent's expected fingerprint (reject stale
/// analyses) and refuse locked layers.</item>
/// <item>Apply: the command mutates the targets inside the open transaction.</item>
/// <item>Verify: re-read only the touched entities and evaluate the command's postconditions.</item>
/// <item>Report: a per-entity diff. Any failed postcondition throws, which aborts the transaction.</item>
/// </list>
/// Only the targets and newly created entities are inspected, never the whole drawing.
/// </summary>
public sealed class ChangeSession(ICadTransaction tx)
{
    private readonly Dictionary<string, EntityState> _before = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private readonly List<string> _created = [];
    private readonly List<(string Handle, string Description, Func<EntityState, bool> Check)> _checks = [];

    public ICadTransaction Tx { get; } = tx;

    /// <summary>Snapshot a target before changing it; enforces the fingerprint and layer-lock preconditions.</summary>
    public EntityState Capture(string handle, string? expectFingerprint = null)
    {
        if (_before.TryGetValue(handle, out var known))
        {
            // Already captured earlier in this batch: the current state is what later steps must match.
            var current = Tx.Read(handle);
            CheckFingerprint(current, expectFingerprint);
            return current;
        }

        var state = Tx.Read(handle);
        CheckFingerprint(state, expectFingerprint);
        if (Tx.IsLayerLocked(state.Layer))
        {
            throw new CadException(
                ErrorCodes.LockedLayer,
                $"Entity {state.Handle} is on locked layer '{state.Layer}'.",
                "Ask the user before unlocking the layer; do not unlock it silently.");
        }

        _before[state.Handle] = state;
        _order.Add(state.Handle);
        return state;
    }

    private static void CheckFingerprint(EntityState state, string? expected)
    {
        if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, state.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new CadException(
                ErrorCodes.StaleTarget,
                $"Entity {state.Handle} changed since it was analysed (expected fingerprint {expected}, now {state.Fingerprint}).",
                "Re-query the entity, re-check that it is still the intended target, then retry with the new fingerprint.");
        }
    }

    public void Expect(string handle, string description, Func<EntityState, bool> check) =>
        _checks.Add((handle, description, check));

    public void RecordCreated(string handle) => _created.Add(handle);

    /// <summary>Evaluates all postconditions and builds the diff report. Throws VERIFY_FAILED on any failure.</summary>
    public JsonObject VerifyAndReport()
    {
        var after = new Dictionary<string, EntityState>(StringComparer.OrdinalIgnoreCase);
        EntityState ReadAfter(string handle)
        {
            if (!after.TryGetValue(handle, out var s))
            {
                s = Tx.Read(handle);
                after[handle] = s;
            }

            return s;
        }

        var failures = new JsonArray();
        foreach (var (handle, description, check) in _checks)
        {
            bool ok;
            string? detail = null;
            try
            {
                ok = check(ReadAfter(handle));
            }
            catch (CadException e)
            {
                ok = false;
                detail = e.Message;
            }

            if (!ok)
            {
                failures.Add(new JsonObject { ["handle"] = handle, ["check"] = description, ["detail"] = detail });
            }
        }

        if (failures.Count > 0)
        {
            var ex = new CadException(
                ErrorCodes.VerifyFailed,
                $"{failures.Count} post-change check(s) failed; the transaction was rolled back and the drawing is unchanged: "
                + string.Join("; ", failures.Select(f => $"{f!["handle"]}: {f["check"]}")),
                "Inspect the entity with cad_get, adjust the request (units, block parameters, locked/annotative objects) and retry.");
            throw ex;
        }

        var changes = new JsonArray();
        foreach (var handle in _order)
        {
            var before = _before[handle];
            var now = ReadAfter(handle);
            var diff = Diff(before, now);
            if (diff.Count == 0)
            {
                continue;
            }

            changes.Add(new JsonObject
            {
                ["handle"] = handle,
                ["type"] = now.Type,
                ["before_fingerprint"] = before.Fingerprint,
                ["after_fingerprint"] = now.Fingerprint,
                ["changed"] = diff,
            });
        }

        return new JsonObject
        {
            ["changes"] = changes,
            ["created"] = new JsonArray(_created.Select(h => (JsonNode)ReadAfter(h).ToJson()).ToArray()),
            ["checks_passed"] = _checks.Count,
        };
    }

    public static JsonObject Diff(EntityState before, EntityState after)
    {
        var diff = new JsonObject();
        if (before.Layer != after.Layer)
        {
            diff["layer"] = new JsonObject { ["before"] = before.Layer, ["after"] = after.Layer };
        }

        foreach (var key in before.Props.Select(p => p.Key).Union(after.Props.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var b = CadJson.Canonical(before.Props[key]);
            var a = CadJson.Canonical(after.Props[key]);
            if (!JsonNode.DeepEquals(b, a))
            {
                diff[key] = new JsonObject { ["before"] = b, ["after"] = a };
            }
        }

        return diff;
    }
}
