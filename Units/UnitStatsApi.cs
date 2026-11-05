using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using GameUnitStats = DiceKingdoms.Game.UnitStats;
using GameStats = DiceKingdoms.Game.UnitStats.Stats;
using GameUnitType = DiceKingdoms.Game.Unit.Type;
using GamePlayer = DiceKingdoms.Game.Player;
using GameDeployment = DiceKingdoms.Game.Deployment;

namespace DiceModders.ModCore;

/// <summary>A chance as the game stores it: numerator / denominator (0/0 = never).</summary>
public readonly record struct Chance(int Numerator, int Denominator) {
	public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;
	public override string ToString() => Numerator + "/" + Denominator;
}

/// <summary>
/// A unit type's combat values as the game keeps them (<c>UnitStats.Stats</c>, game v1.0.4). Times are simulation ticks, distances tiles.
/// <see cref="Range"/> and <see cref="MissChance"/> exist only for ranged units.
/// </summary>
public sealed record UnitStatValues(int AttackPeriod, int AttackDelay, int AggroDistance, Chance Parry, Chance BlockArrow,
                                    bool IsRanged, int Range, Chance MissChance);

/// <summary>
/// What is changed on a unit type; every field left null keeps the value below it (the game's, the civilization's default, or a lower-priority mod's).
/// <see cref="Range"/> and <see cref="MissChance"/> only apply to ranged units.
/// </summary>
public sealed class UnitStatChange {
	public int? AttackPeriod, AttackDelay, AggroDistance, Range;
	public Chance? Parry, BlockArrow, MissChance;
}

/// <summary>One mod's unit-stat changes with its host switch. Made by <see cref="ModCore.Units.Register"/>; see <see cref="ModCore.Units"/>.</summary>
public sealed class UnitStatsMod {
	internal readonly string Key;
	internal readonly int Priority;
	internal readonly Dictionary<(int civ, int type), UnitStatChange> Changes = new();
	internal bool? Frozen;

	/// <summary>The host's switch for this mod's unit stats (on by default).</summary>
	public LobbyRule<bool> Rule { get; }

	/// <summary>The mod's key (also the lobby rule's key) and priority.</summary>
	public string ModKey => Key;
	public int ModPriority => Priority;
	/// <summary>Whether the changes are used right now (see <see cref="ModCore.Units"/> for the rules).</summary>
	public bool IsActive => UnitStatsRegistry.Active(this);
	/// <summary>Every change, keyed by (civilization, unit type); civilization -1 = all civilizations. For a lobby screen that lists what mods do.</summary>
	public IReadOnlyDictionary<(int civilization, int unit), UnitStatChange> AllChanges {
		get { var d = new Dictionary<(int, int), UnitStatChange>(); foreach (var kv in Changes) d[kv.Key] = kv.Value; return d; }
	}

	internal UnitStatsMod(string key, int priority, LobbyRule<bool> rule) { Key = key; Priority = priority; Rule = rule; }

	/// <summary>Changes a unit type for EVERY civilization.</summary>
	public UnitStatsMod Set(GameUnitType unit, UnitStatChange change) => Put(-1, unit, change);

	/// <summary>Changes a unit type for ONE civilization (built-in 0..4 or a mod civilization's id). Applied after every all-civilization change.</summary>
	public UnitStatsMod Set(int civilization, GameUnitType unit, UnitStatChange change) {
		if (civilization < 0) throw new ArgumentOutOfRangeException(nameof(civilization));
		return Put(civilization, unit, change);
	}

	/// <summary>Removes a change (civilization -1: the all-civilization one).</summary>
	public UnitStatsMod Clear(int civilization, GameUnitType unit) => Later(() => Changes.Remove((civilization, (int)unit)));

	/// <summary>Removes every change of this mod.</summary>
	public UnitStatsMod ClearAll() => Later(() => Changes.Clear());

	private UnitStatsMod Put(int civ, GameUnitType unit, UnitStatChange change) {
		if (change == null) throw new ArgumentNullException(nameof(change));
		return Later(() => Changes[(civ, (int)unit)] = change);
	}

	// values must not change while a match runs (every player simulates it): a change made then waits for the end of the match
	private UnitStatsMod Later(Action change) {
		if (LobbyState.MatchRunning) { UnitStatsRegistry.Pending.Add(change); CoreLog.Info("unit stats of '" + Key + "' change when the match ends"); return this; }
		change();
		UnitStatsRegistry.Changed();
		return this;
	}
}

internal static class UnitStatsRegistry {
	internal static readonly List<UnitStatsMod> Mods = new();
	internal static readonly Dictionary<(int civ, int type), UnitStatChange> CivDefaults = new();   // a mod civilization's own values
	private static readonly Dictionary<(int civ, int type), GameStats> Built = new();
	private static string _signature = "";
	private static bool _installed;
	internal static bool Bypass;   // read the game's values

	// UnitStats.Stats layout (game v1.0.4, dump.cs): attackPeriod 0x10, attackDelay 0x14, agroDistance 0x18, parryChance 0x1C/0x20,
	// blockArrowChance 0x24/0x28, rangedOpt (Opt<Ranged>) hasValue 0x2C, range 0x30, projectile 0x34, missChance 0x38/0x3C,
	// killRewardPool 0x40, lore 0x48. The ranged values sit in a generic struct the interop does not wrap, so the numbers are read and written by offset.
	private const int AttackPeriod = 0x10, AttackDelay = 0x14, Aggro = 0x18, ParryN = 0x1C, ParryD = 0x20, BlockN = 0x24, BlockD = 0x28,
	                  HasRanged = 0x2C, Range = 0x30, MissN = 0x38, MissD = 0x3C, NumbersEnd = 0x40;

	[ThreadStatic] internal static int ContextCiv;   // civilization + 1 of the unit being made (0 = unknown)
	internal static void SetContext(int civ) => ContextCiv = civ + 1;
	internal static void ClearContext() => ContextCiv = 0;

	internal static void Changed() { Built.Clear(); _signature = ""; }

	internal static readonly List<Action> Pending = new();
	internal static void MatchEnded() {
		if (Pending.Count == 0) return;
		foreach (var a in Pending) { try { a(); } catch (Exception e) { CoreLog.Error("unit stats: deferred change failed: " + e); } }
		Pending.Clear();
		Changed();
	}

	internal static void Install() {
		if (_installed) return;
		_installed = true;
		var harmony = new Harmony("DiceModders.ModCore.unitstats");
		try { harmony.Patch(AccessTools.Method(typeof(GameUnitStats), "get_Item"), postfix: new HarmonyMethod(typeof(UnitStatsRegistry), nameof(ItemPostfix))); }
		catch (Exception e) { CoreLog.Error("unit stats: UnitStats lookup hook failed, unit stat changes are off: " + e); return; }
		int points = 0;
		foreach (var m in AccessTools.GetDeclaredMethods(typeof(DiceKingdoms.Game.CombatGrid))) {
			if (m.Name != "NewUnit") continue;
			try {
				harmony.Patch(m, prefix: new HarmonyMethod(typeof(UnitStatsRegistry), nameof(NewUnitPrefix)),
				              finalizer: new HarmonyMethod(typeof(UnitStatsRegistry), nameof(ContextFinalizer)));
				points++;
			}
			catch (Exception e) { CoreLog.Warn("unit stats: CombatGrid.NewUnit hook failed (" + e.Message + "); per-civilization values cannot apply in matches"); }
		}
		try {
			var rules = AccessTools.PropertySetter(typeof(DiceKingdoms.Meta.RulesUnit), "UnitType");
			if (rules != null) harmony.Patch(rules, prefix: new HarmonyMethod(typeof(UnitStatsRegistry), nameof(RulesPrefix)),
			                                 finalizer: new HarmonyMethod(typeof(UnitStatsRegistry), nameof(ContextFinalizer)));
		}
		catch (Exception e) { CoreLog.Warn("unit stats: Rules screen hook failed: " + e.Message); }
		CoreLog.Info("unit stats: hooks installed (" + points + " unit creation points)");
	}

	// CombatGrid.NewUnit(type, owner, position) / (type, deployment, position) / (type, owner, deployment, position): whose unit is being made
	public static void NewUnitPrefix(object[] __args) {
		try {
			GamePlayer owner = null;
			foreach (var a in __args) {
				if (a is GamePlayer p) { owner = p; break; }
				if (a is GameDeployment d && d.Owner != null) { owner = d.Owner; break; }
			}
			var skins = owner != null ? owner.Skins : null;
			if (skins != null) SetContext((int)skins.civilization); else ClearContext();
		}
		catch { ClearContext(); }
	}

	// the Rules screen shows the civilization picked in its drop-down
	public static void RulesPrefix() {
		int civ = CivilizationText.RulesCivilization();
		if (civ >= 0) SetContext(civ); else ClearContext();
	}

	public static Exception ContextFinalizer(Exception __exception) { ClearContext(); return __exception; }

	public static void ItemPostfix(GameUnitType type, ref GameStats __result) {
		try {
			if (Bypass || __result == null || (Mods.Count == 0 && CivDefaults.Count == 0)) return;
			var replaced = Resolve(ContextCiv - 1, (int)type, __result);
			if (replaced != null) __result = replaced;
		}
		catch (Exception e) { CoreLog.Once("unitstats:item", "unit stats lookup: " + e.Message); }
	}

	internal static bool Active(UnitStatsMod m) {
		if (!ModSwitches.IsOn(m.Rule.ModId)) { m.Frozen = null; return false; }   // the mod is switched off here (and announced as missing)
		if (!LobbyState.InLobby) { m.Frozen = null; return m.Rule.Value; }   // no Steam lobby (local game): the locally set switch
		if (LobbyState.MatchRunning) return m.Frozen ??= Live(m);    // decided once per match
		m.Frozen = null;
		return Live(m);
	}

	private static bool Live(UnitStatsMod m) => m.Rule.Value && BuiltInChanges.EveryoneHas(m.Rule);

	/// <summary>
	/// The game's values, then the civilization's own defaults (mod civilizations), then every active mod in a fixed order all clients agree on:
	/// all-civilization changes before per-civilization ones; within each, lower priority first (higher wins), equal priority by mod key.
	/// Null when nothing changes this unit.
	/// </summary>
	internal static GameStats Resolve(int civ, int type, GameStats original) {
		var active = new List<UnitStatsMod>();
		var sig = new StringBuilder();
		foreach (var m in Mods) if (Active(m)) { active.Add(m); sig.Append(m.Key).Append(';'); }
		if (sig.ToString() != _signature) { Built.Clear(); _signature = sig.ToString(); }
		if (Built.TryGetValue((civ, type), out var done)) return done;

		active.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : string.CompareOrdinal(a.Key, b.Key));
		var layers = new List<UnitStatChange>();
		if (civ >= 0 && CivDefaults.TryGetValue((civ, type), out var own)) layers.Add(own);
		foreach (var m in active) if (m.Changes.TryGetValue((-1, type), out var c)) layers.Add(c);
		if (civ >= 0) foreach (var m in active) if (m.Changes.TryGetValue((civ, type), out var c)) layers.Add(c);
		if (layers.Count == 0) { Built[(civ, type)] = null; return null; }

		var copy = new GameStats();
		IntPtr src = original.Pointer, dst = copy.Pointer;
		unsafe { Buffer.MemoryCopy((byte*)src + AttackPeriod, (byte*)dst + AttackPeriod, NumbersEnd - AttackPeriod, NumbersEnd - AttackPeriod); }
		copy.killRewardPool = original.killRewardPool;
		copy.lore = original.lore;
		bool ranged = (Tools.Mem.ReadInt(dst, HasRanged) & 0xFF) != 0;
		foreach (var c in layers) {
			if (c.AttackPeriod is int ap) Tools.Mem.WriteInt(dst, AttackPeriod, Math.Max(1, ap));
			if (c.AttackDelay is int ad) Tools.Mem.WriteInt(dst, AttackDelay, Math.Max(0, ad));
			if (c.AggroDistance is int ag) Tools.Mem.WriteInt(dst, Aggro, Math.Max(0, ag));
			if (c.Parry is Chance p) { Tools.Mem.WriteInt(dst, ParryN, p.Numerator); Tools.Mem.WriteInt(dst, ParryD, p.Denominator); }
			if (c.BlockArrow is Chance b) { Tools.Mem.WriteInt(dst, BlockN, b.Numerator); Tools.Mem.WriteInt(dst, BlockD, b.Denominator); }
			if (ranged && c.Range is int r) Tools.Mem.WriteInt(dst, Range, Math.Max(1, r));
			if (ranged && c.MissChance is Chance mc) { Tools.Mem.WriteInt(dst, MissN, mc.Numerator); Tools.Mem.WriteInt(dst, MissD, mc.Denominator); }
		}
		Built[(civ, type)] = copy;
		return copy;
	}

	internal static UnitStatValues Read(GameStats s) {
		IntPtr p = s.Pointer;
		int R(int o) => Tools.Mem.ReadInt(p, o);
		bool ranged = (R(HasRanged) & 0xFF) != 0;
		return new UnitStatValues(R(AttackPeriod), R(AttackDelay), R(Aggro), new Chance(R(ParryN), R(ParryD)), new Chance(R(BlockN), R(BlockD)),
		                          ranged, ranged ? R(Range) : 0, ranged ? new Chance(R(MissN), R(MissD)) : default);
	}
}

public static partial class ModCore {
	/// <summary>
	/// Unit combat values (attack speed, aggro distance, parry, arrow block, range, miss chance), for every civilization or per civilization.
	/// <para>Order, lowest first: the game's values, a mod civilization's own defaults (<see cref="SetCivilizationDefaults"/>), then the mods registered
	/// with <see cref="Register"/>: all-civilization changes, then per-civilization ones; inside each group a higher priority wins, equal priorities go by
	/// mod key, so every player computes the same result. Fields left null keep the value below.</para>
	/// <para>Safety: these values drive the simulation every player runs on their own machine, so they must match everywhere. A registered mod's
	/// changes apply only with no Steam lobby (local game), or when the host's switch for it (<see cref="UnitStatsMod.Rule"/>) is on AND every lobby
	/// member has the mod; otherwise everybody plays without them. The decision is frozen at match start. A civilization's own defaults travel with the
	/// civilization, which can only be picked when every member has it.</para>
	/// </summary>
	public static class Units {
		/// <summary>Registers a mod's unit-stat changes and its host switch (a lobby rule named <paramref name="key"/>, on by default).</summary>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static UnitStatsMod Register(string key, string title, int priority = 0, string description = null) {
			Core.Ensure();
			var owner = LobbyMods.Identify(System.Reflection.Assembly.GetCallingAssembly());   // the rule belongs to the calling mod, not ModCore
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));
			foreach (var m in UnitStatsRegistry.Mods) if (m.Key == key) return m;
			var rule = Lobby.Rules.ToggleFor(owner, key, title, true, description ?? "This mod's unit stats. Used only when every player has the mod; otherwise the game's values.");
			var mod = new UnitStatsMod(key, priority, rule);
			rule.Changed += _ => UnitStatsRegistry.Changed();
			UnitStatsRegistry.Mods.Add(mod);
			UnitStatsRegistry.Install();   // the hooks exist only once some mod changes unit stats
			return mod;
		}

		/// <summary>
		/// A mod civilization's own values for one of its unit types (its "defaults"): part of the civilization like its tech tree, so no extra switch.
		/// Every balance mod registered with <see cref="Register"/> applies on top of them. <c>null</c> removes them.
		/// </summary>
		public static void SetCivilizationDefaults(ModCivilization civilization, GameUnitType unit, UnitStatChange values) {
			Core.Ensure();
			if (civilization == null) throw new ArgumentNullException(nameof(civilization));
			if (values == null) UnitStatsRegistry.CivDefaults.Remove((civilization.Id, (int)unit));
			else UnitStatsRegistry.CivDefaults[(civilization.Id, (int)unit)] = values;
			UnitStatsRegistry.Changed();
			UnitStatsRegistry.Install();
		}

		/// <summary>Every registered unit-stat mod (for a lobby screen: what each changes, its switch, whether it is active).</summary>
		public static IReadOnlyList<UnitStatsMod> Mods => UnitStatsRegistry.Mods;

		/// <summary>A mod civilization's own values for a unit type, or null when it uses the game's (like the built-in civilizations).</summary>
		public static UnitStatChange CivilizationDefaults(int civilization, GameUnitType unit)
			=> UnitStatsRegistry.CivDefaults.TryGetValue((civilization, (int)unit), out var v) ? v : null;

		/// <summary>The game's own values for a unit type (ignoring every mod). Null before the game data has loaded.</summary>
		public static UnitStatValues Default(GameUnitType unit) {
			var s = Asset();
			if (s == null) return null;
			UnitStatsRegistry.Bypass = true;
			try { return UnitStatsRegistry.Read(s[unit]); }
			finally { UnitStatsRegistry.Bypass = false; }
		}

		/// <summary>The values a unit of this civilization gets right now (the game's plus the civilization's defaults and the active mods).</summary>
		public static UnitStatValues Effective(int civilization, GameUnitType unit) {
			var s = Asset();
			if (s == null) return null;
			int saved = UnitStatsRegistry.ContextCiv;
			UnitStatsRegistry.SetContext(civilization);
			try { return UnitStatsRegistry.Read(s[unit]); }
			finally { UnitStatsRegistry.ContextCiv = saved; }
		}

		private static GameUnitStats Asset() {
			var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameUnitStats>();
			return all.Length > 0 ? all[0] : null;
		}
	}
}
