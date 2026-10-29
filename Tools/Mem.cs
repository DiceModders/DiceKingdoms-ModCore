using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// Raw IL2CPP object access by field offset. Offsets change with every game build: names are stable, numbers are not. Use this only for
/// members the interop assemblies do not expose (or do not expose reliably), and never for encrypted values such as resource storage.
/// </summary>
public static class Mem {
	/// <summary>The native pointer of an interop object, or zero.</summary>
	public static IntPtr PtrOf(object o) => o is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;

	// Plain (non-generic) helpers on purpose: the game's interop folder contains its own core-library stubs,
	// and a "where T : unmanaged" constraint then fails to compile (CS0656).

	/// <summary>Reads a 32-bit integer at <paramref name="offset"/>.</summary>
	public static unsafe int ReadInt(IntPtr obj, int offset) => obj == IntPtr.Zero ? 0 : *(int*)((byte*)obj + offset);

	/// <summary>Writes a 32-bit integer at <paramref name="offset"/>.</summary>
	public static unsafe void WriteInt(IntPtr obj, int offset, int value) {
		if (obj != IntPtr.Zero) *(int*)((byte*)obj + offset) = value;
	}

	/// <summary>Reads a pointer at <paramref name="offset"/>.</summary>
	public static unsafe IntPtr ReadPtr(IntPtr obj, int offset) => obj == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)((byte*)obj + offset);

	/// <summary>Converts a native IL2CPP string to a managed string ("" for null).</summary>
	public static string Str(IntPtr il2cppString) => il2cppString == IntPtr.Zero ? "" : (IL2CPP.Il2CppStringToManaged(il2cppString) ?? "");

	/// <summary>Length of a native IL2CPP array (stored at +0x18).</summary>
	public static int ArrayLength(IntPtr arr) => arr == IntPtr.Zero ? 0 : ReadInt(arr, 0x18);

	/// <summary>Pointer element <paramref name="i"/> of a native IL2CPP array of references (elements start at +0x20).</summary>
	public static IntPtr ArrayElem(IntPtr arr, int i) => ReadPtr(arr, 0x20 + i * 8);
}
