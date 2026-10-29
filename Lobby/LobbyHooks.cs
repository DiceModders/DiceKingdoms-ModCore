using System;
using HarmonyLib;
using GameState = DiceKingdoms.Game.GameState;

namespace DiceModders.ModCore;

/// <summary>
/// The match starts in <c>GameState.StartGame</c>, which runs on every machine (the host directly, the clients through the game's own RPC), and ends in
/// <c>GameState.FullReset</c>.
/// </summary>
internal static class LobbyHooks {
	private static bool _installed;

	public static void Install() {
		if (_installed) return;
		_installed = true;
		try {
			var harmony = new Harmony("DiceModders.ModCore.lobby");
			harmony.PatchAll(typeof(MatchStartPatch));
			harmony.PatchAll(typeof(MatchEndPatch));
		}
		catch (Exception e) { CoreLog.Error("lobby hooks failed: " + e); }
	}

	[HarmonyPatch(typeof(GameState), "StartGame")]
	private static class MatchStartPatch {
		[HarmonyPostfix]
		public static void Postfix() {
			try { LobbyState.MatchStarted(); }
			catch (Exception e) { CoreLog.Once("lobby:start", "lobby: match start hook: " + e); }
		}
	}

	/// <summary>
	/// The GameState is not destroyed or disabled when a match ends: it stays and the game calls <c>FullReset</c> (back to menu, disconnect, leaving the
	/// lobby, quitting). That is the end of the match.
	/// </summary>
	[HarmonyPatch(typeof(GameState), "FullReset")]
	private static class MatchEndPatch {
		[HarmonyPostfix]
		public static void Postfix() {
			try { LobbyState.MatchEnded(); }
			catch (Exception e) { CoreLog.Once("lobby:end", "lobby: match end hook: " + e); }
			try { ModCore.Match.RaiseReset(); }
			catch (Exception e) { CoreLog.Once("match:reset", "match: reset hook: " + e); }
		}
	}
}
