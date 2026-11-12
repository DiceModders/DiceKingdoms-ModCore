using System;
using HarmonyLib;
using Unity.Mathematics;

namespace DiceModders.ModCore;

using CombatPhase = DiceKingdoms.Game.CombatPhase;
using GameIsland = DiceKingdoms.Game.Island;
using GroundTile = DiceKingdoms.Game.GroundTile;
using GroundTiles = DiceKingdoms.Game.GroundTileExtension;

/// <summary>
/// Game code that does not survive a big island grid.
///
/// <c>CombatPhase.ComputeConnectedComponents(island, layer)</c> (18063DD90; called by CombatInterface.OnEnable and CombatPhase.CreateDeployments when
/// combat starts) labels every tile with the first tile of its walkable region. Its helper <c>Visit</c> (18063FD80) calls itself once per walkable
/// neighbour, so the depth of the recursion is the size of the largest walkable region; on big islands that overflows the stack and the game closes.
///
/// This is the same algorithm with a list of tiles still to visit: the same labels in the same order. Passable (the game's <c>Passable|1_1</c>) =
/// <c>GroundTileExtension.IsPassable(ground)</c>, or a building, or building remains on the tile; outside the grid nothing is passable (the game's
/// layer accessors return the default value there).
///
/// The layers are read straight from memory (<c>IslandGrid.Layer&lt;T&gt;</c>: T[] at +0x10, width +0x18, height +0x1C; index y * width + x; array
/// elements from +0x20, length at +0x18) instead of through the interop accessors: <c>IslandGrid.BuildingAt</c> / <c>RemainsAt</c> return a
/// <c>Nullable&lt;Building.Id&gt;</c> that came back as null through the interop, so the old version threw on the first water tile and handed every
/// island to the game's recursive code. Reading memory is also far faster than ~5 interop calls per tile. Always used.
/// </summary>
[HarmonyPatch(typeof(CombatPhase), nameof(CombatPhase.ComputeConnectedComponents))]
internal static unsafe class WalkableRegions
{
    // IslandGrid fields (game build of 2024-04): see the wiki's island grid page ("Tile layers in memory")
    private const int BuildingsLayer = 0x108, RemainsLayer = 0x118, GroundLayer = 0x138;

    private static int[] _todo = new int[4096];
    private static bool[] _passableGround;

    private readonly struct Raw
    {
        public readonly byte* Data;
        public readonly int Width, Height;
        public Raw(IntPtr layer)
        {
            IntPtr array = *(IntPtr*)((byte*)layer + 0x10);
            Width = *(int*)((byte*)layer + 0x18);
            Height = *(int*)((byte*)layer + 0x1C);
            int length = array == IntPtr.Zero ? 0 : *(int*)((byte*)array + 0x18);
            if (array == IntPtr.Zero || Width <= 0 || Height <= 0 || length < Width * Height) throw new InvalidOperationException("unexpected layer layout");
            Data = (byte*)array + 0x20;
        }
    }

    [HarmonyPrefix] public static bool Prefix(GameIsland island, DiceKingdoms.Game.IslandGrid.Layer<int2> connectedComponents)
    {
        if (island == null || connectedComponents == null)
        {
            CoreLog.Once("regions-null", "walkable regions: called with " + (island == null ? "no island" : "no layer") + " (the game's own code runs instead)");
            return true;
        }
        try
        {
            var grid = island.grid;
            if (grid == null) return true;
            byte* g = (byte*)grid.Pointer;
            var ground = new Raw(*(IntPtr*)(g + GroundLayer));
            var buildings = new Raw(*(IntPtr*)(g + BuildingsLayer));
            var remains = new Raw(*(IntPtr*)(g + RemainsLayer));
            var cc = new Raw(connectedComponents.Pointer);
            int w = cc.Width, h = cc.Height;
            if (ground.Width != w || ground.Height != h || buildings.Width != w || remains.Width != w) throw new InvalidOperationException("layer sizes differ");

            var passableGround = PassableGround();
            int n = w * h;
            int2* labels = (int2*)cc.Data;
            for (int i = 0; i < n; i++) labels[i] = new int2(-1, -1);

            bool Passable(int i)
            {
                int tile = ((int*)ground.Data)[i];
                if (tile >= 0 && tile < passableGround.Length && passableGround[tile]) return true;
                return buildings.Data[i * 8] != 0 || remains.Data[i * 8] != 0;   // Nullable<Building.Id>: hasValue, then the int
            }

            // the game walks Layer.Positions() (x fastest) and visits each unseen tile; Visit labels the tile and, if it is passable, its region
            for (int start = 0; start < n; start++)
            {
                if (labels[start].x != -1 || labels[start].y != -1) continue;
                var label = new int2(start % w, start / w);
                labels[start] = label;
                if (!Passable(start)) continue;
                int count = 0;
                _todo[count++] = start;
                while (count > 0)
                {
                    int at = _todo[--count];
                    int x = at % w, y = at / w;
                    if (count + 4 > _todo.Length) Array.Resize(ref _todo, _todo.Length * 2);
                    if (y + 1 < h) Push(at + w);
                    if (x + 1 < w) Push(at + 1);
                    if (y > 0) Push(at - w);
                    if (x > 0) Push(at - 1);

                    void Push(int next)
                    {
                        if (labels[next].x != -1 || labels[next].y != -1 || !Passable(next)) return;
                        labels[next] = label;
                        _todo[count++] = next;
                    }
                }
            }
            *((byte*)connectedComponents.Pointer + 0x20) = 1;   // Layer.modified, as the game's set_Item leaves it
            return false;
        }
        catch (Exception e)
        {
            CoreLog.Once("regions", "walkable regions: " + e + " (the game's own code runs instead)");
            return true;
        }
    }

    /// <summary>GroundTileExtension.IsPassable for every GroundTile value, asked once.</summary>
    private static bool[] PassableGround()
    {
        if (_passableGround != null) return _passableGround;
        var table = new bool[16];
        for (int i = 0; i < table.Length; i++)
        {
            try { table[i] = GroundTiles.IsPassable((GroundTile)i); }
            catch (Exception) { table[i] = false; }
        }
        return _passableGround = table;
    }
}
