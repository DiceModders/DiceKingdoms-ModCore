using System;
using HarmonyLib;
using GameSkinRegistry = DiceKingdoms.Interface.SkinRegistry;

namespace DiceModders.ModCore;

/// <summary>
/// The moment the game's data is loaded: <c>SkinRegistry.Awake</c> runs when the main menu loads, and from then on every building, unit, ship and ragdoll
/// prefab, the tech trees and the text sprite sheets exist. A civilization builds its prefab copies here, once.
/// </summary>
internal static class GameData {
	private static bool _loaded;
	public static bool Loaded => _loaded;

	public static void Install(Harmony harmony) {
		try {
			var awake = AccessTools.Method(typeof(GameSkinRegistry), "Awake");
			if (awake == null) { CoreLog.Warn("SkinRegistry.Awake not found; GameDataLoaded will not be raised"); return; }
			harmony.Patch(awake, postfix: new HarmonyMethod(typeof(GameData), nameof(AwakePostfix)));
		}
		catch (Exception e) { CoreLog.Error("game data hook failed: " + e); }
		// ModCore set up after the main menu loaded (hot reload): the data is already there
		try { if (UnityEngine.Resources.FindObjectsOfTypeAll<GameSkinRegistry>().Length > 0) Raise(); } catch { /* Unity not ready yet */ }
	}

	public static void AwakePostfix() {
		try { Raise(); }
		catch (Exception e) { CoreLog.Once("gamedata", "game data loaded: " + e); }
	}

	private static void Raise() {
		if (_loaded) return;
		_loaded = true;
		CoreLog.Info("game data loaded");
		ModCore.Civilizations.RaiseGameDataLoaded();
	}
}
