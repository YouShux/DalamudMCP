using System.Numerics;
using System.Text.Json.Serialization;

namespace DalamudMCP.Plugin.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<MechanicShape>))]
internal enum MechanicShape { Circle, Donut, Cone, Rectangle, Cross, Polygon }

internal sealed record MechanicArea(MechanicShape Shape, float X, float Z, float Radius = 0,
    float InnerRadius = 0, float Rotation = 0, float HalfAngle = 0, float Front = 0, float Back = 0,
    float HalfWidth = 0, float[][]? Vertices = null)
{
    internal bool Contains(float x, float z)
    {
        if (Shape == MechanicShape.Polygon) return ContainsPolygon(x, z, Vertices!);
        var dx = x - X;
        var dz = z - Z;
        var squared = dx * dx + dz * dz;
        if (Shape == MechanicShape.Circle) return squared <= Radius * Radius;
        if (Shape == MechanicShape.Donut) return squared >= InnerRadius * InnerRadius && squared <= Radius * Radius;
        var sin = MathF.Sin(Rotation);
        var cos = MathF.Cos(Rotation);
        var forward = dx * sin + dz * cos;
        var side = dx * cos - dz * sin;
        return Shape switch
        {
            MechanicShape.Cone => squared <= Radius * Radius && forward >= MathF.Cos(HalfAngle) * MathF.Sqrt(squared) - 0.00001f,
            MechanicShape.Rectangle => forward >= -Back && forward <= Front && MathF.Abs(side) <= HalfWidth,
            MechanicShape.Cross => MathF.Abs(forward) <= Radius && MathF.Abs(side) <= HalfWidth ||
                MathF.Abs(side) <= Radius && MathF.Abs(forward) <= HalfWidth,
            _ => false,
        };
    }

    internal bool MayOverlapCell(double x, double z, double cellRadius)
    {
        var dx = x - X;
        var dz = z - Z;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (Shape == MechanicShape.Circle) return distance <= Radius + cellRadius;
        if (Shape == MechanicShape.Donut) return distance <= Radius + cellRadius && distance + cellRadius >= InnerRadius;
        var sin = Math.Sin(Rotation);
        var cos = Math.Cos(Rotation);
        var forward = dx * sin + dz * cos;
        var side = Math.Abs(dx * cos - dz * sin);
        if (Shape == MechanicShape.Rectangle) return RectDistance(forward, side, Front, Back, HalfWidth) <= cellRadius;
        if (Shape == MechanicShape.Cross) return Math.Min(RectDistance(forward, side, Radius, Radius, HalfWidth),
            RectDistance(side, Math.Abs(forward), Radius, Radius, HalfWidth)) <= cellRadius;
        if (Shape != MechanicShape.Cone) return true;
        if (Math.Atan2(side, forward) <= HalfAngle) return distance <= Radius + cellRadius;
        var angleSin = Math.Sin(HalfAngle);
        var angleCos = Math.Cos(HalfAngle);
        var along = Math.Clamp(forward * angleCos + side * angleSin, 0, Radius);
        var a = forward - along * angleCos;
        var b = side - along * angleSin;
        return a * a + b * b <= cellRadius * cellRadius;

        static double RectDistance(double a, double b, double front, double back, double width)
        {
            var x = Math.Max(0, Math.Max(a - front, -a - back));
            var z = Math.Max(0, b - width);
            return Math.Sqrt(x * x + z * z);
        }
    }

    internal (float MinX, float MinZ, float MaxX, float MaxZ) Bounds()
    {
        if (Shape == MechanicShape.Polygon)
            return (Vertices!.Min(v => v[0]), Vertices!.Min(v => v[1]), Vertices!.Max(v => v[0]), Vertices!.Max(v => v[1]));
        if (Shape == MechanicShape.Rectangle && Rotation == 0)
            return (X - HalfWidth, Z - Back, X + HalfWidth, Z + Front);
        var r = Shape == MechanicShape.Rectangle ? new Vector2(Math.Max(Front, Back), HalfWidth).Length() : Radius;
        return (X - r, Z - r, X + r, Z + r);
    }

    private static bool ContainsPolygon(float x, float z, float[][] vertices)
    {
        var inside = false;
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Length];
            var dx = b[0] - a[0];
            var dz = b[1] - a[1];
            var len = dx * dx + dz * dz;
            if (len > 0)
            {
                var t = Math.Clamp(((x - a[0]) * dx + (z - a[1]) * dz) / len, 0, 1);
                if (Vector2.DistanceSquared(new(x, z), new(a[0] + t * dx, a[1] + t * dz)) < 1e-8f) return true;
            }
            if ((a[1] > z) != (b[1] > z) && x < a[0] + (z - a[1]) * dx / dz) inside = !inside;
        }
        return inside;
    }
}

internal sealed record MechanicHazard(uint ActionID, string SourceID, MechanicArea Area, long ResolvesInMs, string Evidence);
