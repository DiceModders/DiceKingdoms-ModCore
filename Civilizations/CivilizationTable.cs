using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using GamePlayerSkins = DiceKingdoms.Game.PlayerSkins;
using SkinsSerialization = DiceKingdoms.Network.PlayerSkinsSerialization;

namespace DiceModders.ModCore;

/// <summary>
/// The host's numbers for the mod civilizations, so a civilization does not need the same number on every computer.
///
/// Every computer numbers its mod civilizations itself (<see cref="CivilizationRegistry.AllocateId"/>: from the key, a taken number moves on), so the
/// same civilization can get another number on a computer that has other civilizations installed. The host writes one table into the lobby
/// ("modcore.civtable": key = number) and everybody translates at the only place a civilization number crosses the network, the player skins
/// (<c>PlayerSkinsSerialization</c>: WritePlayerSkins / ReadPlayerSkins and the NetworkedVar handlers b__5_0 / b__5_1, one byte each):
/// local number -> host number when sending, host number -> local number when receiving. Local numbers never change.
///
/// Rules that keep it safe:
/// * The table only grows: a number in it never changes while the lobby exists. A new host (the old one left) keeps it and only adds its own
///   missing civilizations, and nothing changes once the islands start to spawn (frozen with the lobby rules).
/// * A civilization can be picked only when it is in the table and every member has it (same key and mod version); see LobbyState.
/// * A number that is not in the table (or a civilization this computer lacks) falls back to the built-in civilization it looks like, so a wrong
///   number can never turn into another mod civilization.
/// * Without a table (no lobby, or a host without this ModCore) nothing is translated, as before.
/// </summary>
internal static class CivilizationTable {
	internal const string Key = "modcore.civtable";

	private static string _raw;                                   // the lobby value the maps were built from
	private static readonly Dictionary<string, int> ByKey = new(StringComparer.Ordinal);   // civilization key -> host number
	private static readonly Dictionary<int, string> ByNumber = new();
	private static bool _frozen;
	private static readonly Harmony Patches = new("DiceModders.ModCore.civtable");
	private static bool _patched;

	public static bool Active => ByKey.Count > 0;

	public static void Install() {
		if (_patched) return;
		_patched = true;
		try { Patches.PatchAll(typeof(WritePatch)); Patches.PatchAll(typeof(ReadPatch)); Patches.PatchAll(typeof(VarWritePatch)); Patches.PatchAll(typeof(VarReadPatch)); }
		catch (Exception e) { CoreLog.Error("civilization table patches failed: " + e); }
	}

	// ------------------------------------------------------------------ the table

	/// <summary>Re-reads the lobby value (cheap when it did not change). Not while frozen: the match keeps the numbers it started with.</summary>
	public static void Read(string raw) {
		if (_frozen || raw == _raw) return;
		_raw = raw;
		ByKey.Clear(); ByNumber.Clear();
		if (string.IsNullOrEmpty(raw) || !raw.StartsWith("v1", StringComparison.Ordinal)) return;
		foreach (var part in raw.Substring(2).Split(';')) {
			int eq = part.LastIndexOf('=');
			if (eq <= 0 || !int.TryParse(part.Substring(eq + 1), out int n)) continue;
			if (n < CivilizationRegistry.FirstId || n > CivilizationRegistry.LastId || ByNumber.ContainsKey(n)) continue;
			string key = part.Substring(0, eq);
			if (ByKey.ContainsKey(key)) continue;
			ByKey[key] = n; ByNumber[n] = key;
		}
		CoreLog.Trace($"civilization table: {ByKey.Count} entr{(ByKey.Count == 1 ? "y" : "ies")}");
	}

	/// <summary>Host: the existing table plus this computer's civilizations that are missing (own number when free, else the next free one).</summary>
	public static string Build(string existing) {
		Read(existing);
		var keys = new Dictionary<string, int>(ByKey, StringComparer.Ordinal);
		var used = new HashSet<int>(ByNumber.Keys);
		foreach (var c in CivilizationRegistry.All) {
			string key = Clean(c.Key);
			if (keys.ContainsKey(key)) continue;
			int n = c.Id;
			for (int tries = 0; used.Contains(n) && tries <= CivilizationRegistry.LastId - CivilizationRegistry.FirstId; tries++)
				n = n == CivilizationRegistry.LastId ? CivilizationRegistry.FirstId : n + 1;
			if (used.Contains(n)) { CoreLog.Warn($"civilization table is full: '{c.Name}' cannot be played in this lobby."); continue; }
			keys[key] = n; used.Add(n);
		}
		var sb = new StringBuilder("v1");
		foreach (var kv in keys) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(';');
		return sb.ToString();
	}

	public static void Freeze() => _frozen = true;
	public static void Unfreeze() => _frozen = false;
	public static void Clear() { _frozen = false; _raw = null; ByKey.Clear(); ByNumber.Clear(); }

	internal static string Clean(string key) => (key ?? "").Replace(';', '_').Replace('=', '_').Replace(',', '_').Replace('@', '_');

	/// <summary>The host's number of a mod civilization, or -1 when the table does not list it (or there is no table).</summary>
	public static int NumberOf(ModCivilization civ) => civ != null && ByKey.TryGetValue(Clean(civ.Key), out int n) ? n : -1;

	// ------------------------------------------------------------------ translation

	/// <summary>This computer's number -> the number sent over the network.</summary>
	public static int ToWire(int local) {
		if (!Active || local < CivilizationRegistry.FirstId) return local;
		var civ = CivilizationRegistry.Find(local);
		if (civ == null) return 0;   // removed from this computer: its old number may mean another civilization elsewhere
		int n = NumberOf(civ);
		return n >= 0 ? n : civ.SkinsLike;   // not in the table: nobody else may know it
	}

	/// <summary>A number received over the network -> this computer's number (Europe for a civilization this computer does not have).</summary>
	public static int FromWire(int wire) {
		int local = Lookup(wire);
		if (local >= 0) return local;
		CoreLog.Once("civtable:unknown:" + wire, $"civilization number {wire} from the network is not a civilization this computer has; shown as Europe.");
		return 0;
	}

	/// <summary>The host's number -> this computer's number, or -1 when this computer does not have that civilization.</summary>
	public static int Lookup(int wire) {
		if (!Active || wire < CivilizationRegistry.FirstId) return wire;
		if (ByNumber.TryGetValue(wire, out string key))
			foreach (var c in CivilizationRegistry.All) if (Clean(c.Key) == key) return c.Id;
		return -1;
	}

	/// <summary>This computer's number -> the host's number, or -1 when the table does not list this civilization.</summary>
	public static int Number(int local) {
		if (!Active || local < CivilizationRegistry.FirstId) return local;
		var civ = CivilizationRegistry.Find(local);
		return civ == null ? local : NumberOf(civ);
	}

	// ------------------------------------------------------------------ patches

	private static void SwapOut(GamePlayerSkins skins, ref int state) {
		state = -1;
		if (skins == null || !Active) return;
		int local = (int)skins.civilization, wire = ToWire(local);
		if (wire == local) return;
		state = local;
		skins.civilization = (DiceKingdoms.Interface.Civilization)wire;
	}

	private static void SwapBack(GamePlayerSkins skins, int state) {
		if (skins != null && state >= 0) skins.civilization = (DiceKingdoms.Interface.Civilization)state;
	}

	private static void Translate(SkinsSerialization serializer, ref GamePlayerSkins result) {
		if (result == null || !Active) return;
		int wire = (int)result.civilization, local = FromWire(wire);
		if (local != wire) result = new GamePlayerSkins((DiceKingdoms.Interface.Civilization)local, serializer.skinRegistry);
	}

	[HarmonyPatch(typeof(SkinsSerialization), nameof(SkinsSerialization.WritePlayerSkins))]
	private static class WritePatch {
		[HarmonyPrefix] public static void Prefix(GamePlayerSkins __1, ref int __state) { try { SwapOut(__1, ref __state); } catch (Exception e) { CoreLog.Once("civtable:w", "civilization table (write): " + e.Message); } }
		[HarmonyPostfix] public static void Postfix(GamePlayerSkins __1, int __state) { SwapBack(__1, __state); }
	}

	[HarmonyPatch(typeof(SkinsSerialization), nameof(SkinsSerialization.ReadPlayerSkins))]
	private static class ReadPatch {
		[HarmonyPostfix] public static void Postfix(SkinsSerialization __instance, ref GamePlayerSkins __result) { try { Translate(__instance, ref __result); } catch (Exception e) { CoreLog.Once("civtable:r", "civilization table (read): " + e.Message); } }
	}

	[HarmonyPatch(typeof(SkinsSerialization), nameof(SkinsSerialization._OnEnable_b__5_0))]
	private static class VarWritePatch {
		[HarmonyPrefix] public static void Prefix(GamePlayerSkins __1, ref int __state) { try { SwapOut(__1, ref __state); } catch (Exception e) { CoreLog.Once("civtable:vw", "civilization table (var write): " + e.Message); } }
		[HarmonyPostfix] public static void Postfix(GamePlayerSkins __1, int __state) { SwapBack(__1, __state); }
	}

	[HarmonyPatch(typeof(SkinsSerialization), nameof(SkinsSerialization._OnEnable_b__5_1))]
	private static class VarReadPatch {
		[HarmonyPostfix] public static void Postfix(SkinsSerialization __instance, ref GamePlayerSkins __result) { try { Translate(__instance, ref __result); } catch (Exception e) { CoreLog.Once("civtable:vr", "civilization table (var read): " + e.Message); } }
	}
}
