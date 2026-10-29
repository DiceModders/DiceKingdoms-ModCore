using System;

namespace DiceModders.ModCore;

/// <summary>The phases of a match, in play order. <see cref="Start"/> runs once at the beginning (placing the first buildings), then the turn loops Harvest ... Win.</summary>
public enum MatchPhase {
	/// <summary>No match, or the first phase has not started yet.</summary>
	None = 0,
	/// <summary>"Settle": every player places the starting buildings. Runs once.</summary>
	Start,
	Harvest,
	Disasters,
	Build,
	Combat,
	/// <summary>Checks whether somebody has won. Skipped when nobody is about to win.</summary>
	Win,
}

public static partial class ModCore {
	/// <summary>
	/// The running match: when it starts and ends, which phase it is in and when phases change. Everything here happens on the main thread and on every
	/// machine (host and clients), because the events come from the game's own <c>GameState</c> events. Lobby things (members, host, rules) are in <see cref="Lobby"/>.
	/// </summary>
	public static class Match {
		/// <summary>True from <see cref="Started"/> until <see cref="Ended"/>. Lobby rules are frozen while it is true.</summary>
		public static bool Running { get { Core.Ensure(); return LobbyState.MatchRunning; } }

		/// <summary>The current phase (<see cref="MatchPhase.None"/> outside a match).</summary>
		public static MatchPhase Phase { get { Core.Ensure(); return MatchState.Phase; } }

		/// <summary>The turn number, starting at 1. It goes up when the Win phase wraps back to Harvest. 0 outside a match.</summary>
		public static int Turn { get { Core.Ensure(); return MatchState.Turn; } }

		/// <summary>The match settings the host picked in the lobby (read live). Outside a lobby or match the values are the game's defaults.</summary>
		public static MatchSettings Settings { get; } = new MatchSettings();

		/// <summary>The game's tutorial match.</summary>
		public static bool IsTraining { get { Core.Ensure(); try { var gs = MatchState.Game; return gs != null && gs.IsTraining; } catch { return false; } } }

		/// <summary>Fewer than two human players (you against AI only): the game then has no phase timer and its developer commands work.</summary>
		public static bool IsSolo {
			get {
				Core.Ensure();
				int humans = 0;
				foreach (var p in Players.All) if (!p.IsAI) humans++;
				return humans < 2;
			}
		}

		/// <summary>Every player has loaded the islands; the match is about to start.</summary>
		public static event Action IslandsSpawned { add { Core.Ensure(); _islandsSpawned += value; } remove { _islandsSpawned -= value; } }
		private static Action _islandsSpawned;

		/// <summary>The match started. Lobby rules are frozen before this is raised.</summary>
		public static event Action Started { add { Core.Ensure(); _started += value; } remove { _started -= value; } }
		private static Action _started;

		/// <summary>A phase began (<see cref="Phase"/> is already the new one). Raised for <see cref="MatchPhase.Start"/> too.</summary>
		public static event Action<MatchPhase> PhaseEntered { add { Core.Ensure(); _phaseEntered += value; } remove { _phaseEntered -= value; } }
		private static Action<MatchPhase> _phaseEntered;

		/// <summary>The phase's time ran out or everybody was ready: the planned actions are applied now (AI players make their moves at this moment).</summary>
		public static event Action<MatchPhase> PhaseSynced { add { Core.Ensure(); _phaseSynced += value; } remove { _phaseSynced -= value; } }
		private static Action<MatchPhase> _phaseSynced;

		/// <summary>The game is over (somebody won). The match screen is still open; players can stay and watch.</summary>
		public static event Action GameOver { add { Core.Ensure(); _gameOver += value; } remove { _gameOver -= value; } }
		private static Action _gameOver;

		/// <summary>This machine reconnected to a running match and rebuilt its state.</summary>
		public static event Action Rejoined { add { Core.Ensure(); _rejoined += value; } remove { _rejoined -= value; } }
		private static Action _rejoined;

		/// <summary>The game reset its match state (<c>GameState.onReset</c>).</summary>
		public static event Action Reset { add { Core.Ensure(); _reset += value; } remove { _reset -= value; } }
		private static Action _reset;

		/// <summary>The match screen was closed: back in the lobby or the menu. Lobby rules follow the lobby again.</summary>
		public static event Action Ended { add { Core.Ensure(); _ended += value; } remove { _ended -= value; } }
		private static Action _ended;

		internal static void RaiseIslandsSpawned() => Raise(_islandsSpawned, "Match.IslandsSpawned");
		internal static void RaiseStarted() => Raise(_started, "Match.Started");
		internal static void RaisePhaseEntered(MatchPhase phase) => Raise(_phaseEntered, phase, "Match.PhaseEntered");
		internal static void RaisePhaseSynced(MatchPhase phase) => Raise(_phaseSynced, phase, "Match.PhaseSynced");
		internal static void RaiseGameOver() => Raise(_gameOver, "Match.GameOver");
		internal static void RaiseRejoined() => Raise(_rejoined, "Match.Rejoined");
		internal static void RaiseReset() => Raise(_reset, "Match.Reset");
		internal static void RaiseEnded() => Raise(_ended, "Match.Ended");

		private static void Raise(Action handler, string what) {
			if (handler == null) return;
			foreach (Action d in handler.GetInvocationList()) {
				try { d(); }
				catch (Exception e) { CoreLog.Error(what + " handler failed: " + e); }
			}
		}

		private static void Raise(Action<MatchPhase> handler, MatchPhase phase, string what) {
			if (handler == null) return;
			foreach (Action<MatchPhase> d in handler.GetInvocationList()) {
				try { d(phase); }
				catch (Exception e) { CoreLog.Error(what + " handler failed: " + e); }
			}
		}
	}
}
