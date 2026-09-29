using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

/// <summary>
/// Reusable block assets: one DWG per block plus a JSON card (texts, extents, source drawing) in a library
/// folder shared by the server and the AutoCAD plugin (same user). Override with POWER_CAD_BLOCK_LIBRARY.
/// </summary>
public static class BlockLibrary
{
    public static string Directory =>
        Environment.GetEnvironmentVariable("POWER_CAD_BLOCK_LIBRARY") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerCad", "blocks");

    /// <summary>File name safe on Windows; Korean and spaces are kept.</summary>
    public static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '$' ? '_' : c)).Trim();

    /// <summary>The DWG path for an asset: an explicit path (".dwg" added) or library/&lt;name&gt;.dwg.</summary>
    public static string Resolve(string name, string? path)
    {
        var p = path is { Length: > 0 }
            ? Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Directory, path))
            : Path.Combine(Directory, SafeName(name) + ".dwg");
        return p.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) ? p : p + ".dwg";
    }

    public static string CardPath(string dwgPath) => Path.ChangeExtension(dwgPath, ".json");

    public static void WriteCard(string dwgPath, JsonObject card) =>
        File.WriteAllText(CardPath(dwgPath), card.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

    /// <summary>All assets in the library: the JSON card when present, otherwise just name and path.</summary>
    public static JsonArray List()
    {
        var items = new JsonArray();
        if (!System.IO.Directory.Exists(Directory))
        {
            return items;
        }

        foreach (var dwg in System.IO.Directory.GetFiles(Directory, "*.dwg").Order(StringComparer.OrdinalIgnoreCase))
        {
            JsonObject item;
            try
            {
                item = File.Exists(CardPath(dwg)) && JsonNode.Parse(File.ReadAllText(CardPath(dwg))) is JsonObject card ? card : new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                item = new JsonObject();
            }

            item["name"] ??= Path.GetFileNameWithoutExtension(dwg);
            item["path"] = dwg;
            items.Add(item);
        }

        return items;
    }
}
