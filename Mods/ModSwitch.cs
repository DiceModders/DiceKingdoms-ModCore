using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;

namespace DiceModders.ModCore;

/// <summary>
/// A mod's on/off switch: the setting <c>[General] Enabled</c> in its own <c>.cfg</c>. In-game config editors (DiceModders ConfigMenu) show it as a
/// toggle and change it while the game runs; the mod reacts through <see cref="Changed"/> / <see cref="WhenOn"/> (event-driven, nothing polls).
/// <para>Safety: a switch made with <c>affectsSimulation: true</c> never changes during a match - a change made then is applied when the match ends.
/// While a mod is off, its lobby rules are not announced to the lobby (the others see it as missing) and every gameplay change ModCore applies
/// for it (unit stats, tech trees) falls back to the game's own, for everybody.</para>
/// </summary>
public sealed class ModSwitch {
	internal readonly string ModId;
	private readonly bool _affectsSimulation;
	private bool _applied;

	/// <summary>The config entry (ConfigMenu edits it).</summary>
	public ConfigEntry<bool> Entry { get; }

	/// <summary>Whether the mod is on right now (a gameplay mod keeps its state until the match ends; a restart-only one its start state).</summary>
	public bool IsOn => _applied;

	/// <summary>Raised when <see cref="IsOn"/> changes.</summary>
	public event Action<bool> Changed;

	internal ModSwitch(string modId, ConfigEntry<bool> entry, bool affectsSimulation, bool restartNeeded) {
		ModId = modId; Entry = entry; _affectsSimulation = affectsSimulation;
		_applied = entry.Value;
		if (!restartNeeded) entry.SettingChanged += (_, _) => Apply("setting changed");
	}

	/// <summary>Runs <paramref name="on"/> or <paramref name="off"/> now for the current state, and again on every change.</summary>
	public ModSwitch WhenOn(Action on, Action off = null) {
		void Run(bool v) { try { (v ? on : off)?.Invoke(); } catch (Exception e) { CoreLog.Error("mod switch '" + ModId + "' handler failed: " + e); } }
		Run(_applied);
		Changed += Run;
		return this;
	}

	internal void Apply(string why) {
		bool want = Entry.Value;
		if (want == _applied) return;
		if (_affectsSimulation && LobbyState.MatchRunning) {
			CoreLog.Info("mod '" + ModId + "' will be turned " + (want ? "on" : "off") + " when the match ends (gameplay must not change during a match)");
			return;
		}
		_applied = want;
		CoreLog.Info("mod '" + ModId + "' turned " + (want ? "on" : "off") + " (" + why + ")");
		LobbyState.PublishMods();   // the lobby learns whether this mod's rules exist
		UnitStatsRegistry.Changed();
		GameBalance.Recompute("mod switch");
		var handler = Changed;
		if (handler == null) return;
		foreach (Action<bool> d in handler.GetInvocationList()) {
			try { using (CoreLog.Slow("handler " + CoreLog.NameOf(d))) d(want); } catch (Exception e) { CoreLog.Error("mod switch '" + ModId + "' handler failed: " + e); }
		}
	}
}

internal static class ModSwitches {
	internal static readonly Dictionary<string, ModSwitch> ById = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>A mod without a switch is always on.</summary>
	public static bool IsOn(string modId) => modId == null || !ById.TryGetValue(modId, out var s) || s.IsOn;

	/// <summary>Called when a match ends: switches changed during the match take effect now.</summary>
	public static void MatchEnded() { foreach (var s in ById.Values) s.Apply("match ended"); }
}

public static partial class ModCore {
	/// <summary>On/off switches for mods, editable in game with an in-game config editor such as DiceModders ConfigMenu.</summary>
	public static class Mods {
		/// <summary>
		/// Makes (or returns) the on/off switch of a plugin: <c>[General] Enabled</c> in <paramref name="config"/> (your plugin's <c>Config</c>).
		/// <paramref name="affectsSimulation"/>: the mod changes gameplay (stats, rules, AI...), so a change waits for the end of a match.
		/// <paramref name="restartNeeded"/>: the mod cannot switch while running; the setting is read at start only (the description says so).
		/// </summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static ModSwitch Switch(ConfigFile config, bool affectsSimulation = false, bool restartNeeded = false, string description = null) {
			Core.Ensure();
			if (config == null) throw new ArgumentNullException(nameof(config));
			string id = LobbyMods.Identify(System.Reflection.Assembly.GetCallingAssembly()).Id;   // the calling plugin's GUID
			if (ModSwitches.ById.TryGetValue(id, out var existing)) return existing;
			string text = (description ?? "Turn this mod on or off.") +
			              (restartNeeded ? " Takes effect after a restart." : affectsSimulation ? " Changes during a match apply when it ends." : "");
			var entry = config.Bind("General", "Enabled", true, text);
			var sw = new ModSwitch(id, entry, affectsSimulation, restartNeeded);
			ModSwitches.ById[id] = sw;
			return sw;
		}

		/// <summary>Whether a mod (by its plugin GUID) is on; mods without a switch are always on.</summary>
		public static bool IsOn(string pluginGuid) => ModSwitches.IsOn(pluginGuid);
	}
}
