using System.Text.Json.Nodes;

namespace PowerCad.Core.Model;

public readonly record struct Vec3(double X, double Y, double Z = 0)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public bool IsClose(Vec3 other, double tol = 1e-6) => (this - other).Length <= tol;

    public JsonArray ToJson() => new(CadJson.Round(X), CadJson.Round(Y), CadJson.Round(Z));

    public static Vec3 FromJson(JsonNode? node, string name)
    {
        if (node is not JsonArray arr || arr.Count is < 2 or > 3)
        {
            throw CadException.Invalid($"'{name}' must be [x, y] or [x, y, z].");
        }

        double Get(int i)
        {
            try
            {
                var d = arr[i]!.GetValue<double>();
                return double.IsFinite(d) ? d : throw new FormatException();
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or NullReferenceException)
            {
                throw CadException.Invalid($"'{name}[{i}]' must be a finite number.");
            }
        }

        return new Vec3(Get(0), Get(1), arr.Count == 3 ? Get(2) : 0);
    }
}
