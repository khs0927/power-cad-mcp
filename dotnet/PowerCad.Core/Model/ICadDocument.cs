using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

/// <summary>A drawing that can run work inside one database transaction (AutoCAD or simulated).</summary>
public interface ICadDocument
{
    JsonObject Describe();

    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction. The transaction commits only when
    /// <paramref name="commit"/> is true and <paramref name="work"/> returns normally; any exception
    /// aborts it, so a failed verification leaves the drawing untouched.
    /// </summary>
    T Execute<T>(Func<ICadTransaction, T> work, bool commit);

    /// <summary>
    /// Zooms the active view to the window (no database change). When <paramref name="snapshotWidth"/> is
    /// set, also renders the view and returns it as a base64 PNG under "image_base64".
    /// </summary>
    JsonObject View(Vec3 min, Vec3 max, int? snapshotWidth, int? snapshotHeight);
}

public interface ICadTransaction
{
    IEnumerable<EntityState> ScanModelSpace();

    /// <exception cref="CadException">NOT_FOUND when the handle is not a model-space entity.</exception>
    EntityState Read(string handle);

    bool IsLayerLocked(string layer);

    void SetText(string handle, string text);

    void Move(string handle, Vec3 displacement);

    void EditInsert(string handle, InsertEdit edit);

    /// <summary>Width of a block definition's geometry along its local X axis, or null if unknown.</summary>
    double? BlockDefinitionWidth(string blockName);

    bool BlockExists(string blockName);

    /// <summary>Creates an entity from a validated spec (see <see cref="Commands.CreateSpec"/>) and returns its handle.</summary>
    string Create(Commands.CreateSpec spec);

    /// <summary>Erases a model-space entity.</summary>
    void Delete(string handle);

    /// <summary>Changes layer/color/linetype/lineweight and, for TEXT/MTEXT, height/rotation/style/justification.</summary>
    void SetProperties(string handle, PropertyEdit edit);

    /// <summary>Copies an entity, displaces the copy and returns its handle.</summary>
    string Copy(string handle, Vec3 displacement);

    /// <summary>Rotates, scales or mirrors an entity in place. TEXT/MTEXT stay readable when mirrored (MIRRTEXT=0).</summary>
    void Transform(string handle, Transform2D transform);

    /// <summary>All layers (name, color, linetype, lineweight, on, frozen, locked, plot, current).</summary>
    IReadOnlyList<JsonObject> Layers();

    /// <summary>Creates or changes one layer.</summary>
    void SetLayer(LayerEdit edit);

    /// <summary>Drawing resources: units, current settings, text/dim styles, linetypes, blocks, extents.</summary>
    JsonObject Inspect();

    /// <summary>
    /// Writes a block definition to a standalone DWG (its contents in model space, block base point at the
    /// origin) and returns what was exported: extents, entity_count, texts.
    /// </summary>
    JsonObject ExportBlock(string name, string path);

    /// <summary>Defines (or, with <paramref name="replace"/>, redefines) block <paramref name="name"/> from a DWG file.</summary>
    void ImportBlock(string path, string name, bool replace);

    /// <summary>
    /// A hatch's pattern as evaluated in the drawing: name, type, scale, angle and its lines (angle in
    /// degrees, base, offset vector, dashes - all in drawing units), plus "support_dir" (AutoCAD's user
    /// Support folder, where .pat files are found by name).
    /// </summary>
    JsonObject HatchPattern(string handle);

    /// <summary>True when the named resource exists. kind: text_style, dim_style, linetype, layer.</summary>
    bool ResourceExists(string kind, string name);
}

/// <summary>Changes to a block reference (doors, windows, openings...). Null means "leave as is".</summary>
public sealed class InsertEdit
{
    public Vec3? Position { get; set; }

    public double? RotationDegrees { get; set; }

    public double? ScaleX { get; set; }

    public double? ScaleY { get; set; }

    public Dictionary<string, double> Dynamic { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
}
