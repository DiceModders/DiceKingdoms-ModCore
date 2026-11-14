using System;
using HarmonyLib;
using GameState = DiceKingdoms.Game.GameState;
using NetworkingManager = MLAPI.NetworkingManager;

namespace DiceModders.ModCore;

/// <summary>
/// Keeps players who cannot play the running match out of it. Lobby rules that change the simulation are fixed for the whole match; somebody who
/// leaves, removes or changes a mod and joins again would play other rules than everybody else and fall out of sync, or not even load the islands.
///
/// How the game lets players back in: the host's connection approval (<c>GameState.StartNetworking</c> lambda b__69_5, async) approves a connection
/// during a match only when a Player with that <c>InitialOwnerId</c> exists; then it calls <c>Approve</c> (-> MLAPI's callback -> 
/// <c>NetworkingManager.HandleApproval</c>) and <c>OnRejoin(player)</c>, which gives the player and island objects back to that client and sends the
/// match state. The guard hands the lambda client id 0 for a refused player, so the game itself takes its "no such player" branch: Approve(false),
/// no OnRejoin, and MLAPI disconnects the client (the callback keeps the real id).
/// The transport's client id is the Steam id.
/// </summary>
internal static class MatchGuard {
	private static bool _installed;

	public static void Install() {
		if (_installed) return;
		_installed = true;
		try {
			var harmony = new Harmony("DiceModders.ModCore.guard");
			harmony.PatchAll(typeof(GameApprovalPatch));
			// NOT NetworkingManager.HandleApproval: it takes Nullable<ulong/Vector3/Quaternion> arguments, and a Harmony patch on it broke every
			// approval through the interop (the host disconnected each joining player with App_Min, then OnJoin threw KeyNotFoundException).
		}
		catch (Exception e) { CoreLog.Error("match guard failed: " + e); }
	}

	/// <summary>
	/// What this match uses that the joining player lacks (a lobby rule, or a mod civilization somebody plays), or null when they may join. Their game
	/// would play other rules, or show the civilization as another one.
	/// </summary>
	private static string Missing(ulong steamId) {
		if (!LobbyState.RulesFrozen || !LobbyState.IsHost || steamId == 0 || steamId == LobbyState.LocalSteamId) return null;
		var rule = LobbyRuleRegistry.FirstInUseMissing(steamId);
		if (rule != null) return $"'{rule.Title}' from {rule.ModName} {rule.ModVersion}";
		foreach (var player in UnityEngine.Object.FindObjectsOfType<DiceKingdoms.Game.Player>()) {
			var skins = player?.Skins;
			if (skins == null) continue;
			var civ = CivilizationRegistry.Find((int)skins.civilization);
			if (civ != null && !LobbyState.MemberHasCivilization(steamId, civ)) return $"the civilization '{civ.Name}' ({civ.ModVersion})";
		}
		return null;
	}

	private static void Report(ulong steamId, string missing) {
		string name = steamId.ToString();
		foreach (var m in LobbyState.Members) if (m.SteamId == steamId) { name = m.Name; break; }
		CoreLog.Warn($"{name} cannot join this match: it uses {missing}, which they do not have.");
	}

	[HarmonyPatch(typeof(GameState), nameof(GameState._StartNetworking_b__69_5))]
	private static class GameApprovalPatch {
		[HarmonyPrefix]
		public static void Prefix(ref ulong clientId) {
			try {
				var missing = Missing(clientId);
				if (missing == null) return;
				Report(clientId, missing);
				clientId = 0;   // no player has this id: the game refuses and skips its rejoin
			}
			catch (Exception e) { CoreLog.Once("guard:game", "match guard: " + e); }
		}
	}

}
