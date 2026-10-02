namespace PowerCad.Core.Model;

/// <summary>Bounds for <see cref="ICadDocument.GetDrawingInventory"/>. Counts always cover the whole drawing.</summary>
public sealed record InventoryOptions(int MaxBlocks = 500, int MaxReferences = 2000, int MaxDepth = 2, int MaxBytes = 600_000)
{
    /// <summary>Leaves room for metadata and the pipe envelope (the transport is capped at 1 MiB).</summary>
    public const int DefaultMaxBytes = 600_000;
}

/// <summary>An attribute definition inside a block definition.</summary>
public sealed record CadAttributeDefinition(string Tag, string Prompt, string DefaultValue, bool Constant);

/// <summary>
/// One block table record: a named block, an anonymous block (e.g. a dynamic block representation), a layout
/// block (*Model_Space, *Paper_Space...) or an XREF. Counts describe the record's own entities only.
/// </summary>
public sealed record CadBlockDefinition(string Name)
{
    /// <summary>The dynamic block an anonymous representation was generated from; otherwise the name itself.</summary>
    public string? EffectiveName { get; init; }

    public bool IsDynamic { get; init; }

    public bool IsAnonymous { get; init; }

    public bool IsLayout { get; init; }

    /// <summary>The record is an attached or overlaid XREF (its contents live in another file).</summary>
    public bool IsXref { get; init; }

    /// <summary>The record was brought in by an XREF ("XREF|NAME").</summary>
    public bool IsDependent { get; init; }

    public IReadOnlyList<CadAttributeDefinition> AttributeDefinitions { get; init; } = [];

    /// <summary>Entity counts by DXF type (LINE, INSERT, ATTDEF, VIEWPORT...).</summary>
    public IReadOnlyDictionary<string, int> CountsByType { get; init; } = new Dictionary<string, int>();

    /// <summary>Direct nested block references by effective block name.</summary>
    public IReadOnlyDictionary<string, int> NestedBlocks { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// A block reference owned directly by a layout block or a block definition. Position, rotation (degrees)
/// and scale are in the owner's coordinates.
/// </summary>
public sealed record CadBlockReference(string Handle, string BlockName, string EffectiveName, Vec3 Position, double Rotation, Vec3 Scale, string Layer)
{
    public bool IsDynamic { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = [];
}

/// <summary>A layout tab and its block. Plot settings are optional (null when unknown).</summary>
public sealed record CadLayout(string Name, int TabOrder, bool IsModel, string BlockName)
{
    public string? PlotDevice { get; init; }

    public string? Media { get; init; }

    public string? PaperUnits { get; init; }

    public string? PlotRotation { get; init; }

    public double? PaperWidth { get; init; }

    public double? PaperHeight { get; init; }
}

/// <summary>
/// An XREF as the host database knows it; the referenced file is never opened. Status is snake_case:
/// resolved, unloaded, unreferenced, file_not_found, unresolved.
/// </summary>
public sealed record CadXref(string Name, string Path, string Status, bool IsOverlay)
{
    /// <summary>Referenced only through another XREF.</summary>
    public bool IsNested { get; init; }

    /// <summary>Names of the XREFs this XREF references.</summary>
    public IReadOnlyList<string> NestedXrefs { get; init; } = [];
}
