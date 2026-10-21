using System;
using HarmonyLib;
using GameCivilization = DiceKingdoms.Interface.Civilization;
using GameTechnologies = DiceKingdoms.Game.Technologies;
using GameTechTree = DiceKingdoms.Game.TechTree;
using GamePlayerSkins = DiceKingdoms.Game.PlayerSkins;

namespace DiceModders.ModCore;

internal static class CivilizationHooks {
	private static bool _installed;

	public static void Install() {
		if (_installed) return;
		_installed = true;
		try {
			var harmony = new Harmony("DiceModders.ModCore.civilizations");
			harmony.PatchAll(typeof(TechTreeLookupHook));
			harmony.PatchAll(typeof(PlayerSkinsCivilizationHook));
		}
		catch (Exception e) { CoreLog.Error("civilization hooks failed: " + e); }
	}
}

/// <summary>
/// <c>Technologies.TechTree(civilization)</c> picks the tree out of the game's list with <c>First(...)</c>, which throws for a civilization the game does not know.
/// For civilizations registered with a tree provider the provider's tree is returned instead.
/// </summary>
[HarmonyPatch(typeof(GameTechnologies), "TechTree")]
internal static class TechTreeLookupHook {
	[HarmonyPrefix]
	public static bool Prefix(GameCivilization civilization, ref GameTechTree __result) {
		try {
			if (CivilizationRegistry.TryGetTree((int)civilization, out var tree)) { __result = tree; return false; }
		}
		catch (Exception e) { CoreLog.Once("civ:tree", "tech tree hook: " + e); }
		return true;
	}
}

/// <summary>
/// <c>PlayerSkins.Civilization</c>'s setter derives the biome with a switch over the five built-in civilizations and throws <c>ArgumentOutOfRangeException</c> for any other value.
/// For a registered civilization the built-in one it is "like" is set first (biome, visual caches), then the civilization number itself is stored.
/// </summary>
[HarmonyPatch(typeof(GamePlayerSkins), "Civilization", MethodType.Setter)]
internal static class PlayerSkinsCivilizationHook {
	private static bool _busy;

	[HarmonyPrefix]
	public static bool Prefix(GamePlayerSkins __instance, GameCivilization value) {
		if (_busy) return true;
		try {
			int id = (int)value;
			if (id >= 0 && id <= 4) return true;
			if (!CivilizationRegistry.TryGetSkinsLike(id, out int like)) return true;
			_busy = true;
			try {
				__instance.Civilization = (GameCivilization)like;   // runs the game's own setter for the borrowed civilization
				__instance.civilization = value;                    // and remembers the real one
			}
			finally { _busy = false; }
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:skins", "civilization setter hook: " + e); }
		return true;
	}
}
