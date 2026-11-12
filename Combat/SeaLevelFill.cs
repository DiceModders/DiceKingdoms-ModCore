using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DiceModders.ModCore;

/// <summary>
/// Island generation code that does not survive a big island grid.
///
/// <c>IslandGeneration.GenerateJob.Execute</c> fills a layer from the map corner with its local function <c>Allow</c> (IL2CPP 1805FA270): every tile
/// gets the lowest ground level a path from the open sea can reach it through. <c>Allow</c> calls itself for the four neighbours of every tile it
/// lowers, so the depth of the recursion grows with the sea area. The job runs as Burst code on Unity's job threads, whose stack is 1 MB (about 9,300
/// calls of this function): grids up to 119 fit, 121 and above overflow the stack and close the game when the islands are generated.
///
/// The job runs the Burst copy (lib_burst_generated.dll, one AVX2 and one SSE2 copy), not the IL2CPP method, so Harmony cannot reach it. The start of
/// each copy is replaced with a jump to <see cref="Fill"/>: the same fill with a list of tiles still to visit instead of recursion. The result does not
/// depend on the visiting order (every tile ends with the lowest level reachable from the start), so islands are exactly the game's. Always used.
/// </summary>
internal static unsafe class SeaLevelFill
{
    /// <summary>The game's <c>CombatGrid.UnsafeLayer&lt;GroundTile&gt;</c>: an UnsafeList of tiles (row by row) and the size.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct Layer
    {
        [FieldOffset(0x00)] public int* Tiles;
        [FieldOffset(0x20)] public int Width;
        [FieldOffset(0x24)] public int Height;
    }

    // First 32 bytes of the Burst copies of Allow. Arguments: position (int2) in xmm0, tile in edx, ground layer in r8, allowed layer in r9.
    private static readonly byte[][] Signatures =
    {
        Bytes("55 41 57 41 56 56 57 53 48 83 ec 38 48 8d 6c 24 30 c5 f9 7f 75 f0 c5 f9 7e c1 85 c9 0f 88 72 01"),   // AVX2
        Bytes("55 41 56 56 57 53 48 83 ec 30 48 8d 6c 24 30 0f 29 75 f0 66 0f 6f f0 66 0f 70 c0 50 66 0f 50 c0"),   // SSE2
    };

    /// <summary>True when the game's recursive fill has been replaced, so grids above 119 tiles are safe to generate.</summary>
    public static bool Installed { get; private set; }

    [ThreadStatic] private static long[] _todo;

    public static void Install()
    {
        try
        {
            IntPtr module = GetModuleHandleW("lib_burst_generated.dll");
            if (module == IntPtr.Zero)
                module = LoadLibraryW(Path.Combine(UnityEngine.Application.dataPath, "Plugins", "x86_64", "lib_burst_generated.dll"));
            if (module == IntPtr.Zero) { CoreLog.Warn("sea level fill: Burst library not found (island grids stay limited to 119)"); return; }

            byte* start = TextSection(module, out int size);
            IntPtr target = (IntPtr)(delegate* unmanaged<long, int, Layer*, Layer*, void>)&Fill;
            int patched = 0;
            foreach (var signature in Signatures)
            {
                byte* at = Find(start, size, signature);
                if (at == null) continue;
                Jump(at, target);
                patched++;
            }
            Installed = patched > 0;
            if (Installed) CoreLog.Info("sea level fill: replaced " + patched + " Burst copies");
            else CoreLog.Warn("sea level fill: Burst code not found, game version changed? (island grids stay limited to 119)");
        }
        catch (Exception e) { CoreLog.Error("sea level fill: " + e); }
    }

    // GroundTile levels as Allow compares them (GenerateJob.TileAltitude): 0 lowest, 3 middle, 2 highest
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Level(int tile) => tile == 0 ? -2 : tile == 3 ? -1 : 0;

    [UnmanagedCallersOnly]
    private static void Fill(long position, int tile, Layer* ground, Layer* allowed)
    {
        try
        {
            int w = allowed->Width, h = allowed->Height;
            int gw = ground->Width, gh = ground->Height;
            var todo = _todo ??= new long[4096];
            int count = 0;
            int x0 = (int)position, y0 = (int)(position >> 32);
            if (x0 < 0 || y0 < 0 || x0 >= w || y0 >= h) return;
            todo[count++] = Pack(x0, y0, tile);

            while (count > 0)
            {
                long item = todo[--count];
                int x = (int)(item & 0xFFFF), y = (int)((item >> 16) & 0xFFFF), from = (int)(item >> 32);

                int here = x < gw && y < gh ? ground->Tiles[y * gw + x] : 0;
                int value = Level(here) < Level(from) ? from : here;          // GenerateJob.Max
                int* cell = allowed->Tiles + (y * w + x);
                if (Level(value) >= Level(*cell)) continue;
                *cell = value;

                if (count + 4 > todo.Length) { Array.Resize(ref todo, todo.Length * 2); _todo = todo; }
                if (y + 1 < h) todo[count++] = Pack(x, y + 1, value);
                if (x + 1 < w) todo[count++] = Pack(x + 1, y, value);
                if (y > 0)     todo[count++] = Pack(x, y - 1, value);
                if (x > 0)     todo[count++] = Pack(x - 1, y, value);
            }
        }
        catch { /* never let an exception reach Burst code */ }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(int x, int y, int tile) => (uint)x | ((long)(uint)y << 16) | ((long)tile << 32);

    // ---- native plumbing ----

    /// <summary>Overwrites the function start with <c>movq rcx, xmm0; mov rax, target; jmp rax</c> (the int2 argument moves to where a C# long arrives).</summary>
    private static void Jump(byte* at, IntPtr target)
    {
        byte[] code =
        {
            0x66, 0x48, 0x0F, 0x7E, 0xC1,   // movq rcx, xmm0
            0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,   // mov rax, imm64
            0xFF, 0xE0,                     // jmp rax
        };
        BitConverter.GetBytes((long)target).CopyTo(code, 7);
        if (!VirtualProtect((IntPtr)at, (UIntPtr)code.Length, 0x40, out uint old)) throw new InvalidOperationException("VirtualProtect failed");
        Marshal.Copy(code, 0, (IntPtr)at, code.Length);
        VirtualProtect((IntPtr)at, (UIntPtr)code.Length, old, out _);
        FlushInstructionCache(GetCurrentProcess(), (IntPtr)at, (UIntPtr)code.Length);
    }

    private static byte* TextSection(IntPtr module, out int size)
    {
        byte* b = (byte*)module;
        int pe = *(int*)(b + 0x3C);
        int sections = *(ushort*)(b + pe + 6);
        int optional = *(ushort*)(b + pe + 20);
        byte* section = b + pe + 24 + optional;
        for (int i = 0; i < sections; i++, section += 40)
            if (section[0] == '.' && section[1] == 't' && section[2] == 'e' && section[3] == 'x' && section[4] == 't')
            {
                size = *(int*)(section + 8);
                return b + *(int*)(section + 12);
            }
        throw new InvalidOperationException("no .text section");
    }

    private static byte* Find(byte* start, int size, byte[] signature)
    {
        byte first = signature[0];
        for (int i = 0, end = size - signature.Length; i <= end; i++)
        {
            if (start[i] != first) continue;
            int j = 1;
            while (j < signature.Length && start[i + j] == signature[j]) j++;
            if (j == signature.Length) return start + i;
        }
        return null;
    }

    private static byte[] Bytes(string hex)
    {
        var parts = hex.Split(' ');
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) bytes[i] = Convert.ToByte(parts[i], 16);
        return bytes;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32")] private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32")] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
}
