using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using GameState = DiceKingdoms.Game.GameState;

namespace DiceModders.ModCore;

/// <summary>
/// Listens to the game's own match events. <c>GameState</c> has Action fields (<c>onIslandSpawned, onStartGame, onEnterPhase, onSyncPhase, onEndGame,
/// onRejoin</c>) that the game invokes on every machine; ModCore adds its handlers next to the game's own when the <c>GameState</c> wakes up. The
/// GameState lives on between matches (the game calls <c>FullReset</c> instead of destroying it), so the handlers stay. End and reset come from a postfix
/// on <c>FullReset</c> (see <see cref="LobbyHooks"/>). <c>TurnPhase.Enter</c> sets <c>GameState.TurnPhase</c> before it invokes <c>onEnterPhase</c>, so the phase is read there.
/// </summary>
internal static class MatchState {
	private static readonly List<object> Keep = new();   // the IL2CPP delegates must outlive the subscription
	private static IntPtr _attachedTo;
	private static bool _installed;

	public static MatchPhase Phase { get; private set; }
	public static int Turn { get; private set; }

	private static GameState _game;
	/// <summary>The current GameState (lobby or match scene), or null. Unity null check: a destroyed one reads as null.</summary>
	public static GameState Game => _game != null ? _game : null;

	public static void Install() {
		if (_installed) return;
		_installed = true;
		try { new Harmony("DiceModders.ModCore.match").PatchAll(typeof(AwakePatch)); }
		catch (Exception e) { CoreLog.Error("match hooks failed: " + e); }
	}

	[HarmonyPatch(typeof(GameState), "Awake")]
	private static class AwakePatch {
		[HarmonyPostfix]
		public static void Postfix(GameState __instance) {
			try { Attach(__instance); }
			catch (Exception e) { CoreLog.Once("match:attach", "match: could not listen to the game's events: " + e); }
		}
	}

	private static void Attach(GameState gs) {
		if (gs == null || gs.Pointer == _attachedTo) return;
		_attachedTo = gs.Pointer;
		_game = gs;
		Phase = MatchPhase.None;
		Turn = 0;
		gs.onIslandSpawned = Add(gs.onIslandSpawned, () => ModCore.Match.RaiseIslandsSpawned());
		gs.onStartGame = Add(gs.onStartGame, () => LobbyState.MatchStarted());
		gs.onEnterPhase = Add(gs.onEnterPhase, () => OnEnterPhase(gs));
		gs.onSyncPhase = Add(gs.onSyncPhase, () => ModCore.Match.RaisePhaseSynced(Phase));
		gs.onEndGame = Add(gs.onEndGame, () => ModCore.Match.RaiseGameOver());
		gs.onRejoin = Add(gs.onRejoin, () => ModCore.Match.RaiseRejoined());
		CoreLog.Info("match: listening to the game's match events");
	}

	/// <summary>The match ended (<c>GameState.FullReset</c>). The GameState stays alive and keeps our handlers for the next match.</summary>
	public static void Clear() {
		Phase = MatchPhase.None;
		Turn = 0;
	}

	private static void OnEnterPhase(GameState gs) {
		Phase = Read(gs);
		try { Turn = gs.TurnCounter; } catch { /* keep the last value */ }
		CoreLog.Trace($"match: turn {Turn}, phase {Phase}");
		ModCore.Match.RaisePhaseEntered(Phase);
	}

	private static MatchPhase Read(GameState gs) {
		IntPtr current = PtrOf(gs.TurnPhase);
		if (current == IntPtr.Zero) return MatchPhase.None;
		if (current == PtrOf(gs.startPhase)) return MatchPhase.Start;
		if (current == PtrOf(gs.harvestPhase)) return MatchPhase.Harvest;
		if (current == PtrOf(gs.disastersPhase)) return MatchPhase.Disasters;
		if (current == PtrOf(gs.buildPhase)) return MatchPhase.Build;
		if (current == PtrOf(gs.combatPhase)) return MatchPhase.Combat;
		if (current == PtrOf(gs.winPhase)) return MatchPhase.Win;
		return MatchPhase.None;
	}

	private static IntPtr PtrOf(Il2CppObjectBase o) => o == null ? IntPtr.Zero : o.Pointer;

	private static Il2CppSystem.Action Add(Il2CppSystem.Action existing, Action handler) {
		Action safe = () => {
			try { handler(); }
			catch (Exception e) { CoreLog.Once("match:handler:" + handler.Method.Name, "match: event handler failed: " + e); }
		};
		var native = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(safe);
		Keep.Add(safe);
		Keep.Add(native);
		var combined = Il2CppSystem.Delegate.Combine(existing, native);
		return combined == null ? native : combined.Cast<Il2CppSystem.Action>();
	}
}
