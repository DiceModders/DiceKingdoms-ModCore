using System;
using System.Collections.Generic;
using DiceKingdoms.Game;
using DiceKingdoms.Interface;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace DiceModders.ModCore;

public static partial class ModCore {
	/// <summary>
	/// How many buildings of one kind an island may have. A mod that adds a building suggests its default (<see cref="SuggestLimit"/>, e.g. the Casino:
	/// 1); a mod that lets the host change limits in the lobby (the Building limits mod) tells ModCore where its values are (<see cref="SetLimits"/>).
	/// ModCore checks the limit where the game checks a building (the same step on every computer), greys out a building at its limit in the build
	/// bar and adds "On this island: n of m" to its tooltip. Computer players' buildings are refused by the same check. An upgraded building (the
	/// upgraded Guard Tower) counts with its base building. 0 = no limit, the default of every building.
	/// </summary>
	public static class Buildings {
		private static readonly Dictionary<int, int> Suggested = new();
		private static readonly Dictionary<int, LobbyRule<bool>> SuggestedWhile = new();
		private static Func<BuildingTypeName, int?> _source;

		/// <summary>
		/// Suggests at most <paramref name="limit"/> buildings of <paramref name="type"/> (a base building) per island (0 = no limit). It is the limit
		/// whenever nothing else sets one (with the Building limits mod: its default, which the host can change). Call it in your plugin's Load, for a
		/// building your mod adds: a player without your mod would not keep to a limit on one of the game's buildings, and the computers would disagree.
		/// </summary>
		public static void SuggestLimit(BuildingTypeName type, int limit) => SuggestLimit(type, limit, null);

		/// <summary>
		/// The same, used only while <paramref name="rule"/> (the lobby rule that turns your building on) is on for everybody: ModCore uses a rule only
		/// when every player has its mod, so every computer then keeps to the limit.
		/// </summary>
		public static void SuggestLimit(BuildingTypeName type, int limit, LobbyRule<bool> rule) {
			Core.Ensure();
			Suggested[(int)type] = Math.Max(0, limit);
			if (rule != null) SuggestedWhile[(int)type] = rule; else SuggestedWhile.Remove((int)type);
			BuildingLimitHooks.Install();
			CoreLog.Info("buildings: limit for building " + (int)type + " (" + type + "): " + (limit <= 0 ? "none" : limit.ToString())
			             + (rule != null ? " while the rule '" + rule.Key + "' is on" : ""));
		}

		/// <summary>The limit per island a mod suggested for <paramref name="type"/>, or 0 (no limit) when none did (the default for a lobby rule).</summary>
		public static int SuggestedLimit(BuildingTypeName type) => Suggested.TryGetValue((int)type, out int limit) ? limit : 0;

		/// <summary>The suggested limit in force now: 0 while the rule it was suggested with is off (or not used because a player lacks its mod).</summary>
		private static int SuggestedNow(BuildingTypeName type) {
			int limit = SuggestedLimit(type);
			if (limit <= 0 || !SuggestedWhile.TryGetValue((int)type, out var rule)) return limit;
			return rule.Value && BuiltInChanges.EveryoneHas(rule) ? limit : 0;
		}

		/// <summary>
		/// Where the limits come from instead of the suggested ones: <paramref name="limits"/> gives the limit of a base building (0 = no limit), or null
		/// for the suggested one. Its answer must be the same on every computer during a match (a lobby rule's <c>Value</c> is). One source; a second
		/// call replaces the first.
		/// </summary>
		public static void SetLimits(string owner, Func<BuildingTypeName, int?> limits) {
			Core.Ensure();
			_source = limits;
			BuildingLimitHooks.Install();
			CoreLog.Info("buildings: limits set by " + (owner ?? "?"));
		}

		/// <summary>The limit per island in force for <paramref name="type"/> (for an upgraded building: its base building's). 0 = no limit.</summary>
		public static int LimitOf(BuildingTypeName type) {
			var baseType = BaseOf(type);
			int? value = null;
			try { value = _source?.Invoke(baseType); }
			catch (Exception e) { CoreLog.Once("buildings:source", "buildings: the limits source failed: " + e.Message); }
			return Math.Max(0, value ?? SuggestedNow(baseType));
		}

		/// <summary>How many buildings of <paramref name="type"/>'s kind the island has now (an upgraded building counts with its base).</summary>
		public static int CountOn(Island island, BuildingTypeName type) => BuildingLimitHooks.Count(island?.grid, BaseOf(type));

		/// <summary>The base building of an upgraded one (the upgraded Guard Tower: the Guard Tower), else <paramref name="type"/> itself.</summary>
		public static BuildingTypeName BaseOf(BuildingTypeName type) => BuildingLimitHooks.BaseOf(type);
	}
}

/// <summary>
/// The game's own checks the limits ride on (Harmony; none of them takes a struct by reference):
/// <list type="bullet">
/// <item><c>BuildData.CanBuildType</c> postfix: the check when a building is placed (<c>DoAction</c>), on every computer at the same step, with the
/// buildings placed earlier in the phase already on the island. This is the check that counts.</item>
/// <item><c>BuildData.PlaceBuildingBulk</c> prefix: walls come ten at a time and the game checks the type once per group, so a group that would go
/// over is refused when it is planned (the other computers never get it).</item>
/// <item><c>BuildData.CanAfford</c> postfix: placing with Shift held stops at the limit (the game asks CanAfford only in the build bar, its
/// tooltip and OnPlace). Not while the build bar is redrawn: there a false CanAfford would make the tooltip say resources are missing.</item>
/// <item><c>BuildData.CanAffordBulk</c> postfix: the wall placer shows red when the walls would go over.</item>
/// <item><c>BuildInterface.RefreshBuildingButtons</c> postfix: a building at its limit is greyed out (the game redraws the bar when resources or
/// technologies change and after every placement).</item>
/// <item><c>BuildInterface.TooltipText</c> postfix: "On this island: n of m".</item>
/// </list>
/// </summary>
internal static class BuildingLimitHooks {
	private static bool _installed;
	private static Dictionary<int, int> _upgrades;   // upgraded building -> base building

	internal static void Install() {
		if (_installed) return;
		_installed = true;
		try {
			var h = new Harmony("DiceModders.ModCore.buildinglimits");
			Patch(h, typeof(BuildData), "CanBuildType", nameof(CanBuildPostfix), false);
			Patch(h, typeof(BuildData), "PlaceBuildingBulk", nameof(BulkPrefix), true);
			Patch(h, typeof(BuildData), "CanAfford", nameof(CanAffordPostfix), false);
			Patch(h, typeof(BuildData), "CanAffordBulk", nameof(CanAffordBulkPostfix), false);
			Patch(h, typeof(BuildInterface), "RefreshBuildingButtons", nameof(ButtonsPrefix), true);
			Patch(h, typeof(BuildInterface), "RefreshBuildingButtons", nameof(ButtonsPostfix), false);
			Patch(h, typeof(BuildInterface), "TooltipText", nameof(TooltipPostfix), false);
			CoreLog.Info("buildings: limits are checked");
		}
		catch (Exception e) { CoreLog.Error("buildings: limit hooks failed, building limits will not work: " + e); }
	}

	private static void Patch(Harmony h, Type type, string method, string patch, bool prefix) {
		var target = AccessTools.Method(type, method);
		if (target == null) { CoreLog.Error("buildings: " + type.Name + "." + method + " not found"); return; }
		var m = new HarmonyMethod(typeof(BuildingLimitHooks), patch);
		if (prefix) h.Patch(target, prefix: m); else h.Patch(target, postfix: m);
	}

	// ------------------------------------------------------------------ kinds and counts

	/// <summary>Upgraded -> base, from the game's ReplaceBuildings technologies; null until the game's data is loaded.</summary>
	private static Dictionary<int, int> Upgrades() {
		if (_upgrades != null) return _upgrades;
		// every technology list there is (a civilization mod may bring its own), so the map is the same on every computer whatever order Unity
		// lists them in; kept only once it has an upgrade in it (before the game fills its tables it may still be empty)
		var map = new Dictionary<int, int>();
		void Take(Technology t) {
			var r = t != null ? t.TryCast<ReplaceBuildings>() : null;
			if (r != null && r.upgradedBuildingType != r.baseBuildingType) map[(int)r.upgradedBuildingType] = (int)r.baseBuildingType;
		}
		foreach (var techs in Resources.FindObjectsOfTypeAll<Technologies>()) {
			Take(techs.upgradeGuardTower);
			var values = techs.Values;
			if (values != null) foreach (var t in values) Take(t);
		}
		if (map.Count == 0) return null;
		return _upgrades = map;
	}

	internal static BuildingTypeName BaseOf(BuildingTypeName type) {
		var map = Upgrades();
		return map != null && map.TryGetValue((int)type, out int b) ? (BuildingTypeName)b : type;
	}

	/// <summary>Buildings of the base kind on the island, counted afresh (the game's check needs the exact number).</summary>
	internal static int Count(IslandGrid grid, BuildingTypeName baseType) {
		if (grid == null) return 0;
		var map = Upgrades();
		int n = 0, want = (int)baseType;
		var e = grid.Buildings.GetEnumerator();
		while (e.MoveNext()) {
			var t = e.Current.type;
			if (t == null) continue;
			int name = (int)t.name;
			if (map != null && map.TryGetValue(name, out int b)) name = b;
			if (name == want) n++;
		}
		return n;
	}

	// the screen asks often (the wall placer every frame): every kind counted once per island and frame, again when the island's building count changes
	private static IntPtr _grid;
	private static int _frame = -1, _total = -1;
	private static readonly Dictionary<int, int> Counted = new();

	private static int CountForScreen(IslandGrid grid, BuildingTypeName baseType) {
		if (grid == null) return 0;
		int total = grid.Buildings.Count;
		if (grid.Pointer != _grid || Time.frameCount != _frame || total != _total) {
			_grid = grid.Pointer; _frame = Time.frameCount; _total = total;
			Counted.Clear();
			var map = Upgrades();
			var e = grid.Buildings.GetEnumerator();
			while (e.MoveNext()) {
				var t = e.Current.type;
				if (t == null) continue;
				int name = (int)t.name;
				if (map != null && map.TryGetValue(name, out int b)) name = b;
				Counted[name] = Counted.TryGetValue(name, out int c) ? c + 1 : 1;
			}
		}
		return Counted.TryGetValue((int)baseType, out int n) ? n : 0;
	}

	// ------------------------------------------------------------------ the game's checks

	public static void CanBuildPostfix(BuildData __instance, BuildingType __0, ref bool __result) {
		try {
			if (!__result || __0 == null) return;
			int limit = ModCore.Buildings.LimitOf(__0.name);
			if (limit > 0 && Count(__instance.Island?.grid, BaseOf(__0.name)) >= limit) __result = false;
		}
		catch (Exception e) { CoreLog.Once("buildings:check", "buildings: limit check failed: " + e); }
	}

	public static bool BulkPrefix(BuildData __instance, Il2CppReferenceArray<Building> __0, ref bool __result) {
		try {
			if (__0 == null || __0.Length == 0) return true;
			var type = __0[0].type;
			if (type == null) return true;
			int limit = ModCore.Buildings.LimitOf(type.name);
			if (limit <= 0 || Count(__instance.Island?.grid, BaseOf(type.name)) + __0.Length <= limit) return true;
			__result = false;
			return false;
		}
		catch (Exception e) { CoreLog.Once("buildings:bulk", "buildings: wall limit check failed: " + e); return true; }
	}

	public static void CanAffordPostfix(BuildData __instance, BuildingType __0, ref bool __result) {
		try {
			if (!__result || __0 == null) return;
			if (_inBar && _barFrame == Time.frameCount) return;   // the build bar and its tooltips: greyed in ButtonsPostfix instead
			int limit = ModCore.Buildings.LimitOf(__0.name);
			if (limit > 0 && CountForScreen(__instance.Island?.grid, BaseOf(__0.name)) >= limit) __result = false;
		}
		catch (Exception e) { CoreLog.Once("buildings:afford", "buildings: limit check (keep placing) failed: " + e); }
	}

	// while the game redraws the build bar (its tooltips are made there too), CanAfford is left alone: false would add "Missing:" to the tooltip
	private static bool _inBar;
	private static int _barFrame = -1;

	public static void ButtonsPrefix() { _inBar = true; _barFrame = Time.frameCount; }

	public static void CanAffordBulkPostfix(BuildData __instance, BuildingType __0, int __1, ref bool __result) {
		try {
			if (!__result || __0 == null) return;
			int limit = ModCore.Buildings.LimitOf(__0.name);
			if (limit > 0 && CountForScreen(__instance.Island?.grid, BaseOf(__0.name)) + Math.Max(1, __1) > limit) __result = false;
		}
		catch (Exception e) { CoreLog.Once("buildings:affordbulk", "buildings: wall limit check failed: " + e); }
	}

	public static void ButtonsPostfix(BuildInterface __instance) {
		_inBar = false;
		try {
			var grid = __instance.Data?.Island?.grid;
			var types = __instance.buildableBuildingTypes;
			var buttons = __instance.buildingButtons;
			if (grid == null || types == null || buttons == null) return;
			for (int i = 0; i < types.Length && i < buttons.Length; i++) {
				var t = types[i];
				if (t == null) continue;
				int limit = ModCore.Buildings.LimitOf(t.name);
				if (limit <= 0 || CountForScreen(grid, BaseOf(t.name)) < limit) continue;
				var button = buttons[i]?.button;
				if (button != null) button.Interactable = false;
			}
		}
		catch (Exception e) { CoreLog.Once("buildings:bar", "buildings: greying the build bar failed: " + e); }
	}

	public static void TooltipPostfix(BuildInterface __instance, BuildingType __0, ref StructuredText __result) {
		try {
			if (__0 == null || __result == null) return;
			int limit = ModCore.Buildings.LimitOf(__0.name);
			if (limit <= 0) return;
			int count = CountForScreen(__instance.Data?.Island?.grid, BaseOf(__0.name));
			string text = ModCore.Texts.ByAssembly.TryGetValue(typeof(Core).Assembly.GetName().Name, out var set) && set.Has("buildings.limit")
				? set.Get("buildings.limit", count, limit) : "On this island: " + count + " of " + limit;
			StructuredText line = new GameText().Text("\n" + text).ToGame();
			__result = __result + line;
		}
		catch (Exception e) { CoreLog.Once("buildings:tooltip", "buildings: limit tooltip failed: " + e.Message); }
	}
}
