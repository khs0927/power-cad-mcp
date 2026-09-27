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
