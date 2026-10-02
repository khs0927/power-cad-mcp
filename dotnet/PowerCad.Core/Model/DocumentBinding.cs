using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

public static class DocumentBinding
{
    public static void Verify(string actual, string? expected)
    {
        if (expected is not null && !string.Equals(actual, expected, StringComparison.Ordinal))
            throw new CadException(ErrorCodes.DocumentChanged,
                "The active drawing differs from the drawing bound to this request.",
                "Select the intended drawing and bind it again; do not reuse handles from another drawing.");
    }
}

/// <summary>Passes the expected identity to the adapter, where it is checked under the document lock.</summary>
public sealed class BoundCadDocument(ICadDocument inner, string id) : ICadDocument
{
    public JsonObject Describe(string? expectedDocumentId = null) => inner.Describe(id);
    public T Execute<T>(Func<ICadTransaction, T> work, bool commit, string? expectedDocumentId = null) =>
        inner.Execute(work, commit, id);
    public JsonObject View(Vec3 min, Vec3 max, int? width, int? height, string? expectedDocumentId = null) =>
        inner.View(min, max, width, height, id);
    public JsonObject Save(SaveRequest request, string? expectedDocumentId = null) => inner.Save(request, id);
}
