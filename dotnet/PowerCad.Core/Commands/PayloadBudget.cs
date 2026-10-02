using System.Text;
using System.Text.Json.Nodes;

namespace PowerCad.Core.Commands;

/// <summary>
/// Serialized-size accounting shared by bounded reads (snapshots, inventories). The pipe transport is capped
/// at 1 MiB, so rows are admitted only while the running total stays under a limit.
/// </summary>
public sealed class PayloadBudget
{
    public int Used { get; private set; }

    public static int Size(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString(CadJson.Options));

    /// <summary>Counts <paramref name="node"/> and returns true when it fits under <paramref name="limit"/>.</summary>
    public bool TryAdd(JsonNode node, int limit)
    {
        var size = Size(node);
        if (Used + size >= limit)
        {
            return false;
        }

        Used += size;
        return true;
    }

    /// <summary>Counts <paramref name="node"/> unconditionally (rows that are always returned).</summary>
    public void Add(JsonNode node) => Used += Size(node);
}
