using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DiceModders.ModCore;

/// <summary>One player of the Steam lobby (AI players are not Steam members and are not listed).</summary>
public readonly record struct LobbyMember(ulong SteamId, string Name, bool IsHost, bool IsLocal, bool IsConnected);

/// <summary>Non-generic view of a lobby rule: what a screen needs to list it.</summary>
public interface ILobbyRule {
	string Key { get; }
	string Title { get; }
	string Description { get; }
	/// <summary>The BepInEx plugin that registered the rule (its GUID, name and version).</summary>
	string ModId { get; }
	string ModName { get; }
	string ModVersion { get; }
	bool AffectsSimulation { get; }
	/// <summary>The result of the rule's <c>visible</c> callback (true without one). A hidden rule still applies its value.</summary>
	bool Visible { get; }
	/// <summary>True for the host while the lobby is open (not during a match).</summary>
	bool Editable { get; }
	/// <summary>The host's current value differs from the default.</summary>
	bool IsNonDefault { get; }
	/// <summary>The heading the rule is listed under (null: the general part of the lobby screen).</summary>
	LobbyGroup Group { get; }
}

internal interface ILobbyRuleInternal : ILobbyRule {
	LobbyMod Mod { get; }
	/// <summary>Fixed for the match with a value that changes the simulation: a player without this rule cannot play this match.</summary>
	bool InUse { get; }
	void Freeze();
	/// <summary>Fixes the rule with the host's decided value (its lobby-data text); false when the text cannot be read (then <see cref="Freeze"/> is used).</summary>
	bool FreezeFrom(string raw);
	/// <summary>The frozen value as lobby-data text (what the host publishes as its decision).</summary>
	string FrozenRaw { get; }
	void Unfreeze();
	void Reevaluate();
}

/// <summary>
/// A setting the host edits in the lobby and everybody with the same mod follows (see <see cref="ModCore.Lobby.Rules"/>).
/// <see cref="Value"/> is the live value while the lobby is open and the frozen copy while a match runs.
/// </summary>
public sealed class LobbyRule<T> : ILobbyRuleInternal {
	private readonly Func<T, string> _write;
	private readonly Func<string, (bool ok, T value)> _read;
	private readonly Func<bool> _visible;
	private T _frozen;
	private bool _isFrozen;
	private T _reported;
	private T _local;
	private bool _hasLocal;

	internal LobbyRule(LobbyMod mod, string key, string title, T defaultValue, string description, Func<bool> visible, bool affectsSimulation,
		Func<T, string> write, Func<string, (bool ok, T value)> read) {
		Mod = mod; Key = key; Title = title; Default = defaultValue; Description = description; AffectsSimulation = affectsSimulation;
		_visible = visible; _write = write; _read = read;
		_reported = defaultValue;
		if (affectsSimulation) LobbyMods.MarkSimulation(mod);   // its DLL fingerprint is compared in the lobby
	}

	/// <summary>The id the rule was registered with, unique per mod (prefix it with your mod name).</summary>
	public string Key { get; }
	public string Title { get; }
	public T Default { get; }
	public string Description { get; }
	public bool AffectsSimulation { get; }
	public string ModId => Mod.Id;
	public string ModName => Mod.Name;
	public string ModVersion => Mod.Version;

	/// <summary>Numbers only: the allowed range and the step of the editor.</summary>
	public int Min { get; internal set; }
	public int Max { get; internal set; }
	public int Step { get; internal set; } = 1;
	/// <summary>For a rule made with <c>Rules.Choice</c>: the names of the values 0, 1, 2 ... (screens show a drop-down); null for a plain number.</summary>
	public IReadOnlyList<string> Choices { get; internal set; }

	/// <summary>
	/// The value to simulate with. Outside a lobby it is <see cref="Default"/>; in the lobby it is the host's current value; from the moment the match
	/// starts (from the moment the islands start to spawn, which is a little before the match itself) it is a frozen copy (changes during a match are
	/// ignored) until the lobby or menu is shown again.
	/// </summary>
	public T Value => _isFrozen ? _frozen : Live;

	/// <summary>The host's value as the lobby currently says (ignores the freeze). Outside a Steam lobby (a local game): the value set locally, else <see cref="Default"/>.</summary>
	public T Live {
		get {
			if (!LobbyState.InLobby) return _hasLocal ? _local : Default;
			string raw = LobbyState.GetLobbyData(StorageKey);
			if (!string.IsNullOrEmpty(raw)) { var (ok, value) = _read(raw); if (ok) return value; }
			return Default;
		}
	}

	/// <summary>The host can change it before the match; with no Steam lobby (a local game) the local player can.</summary>
	public bool Editable => !LobbyState.RulesFrozen && (LobbyState.IsHost || !LobbyState.InLobby);

	public bool Visible {
		get {
			try { return _visible == null || _visible(); }
			catch (Exception e) { CoreLog.Once("lobbyrule:visible:" + Key, $"lobby rule '{Key}': visible() failed: {e.Message}"); return true; }
		}
	}

	public bool IsNonDefault => !EqualityComparer<T>.Default.Equals(Live, Default);

	public LobbyGroup Group { get; private set; }

	/// <summary>Lists the rule under a heading (e.g. <c>ModCore.Lobby.Groups.ForCivilization(civ.Id)</c>); null puts it back in the general part. Returns the rule.</summary>
	public LobbyRule<T> InGroup(LobbyGroup group) {
		if (Group == group) return this;
		Group = group;
		ModCore.Lobby.Groups.RaiseChanged();
		return this;
	}

	/// <summary>Writes a new value into the lobby (host only, before the match). Returns false when it was not accepted.</summary>
	public bool TrySet(T value) {
		if (!Editable) return false;
		if (!LobbyState.InLobby) { _local = value; _hasLocal = true; Reevaluate(); return true; }   // local game: nobody to sync with
		if (!LobbyState.SetLobbyData(StorageKey, _write(value))) return false;
		Reevaluate();   // the host's own data callback may arrive later (or not at all): do not wait for it
		return true;
	}

	/// <summary>Raised on every client when <see cref="Value"/> changes (not only on the host).</summary>
	public event Action<T> Changed;

	// ---- internal

	internal LobbyMod Mod { get; }
	LobbyMod ILobbyRuleInternal.Mod => Mod;
	bool ILobbyRuleInternal.InUse => AffectsSimulation && _isFrozen && !EqualityComparer<T>.Default.Equals(_frozen, Default);
	private string StorageKey => "modcore.rule." + Key;

	void ILobbyRuleInternal.Freeze() {
		var live = Live;
		_frozen = live;
		// A rule that changes the simulation is only used when every lobby member has this mod in the same version; otherwise everybody plays
		// the default (every computer reads the same lobby data, so all of them decide the same way and nobody desyncs).
		string missing = AffectsSimulation && !EqualityComparer<T>.Default.Equals(live, Default) ? LobbyRuleRegistry.FirstMissing(this) : null;
		if (missing != null) {
			_frozen = Default;
			CoreLog.Warn($"lobby rule '{Key}' is off for this match: {missing} does not have {Mod.Name} {ModVersion}.");
		}
		_isFrozen = true;
		if (missing != null) Reevaluate();
	}

	bool ILobbyRuleInternal.FreezeFrom(string raw) {
		if (raw == null) return false;
		var (ok, value) = _read(raw);
		if (!ok) return false;
		_frozen = value;
		_isFrozen = true;
		Reevaluate();
		return true;
	}

	string ILobbyRuleInternal.FrozenRaw => _write(_isFrozen ? _frozen : Live);

	void ILobbyRuleInternal.Unfreeze() {
		_isFrozen = false;
		Reevaluate();
	}

	void ILobbyRuleInternal.Reevaluate() => Reevaluate();

	private void Reevaluate() {
		T now = Value;
		if (EqualityComparer<T>.Default.Equals(now, _reported)) return;
		_reported = now;
		var handler = Changed;
		if (handler == null) return;
		foreach (Action<T> d in handler.GetInvocationList()) {
			try { using (CoreLog.Slow("handler " + CoreLog.NameOf(d))) d(now); }
			catch (Exception e) { CoreLog.Error($"lobby rule '{Key}' Changed handler failed: {e}"); }
		}
	}
}

public static partial class ModCore {
	/// <summary>
	/// The Steam lobby the game is in: who is in it, who hosts it, and rules the host can set for everybody. The match itself is <see cref="Match"/>.
	/// Everything here is read on the main thread. The state comes from the game's own Steam matchmaking (Facepunch.Steamworks) events.
	/// </summary>
	public static partial class Lobby {
		/// <summary>True between entering a Steam lobby and leaving it (the lobby also stays while a match runs).</summary>
		public static bool InLobby { get { Core.Ensure(); return LobbyState.InLobby; } }

		/// <summary>True when the local player owns the lobby.</summary>
		public static bool IsHost { get { Core.Ensure(); return LobbyState.IsHost; } }

		/// <summary>The local Steam id; 0 when Steam is not available.</summary>
		public static ulong LocalSteamId { get { Core.Ensure(); return LobbyState.LocalSteamId; } }

		/// <summary>The current Steam lobby, or null when not in one.</summary>
		public static Steamworks.Data.Lobby? Current { get { Core.Ensure(); return LobbyState.Current; } }

		/// <summary>The players of the lobby (a fresh list after every change; empty when not in a lobby).</summary>
		public static IReadOnlyList<LobbyMember> Members { get { Core.Ensure(); return LobbyState.Members; } }

		/// <summary>Joined or left the lobby, a member joined, left or was kicked, or the owner changed. Rule values have their own <c>Changed</c> events; plain lobby data changes (the game writes its own several times a second) do not raise this.</summary>
		public static event Action Changed { add { Core.Ensure(); _changed += value; } remove { _changed -= value; } }
		private static Action _changed;

		internal static void RaiseChanged() => Raise(_changed, "Lobby.Changed");

		private static void Raise(Action handler, string what) {
			if (handler == null) return;
			foreach (Action d in handler.GetInvocationList()) {
				try { using (CoreLog.Slow("handler " + CoreLog.NameOf(d))) d(); }
				catch (Exception e) { CoreLog.Error(what + " handler failed: " + e); }
			}
		}

		/// <summary>
		/// Settings the host edits in the lobby and everybody with the same mod follows. The value is stored in the Steam lobby data under
		/// "modcore.rule.&lt;key&gt;". A screen that lists them (for example the ModBridge's Settings > Lobby section) reads <see cref="All"/>.
		/// </summary>
		public static class Rules {
			/// <summary>Every registered rule, in registration order.</summary>
			public static IReadOnlyList<ILobbyRule> All => LobbyRuleRegistry.All;

			/// <summary>
			/// True from the moment the islands start to spawn until the match ends: every <c>Value</c> is the match value. Rules that shape the islands
			/// (read in <c>IslandGrid.Awake</c> or <c>GameState.SpawnIslands</c>) should only act while this is true, so menu and editor islands stay normal.
			/// </summary>
			public static bool Fixed => LobbyState.RulesFrozen;

			/// <summary>Raised when a rule is registered.</summary>
			public static event Action<ILobbyRule> Registered;
			internal static void RaiseRegistered(ILobbyRule rule) {
				var handler = Registered;
				if (handler == null) return;
				foreach (Action<ILobbyRule> d in handler.GetInvocationList()) {
					try { using (CoreLog.Slow("handler " + CoreLog.NameOf(d))) d(rule); }
					catch (Exception e) { CoreLog.Error("Lobby.Rules.Registered handler failed: " + e); }
				}
			}

			/// <param name="key">Unique per mod; registering a key twice returns the same rule.</param>
			/// <param name="visible">Evaluated by whatever lists the rule; a hidden rule still applies its value.</param>
			/// <param name="affectsSimulation">False for purely visual rules: they never trigger the "may desync" warning.</param>
			[MethodImpl(MethodImplOptions.NoInlining)]
			public static LobbyRule<bool> Toggle(string key, string title, bool defaultValue, string description = null, Func<bool> visible = null, bool affectsSimulation = true)
				=> ToggleFor(LobbyMods.Identify(Assembly.GetCallingAssembly()), key, title, defaultValue, description, visible, affectsSimulation);

			/// <summary>A toggle that belongs to <paramref name="mod"/> (ModCore registers rules on behalf of the mod that called it).</summary>
			internal static LobbyRule<bool> ToggleFor(LobbyMod mod, string key, string title, bool defaultValue, string description = null, Func<bool> visible = null, bool affectsSimulation = true) {
				return LobbyRuleRegistry.Register(key, () => new LobbyRule<bool>(mod, key, title, defaultValue, description, visible, affectsSimulation,
					v => v ? "1" : "0",
					raw => raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) ? (true, true)
						 : raw == "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase) ? (true, false) : (false, false)));
			}

			[MethodImpl(MethodImplOptions.NoInlining)]
			public static LobbyRule<int> Number(string key, string title, int defaultValue, int min, int max, int step, string description = null, Func<bool> visible = null, bool affectsSimulation = true) {
				var mod = LobbyMods.Identify(Assembly.GetCallingAssembly());
				if (max < min) (min, max) = (max, min);
				int lo = min, hi = max;
				var rule = LobbyRuleRegistry.Register(key, () => new LobbyRule<int>(mod, key, title, Math.Clamp(defaultValue, lo, hi), description, visible, affectsSimulation,
					v => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
					raw => int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? (true, Math.Clamp(v, lo, hi)) : (false, 0)));
				rule.Min = lo; rule.Max = hi; rule.Step = Math.Max(1, step);
				return rule;
			}

			/// <summary>
			/// A text value the host sets, for data a mod's own window edits (for example many changed values in one compact string). It is synced,
			/// frozen and checked like every rule, but rule screens (ModBridge) draw no row for it. Steam keeps up to about 8 KB per value.
			/// </summary>
			[MethodImpl(MethodImplOptions.NoInlining)]
			public static LobbyRule<string> Text(string key, string title, string defaultValue = "", string description = null, Func<bool> visible = null,
			                                     bool affectsSimulation = true) {
				var mod = LobbyMods.Identify(Assembly.GetCallingAssembly());
				return LobbyRuleRegistry.Register(key, () => new LobbyRule<string>(mod, key, title, defaultValue ?? "", description, visible, affectsSimulation,
					v => v ?? "", raw => (true, raw ?? "")));
			}

			/// <summary>
			/// One of a few named choices, stored as its number (0 = the first). Screens show a drop-down with <paramref name="choices"/>.
			/// Use it instead of several toggles that exclude each other.
			/// </summary>
			[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
			public static LobbyRule<int> Choice(string key, string title, int defaultIndex, IReadOnlyList<string> choices, string description = null,
			                                    Func<bool> visible = null, bool affectsSimulation = true) {
				if (choices == null || choices.Count == 0) throw new ArgumentException("at least one choice is needed", nameof(choices));
				var mod = LobbyMods.Identify(Assembly.GetCallingAssembly());
				int hi = choices.Count - 1;
				var rule = LobbyRuleRegistry.Register(key, () => new LobbyRule<int>(mod, key, title, Math.Clamp(defaultIndex, 0, hi), description, visible, affectsSimulation,
					v => v.ToString(System.Globalization.CultureInfo.InvariantCulture),
					raw => int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? (true, Math.Clamp(v, 0, hi)) : (false, 0)));
				rule.Min = 0; rule.Max = hi; rule.Step = 1; rule.Choices = new List<string>(choices);
				return rule;
			}
		}

		/// <summary>Who in the lobby is missing a mod whose rule the host has changed.</summary>
		public static class Compatibility {
			/// <summary>One line per member and mod: "&lt;name&gt; does not have &lt;mod&gt;, this rule will be off for everyone in the match." Empty when everybody is fine (and always during a match).</summary>
			public static IReadOnlyList<string> Warnings { get { Core.Ensure(); return LobbyRuleRegistry.Warnings(); } }

			/// <summary>True when <see cref="Warnings"/> is empty.</summary>
			public static bool IsCompatible => Warnings.Count == 0;
		}
	}
}
