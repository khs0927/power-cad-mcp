using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerCad.Core.Transport;

/// <summary>What a running plugin publishes so the server can find and authenticate to it.</summary>
public sealed record DiscoveryInfo(
    int SchemaVersion,
    string Product,
    int Year,
    string PipeName,
    string AuthToken,
    int Pid,
    string PluginVersion,
    DateTimeOffset StartedAt)
{
    public string Target => $"{Product}-{Year}-{Pid}";
}

/// <summary>
/// Discovery files live in %LOCALAPPDATA%\PowerCad (override with POWER_CAD_HOME), one per AutoCAD process.
/// Files whose process is gone are deleted on read.
/// </summary>
public sealed class DiscoveryStore(string? directory = null)
{
    public string Directory { get; } = directory
        ?? Environment.GetEnvironmentVariable("POWER_CAD_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerCad");

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string PathFor(DiscoveryInfo info) => Path.Combine(Directory, $"{info.Product}-{info.Year}-{info.Pid}.json");

    public string Write(DiscoveryInfo info)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(info);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, CadJson.Options));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public void Delete(DiscoveryInfo info)
    {
        try
        {
            File.Delete(PathFor(info));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Live plugins, newest first. Orphans (dead PID) and unreadable files are removed.</summary>
    public IReadOnlyList<DiscoveryInfo> List(Func<int, bool>? isAlive = null)
    {
        isAlive ??= IsProcessAlive;
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var result = new List<DiscoveryInfo>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            DiscoveryInfo? info = null;
            try
            {
                info = JsonSerializer.Deserialize<DiscoveryInfo>(File.ReadAllText(file), CadJson.Options);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
            }

            if (info is null || !isAlive(info.Pid))
            {
                TryDelete(file);
                continue;
            }

            result.Add(info);
        }

        return result.OrderByDescending(i => i.StartedAt).ToList();
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static JsonObject Describe(DiscoveryInfo i) => new()
    {
        ["target"] = i.Target,
        ["product"] = i.Product,
        ["year"] = i.Year,
        ["pid"] = i.Pid,
        ["plugin_version"] = i.PluginVersion,
        ["started_at"] = i.StartedAt.ToString("O"),
    };
}
