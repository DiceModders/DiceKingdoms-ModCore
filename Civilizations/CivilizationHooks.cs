using System;
using System.Collections.Generic;
using HarmonyLib;
using GameCivilization = DiceKingdoms.Interface.Civilization;
using GameTechnologies = DiceKingdoms.Game.Technologies;
using GameTechTree = DiceKingdoms.Game.TechTree;
using GamePlayerSkins = DiceKingdoms.Game.PlayerSkins;
using GamePlayer = DiceKingdoms.Game.Player;

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
			harmony.PatchAll(typeof(NextCivilizationHook));
		}
		catch (Exception e) { CoreLog.Error("civilization hooks failed: " + e); }
	}
}

/// <summary>
/// <c>Technologies.TechTree(civilization)</c> picks the tree out of the game's list with <c>First(...)</c>, which throws for a civilization the game does not know.
/// For civilizations registered with a tree provider the provider's tree is returned instead; one registered without a tree (or whose provider returned none)
/// gets the tree of the built-in civilization it is "like", so the Rules screen and a match never throw.
/// </summary>
[HarmonyPatch(typeof(GameTechnologies), "TechTree")]
internal static class TechTreeLookupHook {
	[HarmonyPrefix]
	public static bool Prefix(GameTechnologies __instance, GameCivilization civilization, ref GameTechTree __result) {
		try {
			int id = (int)civilization;
			if (id >= 0 && id <= 4) return true;
			if (CivilizationRegistry.TryGetTree(id, out var tree)) { __result = tree; return false; }
			if (CivilizationRegistry.TryGetSkinsLike(id, out int like)) { __result = __instance.TechTree((GameCivilization)like); return false; }
		}
		catch (Exception e) { CoreLog.Once("civ:tree", "tech tree hook: " + e); }
		return true;
	}
}

/// <summary>
/// Clicking a castle on the match-start screen runs <c>Player.NextCivilization</c>, which steps through the player's unlocked civilizations and wraps around.
/// Registered civilizations are added to the end of that cycle: after the last unlocked civilization comes the first registered one, then the next registered one,
/// then the game's own wrap-around takes over again. Nothing in the player's inventory is touched (replacing the inventory broke spawning, because the game also
/// picks a random civilization from it).
/// </summary>
[HarmonyPatch(typeof(GamePlayer), "NextCivilization")]
internal static class NextCivilizationHook {
	[HarmonyPrefix]
	public static bool Prefix(GamePlayer __instance) {
		try {
			if (!__instance.IsServer) return true;                 // clients ask the host through an RPC, which ends up here
			var mods = new List<ModCivilization>(CivilizationRegistry.All);
			if (mods.Count == 0) return true;
			if (__instance.StartGameReady) return true;            // the game itself ignores clicks once the player is ready
			var current = __instance.Skins;
			if (current == null) return true;
			int previous = (int)current.civilization;

			int index = -1;
			for (int i = 0; i < mods.Count; i++) if (mods[i].Id == previous) index = i;

			int next = -1;
			if (index >= 0) {
				if (index + 1 < mods.Count) next = mods[index + 1].Id;   // otherwise the game wraps to its first civilization
			}
			else {
				var unlocked = __instance.Inventory?.UnlockedCivilizations;
				if (unlocked != null && unlocked.Length > 0 && (int)unlocked[unlocked.Length - 1] == previous) next = mods[0].Id;
			}
			if (next < 0) return true;

			// the PlayerSkins constructor rejects unknown numbers, so build it for the built-in civilization this one looks like and then store the real number
			if (!CivilizationRegistry.TryGetSkinsLike(next, out int like)) return true;
			var skins = new GamePlayerSkins((GameCivilization)like, __instance.skinRegistry);
			skins.civilization = (GameCivilization)next;
			__instance.skins.Value = skins;
			CoreLog.Trace("castle click: mod civilization " + next + " (looks like " + like + ")");
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:next", "castle click hook: " + e.Message); }
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
