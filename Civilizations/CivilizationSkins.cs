using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
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

	internal readonly List<(int x, int y, Color c)> GroundCells = new(), NatureCells = new();
	internal bool HasIslandColours => GroundCells.Count > 0 || NatureCells.Count > 0;

	/// <summary>
	/// Recolours a cell of the game's 8x8 colour palette (x from the left, y from the bottom) on this civilization's island ground: for example the
	/// borrowed biome's grass cells. The game picks the cells per biome (PLAINS (1,1)/(4,0), DESERT (4,1)/(4,2), SNOW (4,3)/(4,4), MEDITERRANEAN (5,3)/(5,4),
	/// JUNGLE (6,3)/(6,4); cliffs (0,2), desert cliffs (0,3)). Visual and local.
	/// </summary>
	public CivilizationSkins GroundColour(int x, int y, Color colour) { GroundCells.RemoveAll(c => c.x == x && c.y == y); GroundCells.Add((x, y, colour)); return this; }
	/// <summary>The same for the mountains (inner cliffs), trees and flowers on this civilization's islands (snow models: (2,2) snow, (0,2) cliff body).</summary>
	public CivilizationSkins NatureColour(int x, int y, Color colour) { NatureCells.RemoveAll(c => c.x == x && c.y == y); NatureCells.Add((x, y, colour)); return this; }

	internal sealed class ModelEntry {
		public Mesh Mesh;
		public Vector3? FlagPosition;
		public float FlagYaw;
		public Sprite[] Icons;
		public GameBuildingVisual Built;   // the prepared prefab, made on first use
	}
	internal readonly Dictionary<int, ModelEntry> BuildingModels = new();

	/// <summary>
	/// A building that looks like the <c>skinsLike</c> civilization's one but with your own body mesh. On first use ModCore copies that building's prefab,
	/// puts <paramref name="mesh"/> into it (normal and damaged look, one LOD group; the others are blanked so nothing overlaps) and uses the copy for this
	/// civilization everywhere the game shows the building. The game's materials stay, so player colours, tints and the burnt look keep working; give the
	/// mesh UVs on the game's 8x8 colour palette. Footprint, rules and effects (fire anchors, inhabitants) stay those of the original building. The mesh is
	/// in the prefab's own space; <paramref name="flagPosition"/> / <paramref name="flagYaw"/> move the building's big flag (castles) to stand on your model.
	/// A <see cref="Building"/> prefab for the same type wins over this. Build the mesh with <see cref="Tools.Meshes"/>.
	/// <para><paramref name="icons"/>: the building's 2D pictures, one per player colour in the game's colour order (12; see
	/// <see cref="Tools.Sprites.LoadPlayerColorSet"/>). The game shows them in the lobby, the player summary, the scoreboard, the end screen, the build
	/// menu, the Rules screen and in texts. Without them the look-alike building's pictures stay.</para>
	/// </summary>
	public CivilizationSkins BuildingModel(GameBuildingTypeName type, Mesh mesh, Vector3? flagPosition = null, float flagYaw = 0f, Sprite[] icons = null) {
		if (mesh == null) { BuildingModels.Remove((int)type); return this; }
		BuildingModels[(int)type] = new ModelEntry { Mesh = mesh, FlagPosition = flagPosition, FlagYaw = flagYaw, Icons = icons };
		return this;
	}
}

/// <summary>Makes the prefab copies for <see cref="CivilizationSkins.BuildingModel"/>. They live under one inactive, persistent object.</summary>
internal static class CivilizationModels {
	private static readonly string[] CivTokens = { "europe", "middle-east", "viking", "ancient", "maya" };   // mesh names: "<building>-<civ>-mesh_LOD0"

	public static GameBuildingVisual Get(string civName, int look, GameBuildingTypeName type, CivilizationSkins.ModelEntry model, GameBuildingVisual basePrefab) {
		if (model.Built != null) return model.Built;   // Unity null check: rebuilt if it was ever destroyed
		if (basePrefab == null) return null;
		var copy = Tools.Prefabs.Copy(basePrefab);   // keeps the game's naming ("Castle Viking"), tools and pooling expect it

		string own = look >= 0 && look < CivTokens.Length ? CivTokens[look] : "europe";
		var filters = copy.GetComponentsInChildren<MeshFilter>(true);
		string ownGroup = null, firstGroup = null;
		foreach (var mf in filters) {
			string group = GroupOf(mf.gameObject.name);
			if (group == null) continue;
			firstGroup ??= group;
			if (ownGroup == null && group.EndsWith("-" + own, StringComparison.Ordinal)) ownGroup = group;
		}
		ownGroup ??= firstGroup;

		int filled = 0, blanked = 0;
		bool flag = false;
		foreach (var mf in filters) {
			string name = mf.gameObject.name;
			string group = GroupOf(name);
			if (group != null) {
				// Some prefabs hold two LOD groups (Castle Viking has castle-europe and castle-viking parts): fill one, blank the rest, or the copies z-fight.
				if (group == ownGroup) { mf.sharedMesh = model.Mesh; filled++; }
				else { mf.sharedMesh = Tools.Meshes.Empty; blanked++; }
			}
			else if (name == "flag-big" && model.FlagPosition.HasValue) {
				mf.transform.localPosition = model.FlagPosition.Value;
				mf.transform.localRotation = Quaternion.Euler(0f, model.FlagYaw, 0f);
				flag = true;
			}
		}
		bool icons = SetIcons(copy, model.Icons, civName, type);
		model.Built = copy;
		CoreLog.Info("civilization '" + civName + "': own model for " + type + " made from '" + basePrefab.name + "' (meshes " + filled + ", blanked " + blanked + ", flag moved " + flag + ", icons " + icons + ")");
		return copy;
	}

	/// <summary>
	/// <c>BuildingVisual.Icon(color)</c> returns <c>icons[color.Index]</c> with no fallback, so the array must keep the original's length and order
	/// (one per player colour). Our copy gets its own array; the look-alike's prefab is never touched.
	/// </summary>
	private static bool SetIcons(GameBuildingVisual copy, Sprite[] icons, string civName, GameBuildingTypeName type) {
		if (icons == null) return false;
		int needed = copy.icons != null ? copy.icons.Length : 0;
		if (icons.Length != needed || Array.IndexOf(icons, null) >= 0) {
			CoreLog.Warn("civilization '" + civName + "': " + type + " icons ignored, need " + needed + " sprites (one per player colour), got " + icons.Length);
			return false;
		}
		copy.icons = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Sprite>(icons);
		return true;
	}

	/// <summary>"castle-viking-mesh_LOD1" -> "castle-viking"; null for other objects.</summary>
	private static string GroupOf(string objectName) {
		int i = objectName.IndexOf("-mesh_LOD", StringComparison.Ordinal);
		return i > 0 ? objectName.Substring(0, i) : null;
	}
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

	/// <summary>
	/// Whose models apply to this lookup: a registered civilization (its real number is in <see cref="PlayerSkinsLookHooks.RealId"/> while its borrowed one
	/// runs the game's lookup) or a built-in civilization a mod reskinned (<see cref="ModCore.Civilizations.Reskin"/>). A registered civilization never
	/// inherits the reskin of the civilization it looks like: what it leaves out shows the game's original look.
	/// </summary>
	private static bool Resolve(GamePlayerSkins skins, out CivilizationSkins result, out string name, out int look) {
		result = null; name = null; look = 0;
		int real = PlayerSkinsLookHooks.RealId;
		if (real >= 0) {
			var civ = CivilizationRegistry.Find(real);
			if (civ == null) return false;
			result = civ.Skins; name = civ.Name; look = civ.SkinsLike;
			return true;
		}
		if (skins == null) return false;
		int id = (int)skins.civilization;
		if (id < 0 || id >= CivilizationRegistry.BuiltInSkins.Length || CivilizationRegistry.BuiltInSkins[id] == null) return false;
		result = CivilizationRegistry.BuiltInSkins[id]; name = ((DiceKingdoms.Interface.Civilization)id).ToString(); look = id;
		return true;
	}

	public static void BuildingPostfix(GamePlayerSkins __instance, GameBuildingType buildingType, ref GameBuildingVisual __result) {
		try {
			if (buildingType == null || !Resolve(__instance, out var skins, out var name, out int look)) return;
			int type = (int)buildingType.name;
			if (skins.Buildings.TryGetValue(type, out var prefab) && prefab != null) { __result = prefab; return; }
			if (skins.BuildingModels.TryGetValue(type, out var model)) {
				var built = CivilizationModels.Get(name, look, buildingType.name, model, __result);   // __result is the look-alike civilization's prefab
				if (built != null) __result = built;
			}
		}
		catch (Exception e) { CoreLog.Once("civ:skin:b", "building skin hook: " + e.Message); }
	}

	public static void UnitPostfix(GamePlayerSkins __instance, object unitType, ref GameUnitVisual __result) {
		try {
			if (Resolve(__instance, out var skins, out _, out _) && skins.Units.TryGetValue(Convert.ToInt32(unitType), out var prefab) && prefab != null) __result = prefab;
		}
		catch (Exception e) { CoreLog.Once("civ:skin:u", "unit skin hook: " + e.Message); }
	}

	public static void NaturePostfix(GamePlayerSkins __instance, GameNatureTile natureTile, ref GameNatureVisualHub __result) {
		try {
			if (Resolve(__instance, out var skins, out _, out _) && skins.NatureTiles.TryGetValue((int)natureTile, out var prefab) && prefab != null) __result = prefab;
		}
		catch (Exception e) { CoreLog.Once("civ:skin:n", "nature skin hook: " + e.Message); }
	}

	public static void ShipPostfix(GamePlayerSkins __instance, ref GameShipVisual __result) {
		try {
			if (Resolve(__instance, out var skins, out _, out _) && skins.ShipPrefab != null) __result = skins.ShipPrefab;
		}
		catch (Exception e) { CoreLog.Once("civ:skin:s", "ship skin hook: " + e.Message); }
	}
}