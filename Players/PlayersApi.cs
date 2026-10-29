using System;
using System.Collections.Generic;
using GamePlayer = DiceKingdoms.Game.Player;

namespace DiceModders.ModCore;

/// <summary>AI difficulty, as in the lobby's drop-down.</summary>
public enum AiDifficulty {
	/// <summary>Does nothing (only the tutorial uses it).</summary>
	Passive = 0,
	Peasant = 1,
	Bourgeois = 2,
	General = 3,
	King = 4,
	Sorcerer = 5,
}

/// <summary>What an AI player aims for.</summary>
public enum AiStrategy {
	Military = 0,
	Wonder = 1,
}

/// <summary>
/// One player of the lobby or match, human or AI. All values are read live from the game's <c>Player</c> object, so keep the object and read it when you
/// need the value. <see cref="IsValid"/> turns false when the game removes the player (left the lobby, match closed).
/// </summary>
public sealed class MatchPlayer {
	private static readonly string[] BuiltInCivilizations = { "Europe", "Middle East", "Viking", "Ancient", "Maya" };

	internal MatchPlayer(GamePlayer player) => Game = player;

	/// <summary>The game's own object, for anything ModCore does not wrap.</summary>
	public GamePlayer Game { get; }

	/// <summary>The game still has this player.</summary>
	public bool IsValid => Game != null;   // Unity null check: a destroyed player reads as null

	/// <summary>
	/// The player's stable id: the Steam id for humans, a made-up id counting down from <c>ulong.MaxValue</c> for AI. It never changes during a match and is what
	/// the game uses to recognise a player who reconnects.
	/// </summary>
	public ulong Id => Try(() => Game.InitialOwnerId, 0UL);

	/// <summary>Display name: the Steam name, or for AI the colour and difficulty ("Red Peasant").</summary>
	public string Name => Try(() => Game.UserName, "") ?? "";

	public bool IsAI => Try(() => Game.IsAI, false);

	/// <summary>The player on this machine.</summary>
	public bool IsLocal => Try(() => { var local = GamePlayer.LocalPlayer; return local != null && local.Pointer == Game.Pointer; }, false);

	/// <summary>The player who hosts the match (never an AI).</summary>
	public bool IsHost => Try(() => {
		var gs = MatchState.Game;
		return gs != null && !Game.IsAI && Game.InitialOwnerId == gs.OwnerClientId;
	}, false);

	/// <summary>AI difficulty; null for humans.</summary>
	public AiDifficulty? Difficulty => ReadAi(DifficultyOffset, 0, 5, out int v) ? (AiDifficulty)v : null;

	/// <summary>AI strategy; null for humans. The AI picks it (with its build order) when the Start phase ends, so read it from the first Harvest on.</summary>
	public AiStrategy? Strategy => ReadAi(StrategyOffset, 0, 1, out int v) ? (AiStrategy)v : null;

	// The interop copy of Player.AI (Nullable<AIState>, a struct holding two arrays) comes back with garbage, so the game's own field is read directly:
	// Player.ai (0xD0) = NetworkedVar<Opt<AIState>>, its InternalValue at +0x38: hasValue (bool) +0x38, Difficulty +0x40, Strategy +0x44
	// (from Player.get_AI, game v1.0.4). A value outside the enum's range means the layout changed: then nothing is returned instead of a wrong value.
	private const int AiField = 0xD0, OptHasValue = 0x38, DifficultyOffset = 0x40, StrategyOffset = 0x44;

	private bool ReadAi(int offset, int min, int max, out int value) {
		value = 0;
		try {
			if (Game == null || !Game.IsAI) return false;
			IntPtr networkedVar = Tools.Mem.ReadPtr(Tools.Mem.PtrOf(Game), AiField);
			if (networkedVar == IntPtr.Zero || (Tools.Mem.ReadInt(networkedVar, OptHasValue) & 0xFF) == 0) return false;
			value = Tools.Mem.ReadInt(networkedVar, offset);
			if (value >= min && value <= max) return true;
			CoreLog.Once("players:ai-layout", "players: the AI state has an unexpected value (" + value + "), the game's layout may have changed; AI difficulty/strategy are not reported");
			return false;
		}
		catch { return false; }
	}

	/// <summary>The civilization number: 0..4 built in (Europe, Middle East, Viking, Ancient, Maya), 5..127 added by mods.</summary>
	public int Civilization => Try(() => (int)Game.Skins.Civilization, -1);

	/// <summary>The civilization's name (a mod civilization's own name).</summary>
	public string CivilizationName {
		get {
			int id = Civilization;
			if (id >= 0 && id < BuiltInCivilizations.Length) return BuiltInCivilizations[id];
			var civ = CivilizationRegistry.Find(id);
			return civ != null ? civ.Name : "";
		}
	}

	/// <summary>The colour's index (0 Red, 1 Pink, 2 Orange, 3 Green, 4 Carribean, 5 Cyan, 6 Yellow, 7 Ivory, 8 Brown, 9 Blue, 10 Grey, 11 Purple).</summary>
	public int ColorIndex => Try(() => Game.Color.Index, -1);

	/// <summary>The colour's name.</summary>
	public string ColorName => Try(() => Game.Color.Name, "") ?? "";

	/// <summary>The colour as a Unity colour (for drawing).</summary>
	public UnityEngine.Color Color => Try(() => Game.Color.Color, UnityEngine.Color.white);

	/// <summary>The team number (colours are grouped in threes). Only meaningful when <see cref="MatchSettings.Teams"/> is on.</summary>
	public int Team => Try(() => Game.Team.Index, -1);

	/// <summary>The player's island, once the islands exist (null in the lobby).</summary>
	public DiceKingdoms.Game.Island Island => Try(() => Game.Island, null);

	/// <summary>Lost the match (no castle left or surrendered).</summary>
	public bool IsDefeated => Try(() => Game.IsDefeated, false);

	/// <summary>Connected right now (a human who dropped out reads false and can rejoin). AI players always read false.</summary>
	public bool IsConnected => Try(() => Game.IsConnected, false);

	private T Try<T>(Func<T> read, T fallback) {
		if (Game == null) return fallback;
		try { return read(); }
		catch { return fallback; }
	}

	public override string ToString() => Name + " (" + (IsAI ? "AI" : "human") + ", " + CivilizationName + ", " + ColorName + ")";
}

public static partial class ModCore {
	/// <summary>
	/// The players of the lobby screen and the match, humans and AI, from the game's own list. (<see cref="Lobby.Members"/> is the Steam lobby: humans only,
	/// also before the match-start screen exists.) Read on the main thread.
	/// </summary>
	public static class Players {
		/// <summary>Every player, in the game's order. A fresh list on every call; empty when there is no lobby screen or match.</summary>
		public static IReadOnlyList<MatchPlayer> All {
			get {
				Core.Ensure();
				var result = new List<MatchPlayer>();
				try {
					var gs = MatchState.Game;
					var list = gs != null ? gs.Players : null;
					if (list == null) return result;
					for (int i = 0; i < list.Count; i++) {
						var p = list[i];
						if (p != null) result.Add(new MatchPlayer(p));
					}
				}
				catch (Exception e) { CoreLog.Once("players:all", "players: could not read the game's player list: " + e.Message); }
				return result;
			}
		}

		/// <summary>The player on this machine, or null.</summary>
		public static MatchPlayer Local {
			get {
				Core.Ensure();
				try { var p = DiceKingdoms.Game.Player.LocalPlayer; return p != null ? new MatchPlayer(p) : null; }
				catch { return null; }
			}
		}

		/// <summary>The host, or null.</summary>
		public static MatchPlayer Host { get { foreach (var p in All) if (p.IsHost) return p; return null; } }

		/// <summary>The player with this <see cref="MatchPlayer.Id"/>, or null.</summary>
		public static MatchPlayer Find(ulong id) { foreach (var p in All) if (p.Id == id) return p; return null; }
	}
}
