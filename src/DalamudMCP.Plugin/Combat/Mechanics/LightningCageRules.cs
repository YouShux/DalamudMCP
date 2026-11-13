using System.Numerics;

namespace DalamudMCP.Plugin.Tools;

// M4S 格型数据来自 BossmodReborn 的 ElectropeEdge.cs（BSD-3-Clause）。
internal static class LightningCageRules
{
    internal const uint Duty = 992, Edge = 38341, WitchgleamCast = 38789, Witchgleam = 38790,
        Anchor = 38350, CellCast = 38351, Spark2 = 38347, Spark3 = 38348, Charge = 3999;

    private static readonly (int[] Danger, int Idle, int[] Two, int[] Three)[] layouts =
    [
        ([0,1,3,4,10,18,24,26,28,33,34,35], 2, [8,12], [32,36]),
        ([1,4,8,12,16,17,18,19,24,28,33,36], 20, [3,35], [0,32]),
        ([1,2,3,8,10,12,18,26,32,33,35,36], 34, [24,28], [0,4]),
        ([0,3,8,12,17,18,19,20,24,28,32,35], 16, [1,33], [4,36]),
    ];

    internal static int? CellAt(Vector2 position)
    {
        if (!(position.X is >= 80 and < 120 && position.Y is >= 80 and < 120)) return null;
        return (int)((position.Y - 80) / 8) * 8 + (int)((position.X - 80) / 8);
    }

    internal static MechanicArea CellArea(int i) =>
        new(MechanicShape.Rectangle, 84 + 8 * (i & 7), 84 + 8 * (i >> 3), Front: 4, Back: 4, HalfWidth: 4);

    internal static int[]? SafeCells(int[] danger, int? order, int? gleams, int? sparks)
    {
        if (order is not (1 or 2) || gleams is null || sparks is null or < 0 or >= 12) return null;
        var nextOrder = sparks < 4 ? 1 : sparks < 8 ? 2 : 3;
        foreach (var layout in layouts)
        {
            if (!layout.Danger.AsSpan().SequenceEqual(danger)) continue;
            return order != nextOrder ? [layout.Idle] : (gleams + nextOrder) switch
            {
                3 => layout.Two,
                4 => layout.Three,
                _ => null,
            };
        }
        return null;
    }
}
