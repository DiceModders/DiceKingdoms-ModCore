using System;
using GameLobbySettings = DiceKingdoms.Game.LobbySettings;

namespace DiceModders.ModCore;

/// <summary>Phase timer speed chosen in the lobby.</summary>
public enum MatchTimerSpeed {
	Fast = 0,
	Slow = 1,
	/// <summary>No phase timer. The game forces this when fewer than two humans play.</summary>
	Unlimited = 2,
}

/// <summary>
/// The game's own match settings (the lobby's settings box: timer, multi meteor, teams, wonder victory), read live from the game. The host sets them;
/// clients get the host's values through the game's own sync. Mod settings are lobby rules (<see cref="ModCore.Lobby.Rules"/>), not these.
/// </summary>
public sealed class MatchSettings {
	internal MatchSettings() { }

	private static GameLobbySettings Game {
		get {
			try { var gs = MatchState.Game; return gs != null ? gs.LobbySettings : null; }
			catch { return null; }
		}
	}

	/// <summary>Phase timer speed. With fewer than two humans the game switches it to <see cref="MatchTimerSpeed.Unlimited"/> when the match starts, whatever the lobby showed.</summary>
	public MatchTimerSpeed TimerSpeed {
		get {
			try { var s = Game; return s != null ? (MatchTimerSpeed)(int)s.timerSpeed.Value : MatchTimerSpeed.Fast; }
			catch { return MatchTimerSpeed.Fast; }
		}
	}

	/// <summary>Several meteors can fall at once.</summary>
	public bool MultiMeteor => Read(s => s.MultiMeteor);

	/// <summary>Team mode: players are grouped by colour (three colours per team).</summary>
	public bool Teams => Read(s => s.Teams);

	/// <summary>A wonder can win the match.</summary>
	public bool WonderVictory => Read(s => s.WonderVictory);

	private static bool Read(Func<GameLobbySettings, bool> get) {
		try { var s = Game; return s != null && get(s); }
		catch { return false; }
	}
}
