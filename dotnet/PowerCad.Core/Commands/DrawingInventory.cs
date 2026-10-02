using System.Text.Json.Nodes;
using PowerCad.Core.Model;

namespace PowerCad.Core.Commands;

/// <summary>
/// Builds the read-only drawing inventory (layouts, block definitions, block references, XREFs) from the
/// backend-neutral transaction primitives, so AutoCAD and the simulator share one shape and one set of bounds.
/// Counts always cover the whole drawing; only returned rows are bounded.
/// </summary>
public static class DrawingInventory
{
    public const int MaxBlocksLimit = 5000;
    public const int MaxReferencesLimit = 10000;
    public const int MaxDepthLimit = 8;

    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public static JsonObject Build(ICadTransaction tx, InventoryOptions options)
    {
        var layouts = tx.Layouts();
        var definitions = tx.BlockDefinitions().ToList();
        var xrefs = tx.Xrefs();
        var byName = new Dictionary<string, CadBlockDefinition>(Names);
        foreach (var definition in definitions)
        {
            byName.TryAdd(definition.Name, definition);
        }

        var xrefNames = new HashSet<string>(xrefs.Select(x => x.Name), Names);
        foreach (var definition in definitions.Where(d => d.IsXref))
        {
            xrefNames.Add(definition.Name);
        }

        // ---- one pass over each layout's top-level references: complete counts, bounded rows
        var insertCounts = new Dictionary<string, Dictionary<string, int>>(Names); // block -> layout -> count
        var children = new Dictionary<string, List<CadBlockReference>>(Names);
        var candidates = new List<JsonObject>();
        var topLevelByLayout = new Dictionary<string, int>(Names);
        var topLevel = 0;
        var nested = 0;
        var referencesTruncated = false;
        var depthLimited = false;

        List<CadBlockReference> Children(string block)
        {
            if (!children.TryGetValue(block, out var list))
            {
                list = xrefNames.Contains(block) ? new List<CadBlockReference>() : tx.BlockReferences(block).ToList();
                children[block] = list;
            }

            return list;
        }

        void Expand(CadBlockReference parent, string layout, List<string> path, List<string> blockPath, int depth)
        {
            if (xrefNames.Contains(parent.BlockName))
            {
                return;
            }

            var rows = Children(parent.BlockName);
            if (rows.Count == 0)
            {
                return;
            }

            if (depth >= options.MaxDepth)
            {
                depthLimited = true;
                return;
            }

            foreach (var child in rows)
            {
                nested++;
                if (blockPath.Contains(child.BlockName, Names))
                {
                    continue; // never recurse into a block that contains itself
                }

                if (candidates.Count >= options.MaxReferences)
                {
                    referencesTruncated = true;
                    return;
                }

                List<string> childPath = [.. path, child.Handle];
                List<string> childBlocks = [.. blockPath, child.BlockName];
                candidates.Add(ReferenceRow(child, layout, depth + 1, childPath, childBlocks, parent.Handle, xrefNames));
                Expand(child, layout, childPath, childBlocks, depth + 1);
            }
        }

        foreach (var layout in layouts)
        {
            var count = 0;
            foreach (var reference in tx.BlockReferences(layout.BlockName))
            {
                count++;
                topLevel++;
                Count(insertCounts, reference.BlockName, layout.Name);
                if (!Names.Equals(reference.EffectiveName, reference.BlockName))
                {
                    Count(insertCounts, reference.EffectiveName, layout.Name);
                }

                if (candidates.Count >= options.MaxReferences)
                {
                    referencesTruncated = true;
                    continue;
                }

                List<string> path = [reference.Handle];
                List<string> blocks = [reference.BlockName];
                candidates.Add(ReferenceRow(reference, layout.Name, 0, path, blocks, null, xrefNames));
                Expand(reference, layout.Name, path, blocks, 0);
            }

            topLevelByLayout[layout.Name] = count;
        }

        // ---- rows, in priority order: layouts (always), XREFs, block definitions, references
        var budget = new PayloadBudget();
        var layoutRows = new JsonArray();
        foreach (var layout in layouts)
        {
            var row = LayoutRow(layout, byName.GetValueOrDefault(layout.BlockName), topLevelByLayout.GetValueOrDefault(layout.Name));
            budget.Add(row);
            layoutRows.Add(row);
        }

        var xrefRows = new JsonArray();
        var xrefsTruncated = false;
        foreach (var xref in xrefs)
        {
            var row = XrefRow(xref, insertCounts.GetValueOrDefault(xref.Name));
            if (budget.TryAdd(row, options.MaxBytes))
            {
                xrefRows.Add(row);
            }
            else
            {
                xrefsTruncated = true;
            }
        }

        var xrefByName = new Dictionary<string, CadXref>(Names);
        foreach (var xref in xrefs)
        {
            xrefByName.TryAdd(xref.Name, xref);
        }

        var blockRows = new JsonArray();
        var blocksTruncated = false;
        foreach (var definition in definitions)
        {
            if (blockRows.Count >= options.MaxBlocks)
            {
                blocksTruncated = true;
                break;
            }

            var row = BlockRow(definition, insertCounts.GetValueOrDefault(definition.Name), xrefByName.GetValueOrDefault(definition.Name));
            if (!budget.TryAdd(row, options.MaxBytes))
            {
                blocksTruncated = true;
                break;
            }

            blockRows.Add(row);
        }

        var referenceRows = new JsonArray();
        foreach (var row in candidates)
        {
            if (!budget.TryAdd(row, options.MaxBytes))
            {
                referencesTruncated = true;
                break;
            }

            referenceRows.Add(row);
        }

        return new JsonObject
        {
            ["scope"] = "native_inventory",
            ["read_only"] = true,
            ["limits"] = new JsonObject
            {
                ["max_blocks"] = options.MaxBlocks,
                ["max_references"] = options.MaxReferences,
                ["max_depth"] = options.MaxDepth,
                ["max_bytes"] = options.MaxBytes,
            },
            ["excluded_scopes"] = new JsonArray("xref_contents", "entity_geometry"),
            ["layout_count"] = layouts.Count,
            ["block_definition_count"] = definitions.Count,
            ["xref_count"] = xrefs.Count,
            ["top_level_reference_count"] = topLevel,
            ["nested_reference_count"] = nested,
            ["returned_reference_count"] = referenceRows.Count,
            ["blocks_truncated"] = blocksTruncated,
            ["references_truncated"] = referencesTruncated,
            ["xrefs_truncated"] = xrefsTruncated,
            ["depth_limited"] = depthLimited,
            ["truncated"] = blocksTruncated || referencesTruncated || xrefsTruncated,
            ["layouts"] = layoutRows,
            ["xrefs"] = xrefRows,
            ["block_definitions"] = blockRows,
            ["block_references"] = referenceRows,
        };
    }

    private static void Count(Dictionary<string, Dictionary<string, int>> counts, string block, string layout)
    {
        if (!counts.TryGetValue(block, out var perLayout))
        {
            perLayout = new Dictionary<string, int>(Names);
            counts[block] = perLayout;
        }

        perLayout[layout] = perLayout.TryGetValue(layout, out var n) ? n + 1 : 1;
    }

    private static JsonObject CountsJson(IEnumerable<KeyValuePair<string, int>> counts) =>
        new(counts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    private static JsonObject LayoutRow(CadLayout layout, CadBlockDefinition? block, int references)
    {
        var counts = block?.CountsByType ?? new Dictionary<string, int>();
        var row = new JsonObject
        {
            ["name"] = layout.Name,
            ["tab_order"] = layout.TabOrder,
            ["is_model"] = layout.IsModel,
            ["block"] = layout.BlockName,
            ["entity_count"] = counts.Values.Sum(),
            ["counts_by_type"] = CountsJson(counts),
            ["viewport_count"] = counts.GetValueOrDefault("VIEWPORT"),
            ["block_reference_count"] = references,
        };
        if (layout.PlotDevice is not null || layout.Media is not null)
        {
            row["plot"] = new JsonObject
            {
                ["device"] = layout.PlotDevice,
                ["media"] = layout.Media,
                ["paper_units"] = layout.PaperUnits,
                ["rotation"] = layout.PlotRotation,
                ["paper_size"] = layout.PaperWidth is { } w && layout.PaperHeight is { } h
                    ? new JsonArray(CadJson.Round(w), CadJson.Round(h))
                    : null,
            };
        }

        return row;
    }

    private static JsonObject XrefRow(CadXref xref, Dictionary<string, int>? inserts) => new()
    {
        ["name"] = xref.Name,
        ["path"] = xref.Path,
        ["status"] = xref.Status,
        ["found"] = xref.Status == "resolved",
        ["type"] = xref.IsOverlay ? "overlay" : "attach",
        ["is_nested"] = xref.IsNested,
        ["nested_xrefs"] = new JsonArray(xref.NestedXrefs.Select(n => (JsonNode)n).ToArray()),
        ["insert_count"] = inserts?.Values.Sum() ?? 0,
    };

    private static JsonObject BlockRow(CadBlockDefinition definition, Dictionary<string, int>? inserts, CadXref? xref)
    {
        var row = new JsonObject
        {
            ["name"] = definition.Name,
            ["effective_name"] = definition.EffectiveName ?? definition.Name,
            ["is_dynamic"] = definition.IsDynamic,
            ["is_anonymous"] = definition.IsAnonymous,
            ["is_layout"] = definition.IsLayout,
            ["is_xref"] = definition.IsXref,
            ["is_from_external_reference"] = definition.IsXref || definition.IsDependent,
            ["is_xref_dependent"] = definition.IsDependent,
            ["attribute_definitions"] = new JsonArray(definition.AttributeDefinitions.Select(a => (JsonNode)new JsonObject
            {
                ["tag"] = a.Tag,
                ["prompt"] = a.Prompt,
                ["default"] = a.DefaultValue,
                ["constant"] = a.Constant,
            }).ToArray()),
            ["entity_count"] = definition.CountsByType.Values.Sum(),
            ["counts_by_type"] = CountsJson(definition.CountsByType),
            ["nested_blocks"] = CountsJson(definition.NestedBlocks),
            ["insert_count"] = inserts?.Values.Sum() ?? 0,
            ["insert_counts_by_layout"] = CountsJson(inserts ?? new Dictionary<string, int>()),
        };
        if (xref is not null)
        {
            row["xref"] = new JsonObject
            {
                ["path"] = xref.Path,
                ["status"] = xref.Status,
                ["found"] = xref.Status == "resolved",
                ["type"] = xref.IsOverlay ? "overlay" : "attach",
            };
        }

        return row;
    }

    private static JsonObject ReferenceRow(CadBlockReference r, string layout, int depth, List<string> path, List<string> blocks, string? parent, HashSet<string> xrefNames)
    {
        var row = new JsonObject
        {
            ["layout"] = layout,
            ["handle"] = r.Handle,
            ["block_name"] = r.BlockName,
            ["effective_name"] = r.EffectiveName,
            ["is_dynamic"] = r.IsDynamic,
            ["is_xref"] = xrefNames.Contains(r.BlockName),
            ["layer"] = r.Layer,
            ["position"] = r.Position.ToJson(),
            ["rotation"] = CadJson.Round(r.Rotation),
            ["scale"] = r.Scale.ToJson(),
            ["attributes"] = new JsonArray(r.Attributes.Select(a => (JsonNode)new JsonObject { ["tag"] = a.Key, ["value"] = a.Value }).ToArray()),
            ["depth"] = depth,
            ["coordinates"] = depth == 0 ? "layout" : "parent_block",
            ["path"] = new JsonArray(path.Select(h => (JsonNode)h).ToArray()),
            ["block_path"] = new JsonArray(blocks.Select(b => (JsonNode)b).ToArray()),
        };
        if (parent is not null)
        {
            row["parent_handle"] = parent;
        }

        return row;
    }
}
