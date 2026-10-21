using System;
using System.Collections.Generic;
using HarmonyLib;
using GameBuildingType = DiceKingdoms.Game.BuildingType;
using GameBuildingTypeName = DiceKingdoms.Game.BuildingTypeName;
using GameBuildingVisual = DiceKingdoms.Game.BuildingVisual;
using GameNatureTile = DiceKingdoms.Game.NatureTile;
using GameNatureVisualHub = DiceKingdoms.Game.NatureVisualHub;
using GameUnitVisual = DiceKingdoms.Interface.UnitVisual;
using GameShipVisual = DiceKingdoms.Interface.ShipVisual;
using GamePlayerSkins = DiceKingdoms.Game.PlayerSkins;

namespace DiceModders.ModCore;

/// <summary>
/// Own models for a civilization. Everything left out falls back to the look of the built-in civilization passed as <c>skinsLike</c>, so a civilization can
/// start with no models at all and replace them one by one. Each call takes the prefab that should be shown instead; the prefab is a normal game prefab
/// (for example a copy of the built-in one with other meshes and materials), see the wiki page "Making a civilization".
/// </summary>
public sealed class CivilizationSkins {
	internal readonly Dictionary<int, GameBuildingVisual> Buildings = new();
	internal readonly Dictionary<int, GameUnitVisual> Units = new();
	internal readonly Dictionary<int, GameNatureVisualHub> NatureTiles = new();
	internal GameShipVisual ShipPrefab;

	/// <summary>The building model for a building type (CASTLE is the castle on the match-start screen).</summary>
	public CivilizationSkins Building(GameBuildingTypeName type, GameBuildingVisual prefab) { Buildings[(int)type] = prefab; return this; }
	/// <summary>The model for a unit type, by its number in the game's <c>Unit.Type</c>: 0 ARCHER, 1 BOURGEOIS, 2 GENERAL, 3 KING, 4 PEASANT, 5 PRIEST, 6 SOLDIER, 7 SORCERER, 8 TEMPLAR, 9 WARLOCK.</summary>
	public CivilizationSkins Unit(int type, GameUnitVisual prefab) { Units[type] = prefab; return this; }
	/// <summary>The model for a nature tile (TREE, ROCK, SCORCHED_EARTH, LAVA, FLOWER, FISH). The prefab is a hub holding the variants the game picks from.</summary>
	public CivilizationSkins Nature(GameNatureTile tile, GameNatureVisualHub prefab) { NatureTiles[(int)tile] = prefab; return this; }
	/// <summary>The ship model.</summary>
	public CivilizationSkins Ship(GameShipVisual prefab) { ShipPrefab = prefab; return this; }
}

/// <summary>
/// <c>PlayerSkins.Get*Prefab</c> asks the skin registry for a replacement of the default prefab, keyed by the civilization number; the registry only has entries
/// for the five built-in civilizations. For a registered civilization the borrowed civilization is put in place of the real one while the game's own lookup
/// runs (prefix and finalizer in <see cref="PlayerSkinsLookHooks"/>, which also remember the real number in <see cref="PlayerSkinsLookHooks.RealId"/>); the
/// postfixes here then swap the result for the model given in <see cref="CivilizationSkins"/> if there is one. Postfixes only touch the result, so a civilization
/// without models of its own behaves exactly as before.
/// </summary>
internal static class CivilizationSkinHooks {
	public static void Install(Harmony harmony) {
		Patch(harmony, "GetBuildingVisualPrefab", nameof(BuildingPostfix));
		Patch(harmony, "GetUnitVisualPrefab", nameof(UnitPostfix));
		Patch(harmony, "GetNatureVisualHubPrefab", nameof(NaturePostfix));
		Patch(harmony, "GetShipVisualPrefab", nameof(ShipPostfix));
	}

	private static void Patch(Harmony harmony, string method, string postfix) {
		try {
			var target = AccessTools.Method(typeof(GamePlayerSkins), method);
			if (target == null) { CoreLog.Warn("PlayerSkins." + method + " not found"); return; }
			harmony.Patch(target, postfix: new HarmonyMethod(typeof(CivilizationSkinHooks), postfix));
		}
		catch (Exception e) { CoreLog.Error("PlayerSkins." + method + " postfix failed: " + e); }
	}

	private static ModCivilization Registered() {
		int id = PlayerSkinsLookHooks.RealId;
		return id < 0 ? null : CivilizationRegistry.Find(id);
	}

	public static void BuildingPostfix(GameBuildingType buildingType, ref GameBuildingVisual __result) {
		try {
			var civ = Registered();
			if (civ != null && buildingType != null && civ.Skins.Buildings.TryGetValue((int)buildingType.name, out var prefab) && prefab != null) __result = prefab;
		}
		catch (Exception e) { CoreLog.Once("civ:skin:b", "building skin hook: " + e.Message); }
	}

	public static void UnitPostfix(object unitType, ref GameUnitVisual __result) {
		try {
			var civ = Registered();
			if (civ != null && civ.Skins.Units.TryGetValue(Convert.ToInt32(unitType), out var prefab) && prefab != null) __result = prefab;
		}
		catch (Exception e) { CoreLog.Once("civ:skin:u", "unit skin hook: " + e.Message); }
	}

	public static void NaturePostfix(GameNatureTile natureTile, ref GameNatureVisualHub __result) {
		try {
			var civ = Registered();
			if (civ != null && civ.Skins.NatureTiles.TryGetValue((int)natureTile, out var prefab) && prefab != null) __result = prefab;
		}
		catch (Exception e) { CoreLog.Once("civ:skin:n", "nature skin hook: " + e.Message); }
	}

	public static void ShipPostfix(ref GameShipVisual __result) {
		try {
			var civ = Registered();
			if (civ != null && civ.Skins.ShipPrefab != null) __result = civ.Skins.ShipPrefab;
		}
		catch (Exception e) { CoreLog.Once("civ:skin:s", "ship skin hook: " + e.Message); }
	}
}
