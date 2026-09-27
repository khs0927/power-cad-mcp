using System.Text.Json.Nodes;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

/// <summary>Typed, validating accessors over a JSON params object.</summary>
public sealed class Params(JsonObject? node)
{
    public JsonObject Node { get; } = node ?? [];

    public bool Has(string name) => Node[name] is not null;

    public string String(string name)
    {
        var s = OptString(name);
        return string.IsNullOrEmpty(s) ? throw CadException.Invalid($"'{name}' is required.") : s;
    }

    public string? OptString(string name)
    {
        var n = Node[name];
        if (n is null)
        {
            return null;
        }

        return n is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : throw CadException.Invalid($"'{name}' must be a string.");
    }

    public double Number(string name) => OptNumber(name) ?? throw CadException.Invalid($"'{name}' is required.");

    public double? OptNumber(string name)
    {
        var n = Node[name];
        if (n is null)
        {
            return null;
        }

        return n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)
            ? d
            : throw CadException.Invalid($"'{name}' must be a finite number.");
    }

    public double Positive(string name)
    {
        var d = Number(name);
        return d > 0 ? d : throw CadException.Invalid($"'{name}' must be > 0.");
    }

    public int Int(string name, int fallback, int min, int max)
    {
        var d = OptNumber(name);
        if (d is null)
        {
            return fallback;
        }

        if (d != Math.Floor(d.Value) || d < min || d > max)
        {
            throw CadException.Invalid($"'{name}' must be an integer between {min} and {max}.");
        }

        return (int)d.Value;
    }

    public bool Bool(string name, bool fallback = false)
    {
        var n = Node[name];
        if (n is null)
        {
            return fallback;
        }

        return n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw CadException.Invalid($"'{name}' must be true or false.");
    }

    public Vec3 Point(string name) => Vec3.FromJson(Node[name] ?? throw CadException.Invalid($"'{name}' is required."), name);

    public Vec3? OptPoint(string name) => Node[name] is null ? null : Vec3.FromJson(Node[name], name);

    public List<string> Strings(string name, bool required = false)
    {
        var n = Node[name];
        if (n is null)
        {
            return required ? throw CadException.Invalid($"'{name}' is required.") : [];
        }

        if (n is not JsonArray arr)
        {
            throw CadException.Invalid($"'{name}' must be an array of strings.");
        }

        var list = arr.Select((x, i) => x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : throw CadException.Invalid($"'{name}[{i}]' must be a non-empty string.")).ToList();
        if (required && list.Count == 0)
        {
            throw CadException.Invalid($"'{name}' must not be empty.");
        }

        return list;
    }

    public List<JsonObject> Objects(string name, int max)
    {
        if (Node[name] is not JsonArray arr || arr.Count == 0)
        {
            throw CadException.Invalid($"'{name}' must be a non-empty array of objects.");
        }

        if (arr.Count > max)
        {
            throw CadException.Invalid($"'{name}' has {arr.Count} items; the limit is {max}.", "Split the work into smaller calls.");
        }

        return arr.Select((x, i) => x as JsonObject ?? throw CadException.Invalid($"'{name}[{i}]' must be an object.")).ToList();
    }

    public JsonObject? OptObject(string name) => Node[name] switch
    {
        null => null,
        JsonObject o => o,
        _ => throw CadException.Invalid($"'{name}' must be an object."),
    };

    /// <summary>Fails on keys the command does not understand, so typos never silently do nothing.</summary>
    public void AllowOnly(params string[] names)
    {
        var unknown = Node.Select(kv => kv.Key).Where(k => !names.Contains(k, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw CadException.Invalid(
                $"Unknown parameter(s): {string.Join(", ", unknown)}.",
                $"Allowed: {string.Join(", ", names)}.");
        }
    }
}
