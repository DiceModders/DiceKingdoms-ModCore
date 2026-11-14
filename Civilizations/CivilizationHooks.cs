using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using GameCivilization = DiceKingdoms.Interface.Civilization;
using GameTechnologies = DiceKingdoms.Game.Technologies;
using GameTechTree = DiceKingdoms.Game.TechTree;
using GamePlayerSkins = DiceKingdoms.Game.PlayerSkins;
using GamePlayer = DiceKingdoms.Game.Player;
using GameAIPlay = DiceKingdoms.Game.AIPlay;
using GameStatistics = DiceKingdoms.Game.Statistics;
using GameTechnology = DiceKingdoms.Game.Technology;
using GameSkinsSerialization = DiceKingdoms.Network.PlayerSkinsSerialization;

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
			harmony.PatchAll(typeof(PlayerSkinsCloneHook));
			harmony.PatchAll(typeof(NextCivilizationHook));
			harmony.PatchAll(typeof(AiBuildOrderHook));
			harmony.PatchAll(typeof(BuildOrdersHook));
			harmony.PatchAll(typeof(StatisticsTechTreeHook));
			harmony.PatchAll(typeof(StatisticsLearnableHook));
			PlayerSkinsLookHooks.Install(harmony);
			CivilizationText.Install(harmony);
			IslandColours.Install(harmony);
			GameData.Install(harmony);   // last: may raise GameDataLoaded at once (late setup)
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
			if (id >= 0 && id <= 4) {
				if (BuiltInChanges.TryGetTree(id, out var replaced)) { __result = replaced; return false; }   // a mod's tree, only when safe (see BuiltInChanges)
				return true;
			}
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
	/// <summary>The player's real ready flag (the synced field): the StartGameReady getter can be patched by other mods, e.g. a "force ready" cheat.</summary>
	private static bool IsReady(GamePlayer p) { try { return p.startGameReady != null && p.startGameReady.Value; } catch { return false; } }

	[HarmonyPrefix]
	public static bool Prefix(GamePlayer __instance) {
		try {
			CoreLog.Info("civilization cycle: castle clicked (server " + __instance.IsServer + ", ready " + IsReady(__instance) + ", mod civilizations " + CivilizationRegistry.All.Count + ")");
			if (!__instance.IsServer) return true;                 // clients ask the host through an RPC, which ends up here
			var all = new List<ModCivilization>(CivilizationRegistry.All);
			if (all.Count == 0) return true;
			// A member without the mod would throw when the game sends it the number, so only civilizations everybody has are offered.
			var mods = all.FindAll(c => LobbyState.EveryoneHasCivilization(c));
			if (mods.Count < all.Count)
				CoreLog.Once("civ:missing:" + mods.Count + "/" + all.Count, $"civilization cycle: {all.Count - mods.Count} mod civilization(s) skipped, a lobby member does not have them " +
				                                                             "in the same version: " + string.Join(", ", all.FindAll(c => !mods.Contains(c)).ConvertAll(c => c.Name)));
			// The game's NextCivilization only refuses once the match has started; it does NOT look at the ready flag. Clients are always
			// "ready" on the host, so a ready test here kept every client from reaching the mod civilizations.
			var gs = __instance.GameState;
			if (gs != null && gs.GameStarted) return true;
			var current = __instance.Skins;
			if (current == null) return true;
			int previous = (int)current.civilization;

			int index = -1;
			for (int i = 0; i < mods.Count; i++) if (mods[i].Id == previous) index = i;
			if (mods.Count == 0) return true;   // nothing to add for this lobby: the game's own cycle
			if (index < 0 && all.Exists(c => c.Id == previous)) { CoreLog.Info("civilization cycle: on a mod civilization somebody lacks, back to the game's cycle"); return true; }

			int next = -1;
			if (index >= 0) {
				if (index + 1 < mods.Count) next = mods[index + 1].Id;   // otherwise the game wraps to its first civilization
			}
			else {
				var unlocked = __instance.Inventory?.UnlockedCivilizations;
				if (unlocked != null && unlocked.Length > 0 && (int)unlocked[unlocked.Length - 1] == previous) next = mods[0].Id;
				CoreLog.Info($"civilization cycle: on {previous}, last unlocked = {(unlocked != null && unlocked.Length > 0 ? (int)unlocked[unlocked.Length - 1] : -1)}" +
				             (next >= 0 ? $", next = mod civilization {next}" : ", the game's own next"));
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

/// <summary>
/// <c>PlayerSkins.Clone()</c> is <c>new PlayerSkins(civilization, skinRegistry)</c>, and the constructor throws for a civilization number outside the five built-in
/// ones. The Rules screen clones the lobby's skins when it opens (<c>Rules.OnEnable</c>), so with a registered civilization chosen on the castle that screen
/// broke (no tech tree, wrong biome). For a registered civilization the clone is built from the borrowed one and the real number is stored afterwards.
/// </summary>
[HarmonyPatch(typeof(GamePlayerSkins), "Clone")]
internal static class PlayerSkinsCloneHook {
	[HarmonyPrefix]
	public static bool Prefix(GamePlayerSkins __instance, ref GamePlayerSkins __result) {
		try {
			int id = (int)__instance.civilization;
			if (id >= 0 && id <= 4) return true;
			if (!CivilizationRegistry.TryGetSkinsLike(id, out int like)) return true;
			var copy = new GamePlayerSkins((GameCivilization)like, __instance.skinRegistry);
			copy.civilization = (GameCivilization)id;
			__result = copy;
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:clone", "cloning skins: " + e.Message); }
		return true;
	}
}

/// <summary>
/// The game sends a player's civilization as one signed byte and the receiving side builds <c>PlayerSkins</c> from it, whose constructor throws for anything
/// outside 0..4 (and a constructor cannot be patched under IL2CPP). Both readers (<c>ReadPlayerSkins</c> and the stream reader the network variable uses) are
/// replaced: a registered civilization gets the built-in one it looks like and keeps its real number, an unknown number becomes the first civilization
/// instead of throwing and freezing the game.
/// </summary>
internal static class PlayerSkinsLookHooks {
	private static readonly string[] VisualGetters = {
		"GetBuildingVisualPrefab", "GetInnerCliffVisualHubPrefab", "GetNatureVisualHubPrefab", "GetShipVisualPrefab", "GetUnitVisualPrefab",
	};

	/// <summary>The real civilization number while one of the lookups runs with the borrowed number put in place; -1 otherwise.</summary>
	public static int RealId = -1;

	public static void Install(Harmony harmony) {
		try {
			var type = typeof(GameSkinsSerialization);
			var reader = AccessTools.Method(type, "ReadPlayerSkins");
			if (reader != null) harmony.Patch(reader, prefix: new HarmonyMethod(typeof(PlayerSkinsLookHooks), nameof(ReadPlayerSkinsPrefix)));
			else CoreLog.Warn("PlayerSkinsSerialization.ReadPlayerSkins not found");
			// the stream reader is the compiler-generated lambda <OnEnable>b__5_1(Stream)
			MethodInfo streamReader = null;
			foreach (var m in AccessTools.GetDeclaredMethods(type))
				if (m.Name.Contains("OnEnable") && m.Name.Contains("b__5_1")) { streamReader = m; break; }
			if (streamReader != null) harmony.Patch(streamReader, prefix: new HarmonyMethod(typeof(PlayerSkinsLookHooks), nameof(ReadStreamPrefix)));
			else CoreLog.Warn("PlayerSkinsSerialization stream reader not found; a civilization number from the network may throw");
		}
		catch (Exception e) { CoreLog.Error("PlayerSkins reader hooks failed: " + e); }

		// The skin lookups search the skin registry with the real civilization number and find nothing for a registered one (the default, European, look
		// is used). While a lookup runs the borrowed civilization is put in its place.
		foreach (var name in VisualGetters) {
			try {
				var method = AccessTools.Method(typeof(GamePlayerSkins), name);
				if (method == null) { CoreLog.Warn("PlayerSkins." + name + " not found"); continue; }
				harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerSkinsLookHooks), nameof(LookupPrefix)), finalizer: new HarmonyMethod(typeof(PlayerSkinsLookHooks), nameof(LookupFinalizer)));
			}
			catch (Exception e) { CoreLog.Error("PlayerSkins." + name + " hook failed: " + e); }
		}
		// a model the mod supplied for the civilization wins over the borrowed look (see CivilizationSkins)
		CivilizationSkinHooks.Install(harmony);
	}

	public static bool ReadPlayerSkinsPrefix(GameSkinsSerialization __instance, MLAPI.Serialization.BitReader reader, ref GamePlayerSkins __result) {
		try { __result = Build(__instance, reader.ReadSByte()); return false; }
		catch (Exception e) { CoreLog.Once("civ:read", "reading skins: " + e.Message); return true; }
	}

	public static bool ReadStreamPrefix(GameSkinsSerialization __instance, Il2CppSystem.IO.Stream stream, ref GamePlayerSkins __result) {
		try {
			var reader = MLAPI.Serialization.Pooled.PooledBitReader.Get(stream);
			try { __result = Build(__instance, reader.ReadSByte()); }
			finally { reader.Dispose(); }
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:readstream", "reading skins: " + e.Message); return true; }
	}

	private static GamePlayerSkins Build(GameSkinsSerialization serializer, int id) {
		int look = id, real = -1;
		if (id < 0 || id > 4) {
			if (CivilizationRegistry.TryGetSkinsLike(id, out int like)) { look = like; real = id; }
			else {
				look = 0;   // a number this client does not know (the other player has a mod this one lacks): show the first civilization instead of throwing
				CoreLog.Once("civ:unknown" + id, $"civilization {id} is not known here (a mod the host has is missing); using the first civilization");
			}
		}
		var skins = new GamePlayerSkins((GameCivilization)look, serializer.skinRegistry);
		if (real != -1) skins.civilization = (GameCivilization)real;
		return skins;
	}

	public static void LookupPrefix(GamePlayerSkins __instance, out int __state) {
		__state = -1;
		try {
			int id = (int)__instance.civilization;
			if (id >= 0 && id <= 4) return;
			if (!CivilizationRegistry.TryGetSkinsLike(id, out int like)) return;
			__state = id;
			RealId = id;
			__instance.civilization = (GameCivilization)like;
		}
		catch (Exception e) { CoreLog.Once("civ:look", "skin lookup hook: " + e.Message); }
	}

	public static Exception LookupFinalizer(GamePlayerSkins __instance, int __state, Exception __exception) {
		RealId = -1;
		if (__state >= 0) {
			try { __instance.civilization = (GameCivilization)__state; }
			catch (Exception e) { CoreLog.Once("civ:look2", "skin lookup hook: " + e.Message); }
		}
		return __exception;
	}
}

/// <summary>
/// Runs on the host while a lobby is open: a player standing on a registered civilization that some member does not have is moved to the built-in civilization
/// it looks like, because that member's game would throw on the number (and freeze). Runs when a member joins, leaves or publishes its civilizations
/// (the only moments the answer can change; the castle click itself only offers civilizations everybody has).
/// </summary>
internal static class CivilizationGuard {
	public static void Run() {
		using var slow = CoreLog.Slow("civilization guard");
		try {
			if (!LobbyState.InLobby || !LobbyState.IsHost || LobbyState.RulesFrozen) return;
			foreach (var player in UnityEngine.Object.FindObjectsOfType<GamePlayer>()) {
				if (player == null || !player.IsServer) continue;
				var current = player.Skins;
				if (current == null) continue;
				int number = (int)current.civilization;
				if (number < CivilizationRegistry.FirstId) continue;   // built-in
				var civ = CivilizationRegistry.Find(number);
				if (civ != null && LobbyState.EveryoneHasCivilization(civ)) continue;
				// a civilization somebody lacks, or one this computer no longer has (its mod was switched off): the civilization it looks like, else Europe
				player.skins.Value = new GamePlayerSkins((GameCivilization)(civ?.SkinsLike ?? 0), player.skinRegistry);
				CoreLog.Warn(civ != null
					? $"'{civ.Name}' is not installed for everybody in the lobby; a player was moved to the civilization it looks like."
					: $"civilization {number} was removed; a player was moved to Europe.");
			}
		}
		catch (Exception e) { CoreLog.Once("civ:guard", "civilization guard: " + e.Message); }
	}
}

/// <summary>
/// An AI player chooses a build order with <c>AIPlay.SampleBuildOrder(random, civilization, strategies)</c>, which keeps the orders written for exactly that
/// civilization and throws "No build order matching civilization=..." when there are none. <see cref="BuildOrders"/> normally gives every registered
/// civilization its orders; only when it could not (the demo, the tutorial) the civilization it looks like is asked instead.
/// </summary>
[HarmonyPatch(typeof(GameAIPlay), "SampleBuildOrder")]
internal static class AiBuildOrderHook {
	[HarmonyPrefix]
	public static void Prefix(GameAIPlay __instance, ref GameCivilization civilization) {
		try {
			int id = (int)civilization;
			if (id >= 0 && id <= 4) return;
			if (__instance.buildOrders == null) __instance.InitBuildOrders();
			if (BuildOrders.Loaded.Contains(id)) return;
			if (CivilizationRegistry.TryGetSkinsLike(id, out int like)) civilization = (GameCivilization)like;
		}
		catch (Exception e) { CoreLog.Once("civ:ai", "AI build order hook: " + e.Message); }
	}
}

/// <summary>
/// <c>Statistics.TechTree</c> (the tree of an island's owner, used by the research window and by learning) carries its own copy of the lookup in
/// <c>Technologies.TechTree</c> (the compiler inlined it), so that hook never sees it and the <c>First(...)</c> inside throws "Sequence contains no matching
/// element" for a registered civilization. For one, the lookup goes through <c>Technologies.TechTree</c>, which returns the registered tree.
/// </summary>
[HarmonyPatch(typeof(GameStatistics), "TechTree", MethodType.Getter)]
internal static class StatisticsTechTreeHook {
	[HarmonyPrefix]
	public static bool Prefix(GameStatistics __instance, ref GameTechTree __result) {
		try {
			var skins = __instance.island?.Skins;
			if (skins == null) return true;
			int id = (int)skins.civilization;
			if (id >= 0 && id <= 4) { if (!BuiltInChanges.TryGetTree(id, out _)) return true; }   // a built-in civilization with a mod's tree goes the same way
			else if (CivilizationRegistry.Find(id) == null) return true;
			var technologies = __instance.technologies;
			if (technologies == null) return true;
			__result = technologies.TechTree((GameCivilization)id);   // TechTreeLookupHook answers for the registered number
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:stats", "tech tree getter hook: " + e.Message); }
		return true;
	}
}

/// <summary>
/// <c>Statistics.IsTechnologyLearnable</c> has the same inlined tree lookup (and the research window asks it for every technology every frame). For a
/// registered civilization it is evaluated here with the tree from <see cref="StatisticsTechTreeHook"/>, following the game's own rule: not already learnt, and
/// either no dependencies or at least one of them learnt (in the Steam demo only one technology can be learnt).
/// </summary>
[HarmonyPatch(typeof(GameStatistics), "IsTechnologyLearnable")]
internal static class StatisticsLearnableHook {
	[HarmonyPrefix]
	public static bool Prefix(GameStatistics __instance, GameTechnology technology, ref bool __result) {
		try {
			var skins = __instance.island?.Skins;
			if (skins == null || technology == null) return true;
			int id = (int)skins.civilization;
			if (id >= 0 && id <= 4) { if (!BuiltInChanges.TryGetTree(id, out _)) return true; }   // a built-in civilization with a mod's tree goes the same way
			else if (CivilizationRegistry.Find(id) == null) return true;

			var learnt = __instance.LearntTechnologies;
			if (learnt == null) return true;
			if (learnt.Contains(technology)) { __result = false; return false; }
			if (Utility.Steamworks.SteamworksInit.IsSteamAndDemo && (int)technology.name != 5) { __result = false; return false; }

			var tree = __instance.TechTree;   // StatisticsTechTreeHook returns the registered tree
			if (tree == null) return true;
			var dependencies = tree.GetDependencies(technology);
			bool learnable = dependencies == null || dependencies.Length == 0;
			if (!learnable) foreach (var dependency in dependencies) if (learnt.Contains(dependency)) { learnable = true; break; }
			__result = learnable;
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:learnable", "IsTechnologyLearnable hook: " + e.Message); }
		return true;
	}
}
