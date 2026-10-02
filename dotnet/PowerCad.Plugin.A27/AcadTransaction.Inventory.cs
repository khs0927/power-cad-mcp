using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using PowerCad.Core;
using PowerCad.Core.Model;

namespace PowerCad.Plugin;

/// <summary>
/// Read-only inventory primitives: block table records, layouts, block references and XREFs. Everything is
/// opened ForRead inside the caller's transaction; XREF files are never opened (only the host database is read).
/// </summary>
internal sealed partial class AcadTransaction
{
    public IEnumerable<CadBlockDefinition> BlockDefinitions()
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var records = new List<BlockTableRecord>();
        var dynamicParents = new Dictionary<ObjectId, string>();
        foreach (ObjectId id in bt)
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord btr)
            {
                continue;
            }

            records.Add(btr);
            if (btr.IsDynamicBlock)
            {
                // anonymous representations (*U..) created when a dynamic block's parameters change
                foreach (ObjectId anonymous in btr.GetAnonymousBlockIds())
                {
                    dynamicParents[anonymous] = btr.Name;
                }
            }
        }

        var rows = new List<CadBlockDefinition>();
        foreach (var btr in records)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var nested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var attributes = new List<CadAttributeDefinition>();
            if (!btr.IsFromExternalReference)
            {
                foreach (ObjectId id in btr)
                {
                    if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not Entity e)
                    {
                        continue;
                    }

                    Increment(counts, DxfType(e));
                    switch (e)
                    {
                        case AttributeDefinition ad:
                            attributes.Add(new CadAttributeDefinition(ad.Tag, ad.Prompt, ad.TextString, ad.Constant));
                            break;
                        case BlockReference br:
                            Increment(nested, EffectiveName(br));
                            break;
                    }
                }
            }

            rows.Add(new CadBlockDefinition(btr.Name)
            {
                EffectiveName = dynamicParents.TryGetValue(btr.ObjectId, out var parent) ? parent : btr.Name,
                IsDynamic = btr.IsDynamicBlock,
                IsAnonymous = btr.IsAnonymous,
                IsLayout = btr.IsLayout,
                IsXref = btr.IsFromExternalReference,
                IsDependent = btr.IsDependent,
                AttributeDefinitions = attributes,
                CountsByType = counts,
                NestedBlocks = nested,
            });
        }

        return rows;
    }

    public IReadOnlyList<CadLayout> Layouts()
    {
        var rows = new List<CadLayout>();
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            if (entry.Value.IsErased || tr.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout)
            {
                continue;
            }

            var block = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            var paper = layout.PlotPaperSize;
            rows.Add(new CadLayout(layout.LayoutName, layout.TabOrder, layout.ModelType, block.Name)
            {
                PlotDevice = layout.PlotConfigurationName,
                Media = layout.CanonicalMediaName,
                PaperUnits = layout.PlotPaperUnits.ToString(),
                PlotRotation = layout.PlotRotation.ToString(),
                PaperWidth = paper.X,
                PaperHeight = paper.Y,
            });
        }

        return rows.OrderBy(l => l.TabOrder).ToList();
    }

    public IEnumerable<CadBlockReference> BlockReferences(string ownerBlock)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var rows = new List<CadBlockReference>();
        if (!bt.Has(ownerBlock))
        {
            return rows;
        }

        var owner = (BlockTableRecord)tr.GetObject(bt[ownerBlock], OpenMode.ForRead);
        if (owner.IsFromExternalReference)
        {
            return rows; // XREF contents live in another file
        }

        foreach (ObjectId id in owner)
        {
            // MINSERT and tables also derive from BlockReference; only plain INSERTs are inventoried
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || DxfType(br) != EntityTypes.Insert)
            {
                continue;
            }

            var attributes = new List<KeyValuePair<string, string>>();
            foreach (ObjectId attId in br.AttributeCollection)
            {
                if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference att)
                {
                    attributes.Add(KeyValuePair.Create(att.Tag, att.TextString));
                }
            }

            var block = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            var s = br.ScaleFactors;
            rows.Add(new CadBlockReference(
                br.Handle.ToString(),
                block.Name,
                EffectiveName(br),
                new Vec3(br.Position.X, br.Position.Y, br.Position.Z),
                CadJson.Round(br.Rotation * Deg),
                new Vec3(s.X, s.Y, s.Z),
                br.Layer)
            {
                IsDynamic = br.IsDynamicBlock,
                Attributes = attributes,
            });
        }

        return rows;
    }

    public IReadOnlyList<CadXref> Xrefs()
    {
        var graph = XrefLinks();
        var rows = new List<CadXref>();
        foreach (ObjectId id in (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead))
        {
            if (id.IsErased || tr.GetObject(id, OpenMode.ForRead) is not BlockTableRecord btr || !btr.IsFromExternalReference)
            {
                continue;
            }

            var row = new CadXref(btr.Name, btr.PathName, SnakeCase(btr.XrefStatus.ToString()), btr.IsFromOverlayReference);
            if (graph.TryGetValue(btr.Name, out var link))
            {
                row = row with { IsNested = link.IsNested, NestedXrefs = link.Children };
            }

            rows.Add(row);
        }

        return rows;
    }

    private sealed record XrefLink(bool IsNested, List<string> Children);

    /// <summary>
    /// The host drawing's XREF graph (what the host database already knows; no file is opened). Node 0 is the
    /// host itself. When the graph is unavailable the XREFs are still reported, without nesting.
    /// </summary>
    private Dictionary<string, XrefLink> XrefLinks()
    {
        var links = new Dictionary<string, XrefLink>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var graph = db.GetHostDwgXrefGraph(true);
            for (var i = 1; i < graph.NumNodes; i++)
            {
                var node = graph.GetXrefNode(i);
                var children = new List<string>();
                for (var j = 0; j < node.NumOut; j++)
                {
                    if (node.Out(j) is XrefGraphNode child)
                    {
                        children.Add(child.Name);
                    }
                }

                links[node.Name] = new XrefLink(node.IsNested, children);
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            links.Clear(); // report the flat list rather than fail the whole inventory
        }

        return links;
    }

    private static string DxfType(Entity e) =>
        e.GetRXClass().DxfName is { Length: > 0 } dxf ? dxf : e.GetType().Name.ToUpperInvariant();

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    /// <summary>FileNotFound -> file_not_found.</summary>
    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && sb.Length > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
