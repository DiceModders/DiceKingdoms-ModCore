using System;
using System.Collections.Generic;
using System.Text;
using GameBuildingType = DiceKingdoms.Game.BuildingType;
using GameBuildingTypeName = DiceKingdoms.Game.BuildingTypeName;
using GameBuildingTypes = DiceKingdoms.Game.BuildingTypes;
using GameTechnologies = DiceKingdoms.Game.Technologies;
using GameTechnology = DiceKingdoms.Game.Technology;
using GameTechnologyName = DiceKingdoms.Game.TechnologyName;

namespace DiceModders.ModCore;

/// <summary>Resources as the game counts them (<c>ResourceAmount</c>); a null field keeps the value below.</summary>
public sealed class ResourceChange {
	public int? Food, Wood, Stone, Gold, Hammer, Culture, Military, Disaster;
	internal int?[] All => new[] { Food, Wood, Stone, Gold, Hammer, Culture, Military, Disaster };
}

/// <summary>The game's resource amounts (food, wood, stone, gold, hammer, culture, military, disaster).</summary>
public sealed record ResourceValues(int Food, int Wood, int Stone, int Gold, int Hammer, int Culture, int Military, int Disaster);

/// <summary>What is changed on a building type; null keeps the value below.</summary>
public sealed class BuildingChange {
	public ResourceChange Cost, StorageCapacity, PillageReward, RemainsReward;
	public int? Health, TowerRange, TowerPeriod;
}

/// <summary>A building type's values as the game keeps them (<c>BuildingType</c>, game v1.0.4). Tower values exist only for towers.</summary>
public sealed record BuildingValues(ResourceValues Cost, ResourceValues StorageCapacity, ResourceValues PillageReward, ResourceValues RemainsReward,
                                    int Health, int Tier, bool IsTower, int TowerRange, int TowerPeriod);

/// <summary>One mod's building / technology changes with its host switch. Made by <see cref="ModCore.Balance.Register"/>.</summary>
public sealed class BalanceMod {
	internal readonly string Key;
	internal readonly int Priority;
	internal readonly Dictionary<int, BuildingChange> Buildings = new();
	internal readonly Dictionary<int, ResourceChange> TechCosts = new();
	internal bool? Frozen;

	/// <summary>The host's switch for this mod's changes (on by default).</summary>
	public LobbyRule<bool> Rule { get; }
	public string ModKey => Key;
	public int ModPriority => Priority;
	public bool IsActive => GameBalance.Active(this);
	public IReadOnlyDictionary<int, BuildingChange> BuildingChanges => new Dictionary<int, BuildingChange>(Buildings);
	public IReadOnlyDictionary<int, ResourceChange> TechnologyCostChanges => new Dictionary<int, ResourceChange>(TechCosts);

	internal BalanceMod(string key, int priority, LobbyRule<bool> rule) { Key = key; Priority = priority; Rule = rule; }

	public BalanceMod Set(GameBuildingTypeName building, BuildingChange change) { if (change == null) throw new ArgumentNullException(nameof(change)); return Later(() => Buildings[(int)building] = change); }
	public BalanceMod SetCost(GameTechnologyName technology, ResourceChange cost) { if (cost == null) throw new ArgumentNullException(nameof(cost)); return Later(() => TechCosts[(int)technology] = cost); }
	public BalanceMod Clear(GameBuildingTypeName building) => Later(() => Buildings.Remove((int)building));
	public BalanceMod Clear(GameTechnologyName technology) => Later(() => TechCosts.Remove((int)technology));
	public BalanceMod ClearAll() => Later(() => { Buildings.Clear(); TechCosts.Clear(); });

	private BalanceMod Later(Action change) {
		if (LobbyState.MatchRunning) { GameBalance.Pending.Add(change); CoreLog.Info("balance of '" + Key + "' changes when the match ends"); return this; }
		change();
		GameBalance.Recompute("changed by " + Key);
		return this;
	}
}

/// <summary>
/// Buildings and technologies are shared game objects (one per type, for every player), so their values are changed in place and put back:
/// every time the set of active mods changes (lobby rule, mod switch, members, match start/end) the originals are restored and the active
/// mods applied again, lowest priority first. While a match runs nothing changes.
/// </summary>
internal static class GameBalance {
	internal static readonly List<BalanceMod> Mods = new();
	internal static readonly List<Action> Pending = new();
	private static readonly Dictionary<IntPtr, byte[]> Originals = new();   // object -> its original value bytes
	private static string _applied = "";

	// BuildingType (dump.cs, game v1.0.4): storageCapacity 0x38, cost 0x58 (ResourceAmount = 8 ints), tier 0x78, health 0x80, pillageReward 0x88,
	// remainsReward 0xA8, tower Opt<Tower> hasValue 0xC8, range 0xCC, period 0xD0. Technology: cost 0x14.
	private const int BStart = 0x38, BEnd = 0xD4, Storage = 0x38, Cost = 0x58, Tier = 0x78, Health = 0x80, Pillage = 0x88, Remains = 0xA8,
	                  HasTower = 0xC8, TowerRange = 0xCC, TowerPeriod = 0xD0, TStart = 0x14, TEnd = 0x34, TCost = 0x14;

	internal static bool Active(BalanceMod m) {
		if (!ModSwitches.IsOn(m.Rule.ModId)) { m.Frozen = null; return false; }
		if (!LobbyState.InLobby) { m.Frozen = null; return m.Rule.Value; }
		if (LobbyState.MatchRunning) return m.Frozen ??= m.Rule.Value && BuiltInChanges.EveryoneHas(m.Rule);
		m.Frozen = null;
		return m.Rule.Value && BuiltInChanges.EveryoneHas(m.Rule);
	}

	internal static void MatchEnded() {
		foreach (var a in Pending) { try { a(); } catch (Exception e) { CoreLog.Error("balance: deferred change failed: " + e); } }
		Pending.Clear();
		Recompute("match ended");
	}

	/// <summary>Restores the originals and applies the active mods, if the active set changed. Cheap when nothing changed (one string compare).</summary>
	internal static void Recompute(string why) {
		if (Mods.Count == 0 && Originals.Count == 0) return;
		using var slow = CoreLog.Slow("balance recompute");
		try {
			var active = new List<BalanceMod>();
			var sig = new StringBuilder();
			foreach (var m in Mods) if (Active(m)) { active.Add(m); sig.Append(m.Key).Append(':').Append(m.Buildings.Count).Append('/').Append(m.TechCosts.Count).Append(';'); }
			string s = sig.ToString();
			if (s == _applied && why != "forced") return;
			var buildings = BuildingTypes();
			var techs = Technologies();
			if (buildings == null && techs == null) return;   // before the game data has loaded: GameData calls again

			foreach (var kv in Originals) Write(kv.Key, kv.Value);   // back to the game's values
			active.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : string.CompareOrdinal(a.Key, b.Key));
			if (buildings != null)
				foreach (var bt in buildings) {
					if (bt == null) continue;
					IntPtr p = bt.Pointer;
					foreach (var m in active) {
						if (!m.Buildings.TryGetValue((int)bt.name, out var c)) continue;
						Remember(p, BStart, BEnd);
						Res(p, Cost, c.Cost); Res(p, Storage, c.StorageCapacity); Res(p, Pillage, c.PillageReward); Res(p, Remains, c.RemainsReward);
						if (c.Health is int h) Tools.Mem.WriteInt(p, Health, Math.Max(1, h));
						bool tower = (Tools.Mem.ReadInt(p, HasTower) & 0xFF) != 0;
						if (tower && c.TowerRange is int tr) Tools.Mem.WriteInt(p, TowerRange, Math.Max(1, tr));
						if (tower && c.TowerPeriod is int tp) Tools.Mem.WriteInt(p, TowerPeriod, Math.Max(1, tp));
					}
				}
			if (techs != null)
				foreach (var t in techs) {
					if (t == null) continue;
					foreach (var m in active) {
						if (!m.TechCosts.TryGetValue((int)t.name, out var c)) continue;
						Remember(t.Pointer, TStart, TEnd);
						Res(t.Pointer, TCost, c);
					}
				}
			_applied = s;
			CoreLog.Info("balance: " + (active.Count == 0 ? "game values" : active.Count + " mod(s) applied") + " (" + why + ")");
		}
		catch (Exception e) { CoreLog.Error("balance: could not apply building/technology values: " + e); }
	}

	private static void Res(IntPtr p, int offset, ResourceChange c) {
		if (c == null) return;
		var v = c.All;
		for (int i = 0; i < 8; i++) if (v[i] is int x) Tools.Mem.WriteInt(p, offset + 4 * i, Math.Max(0, x));
	}

	// original bytes are saved once per object, keyed by pointer (the region start is stored in the first 4 bytes)
	private static unsafe void Remember(IntPtr p, int start, int end) {
		if (Originals.ContainsKey(p)) return;
		var b = new byte[4 + end - start];
		BitConverter.GetBytes(start).CopyTo(b, 0);
		fixed (byte* d = b) Buffer.MemoryCopy((byte*)p + start, d + 4, end - start, end - start);
		Originals[p] = b;
	}

	private static unsafe void Write(IntPtr p, byte[] b) {
		int start = BitConverter.ToInt32(b, 0);
		fixed (byte* s = b) Buffer.MemoryCopy(s + 4, (byte*)p + start, b.Length - 4, b.Length - 4);
	}

	internal static GameBuildingType[] BuildingTypes() {
		var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameBuildingTypes>();
		return all.Length > 0 ? all[0].Types : null;
	}

	private static GameTechnology[] _technologies;

	/// <summary>Every technology, through the game's own lookup <c>Technologies.Get(name)</c> (it fills its table on first use; <c>Values</c> can still be empty then).</summary>
	internal static GameTechnology[] Technologies() {
		if (_technologies != null) return _technologies;
		var all = UnityEngine.Resources.FindObjectsOfTypeAll<GameTechnologies>();
		if (all.Length == 0) return null;
		var list = new List<GameTechnology>();
		foreach (GameTechnologyName n in Enum.GetValues(typeof(GameTechnologyName))) {
			try { var t = all[0].Get(n); if (t != null) list.Add(t); }
			catch { }   // a name without a technology
		}
		return list.Count > 0 ? _technologies = list.ToArray() : null;
	}

	internal static ResourceValues ReadRes(IntPtr p, int o) {
		int R(int i) => Tools.Mem.ReadInt(p, o + 4 * i);
		return new ResourceValues(R(0), R(1), R(2), R(3), R(4), R(5), R(6), R(7));
	}

	internal static BuildingValues ReadBuilding(IntPtr p) {
		bool tower = (Tools.Mem.ReadInt(p, HasTower) & 0xFF) != 0;
		return new BuildingValues(ReadRes(p, Cost), ReadRes(p, Storage), ReadRes(p, Pillage), ReadRes(p, Remains), Tools.Mem.ReadInt(p, Health),
		                          Tools.Mem.ReadInt(p, Tier), tower, tower ? Tools.Mem.ReadInt(p, TowerRange) : 0, tower ? Tools.Mem.ReadInt(p, TowerPeriod) : 0);
	}

	/// <summary>The game's own bytes for an object (from the saved originals when a mod changed it).</summary>
	internal static T Original<T>(IntPtr p, Func<IntPtr, T> read) {
		if (!Originals.TryGetValue(p, out var b)) return read(p);
		var copy = new byte[b.Length - 4 + BitConverter.ToInt32(b, 0)];
		unsafe {
			fixed (byte* c = copy) {
				Buffer.MemoryCopy((byte*)p, c, BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 0));
				fixed (byte* s = b) Buffer.MemoryCopy(s + 4, c + BitConverter.ToInt32(b, 0), b.Length - 4, b.Length - 4);
				return read((IntPtr)c);
			}
		}
	}
}

public static partial class ModCore {
	/// <summary>
	/// Building and technology values: building cost, storage, health, pillage and remains rewards, tower range and period; technology cost.
	/// <para>These are shared by every player and civilization in the game (one object per building type / technology), so changes apply to all
	/// civilizations. Same safety as <see cref="Units"/>: a mod's changes apply only with no Steam lobby, or when the host's switch for it is on and
	/// every lobby member has the same mod version (and it is switched on); otherwise everybody plays the game's values. Nothing changes during a
	/// match: changes made then apply when it ends. Several mods: lower priority first, the higher priority wins; equal priority by key.</para>
	/// </summary>
	public static class Balance {
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static BalanceMod Register(string key, string title, int priority = 0, string description = null) {
			Core.Ensure();
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));
			foreach (var m in GameBalance.Mods) if (m.Key == key) return m;
			var owner = LobbyMods.IdentifySimulation(System.Reflection.Assembly.GetCallingAssembly());
			var rule = Lobby.Rules.ToggleFor(owner, key, title, true, description ?? "This mod's building and technology values. Used only when every player has the mod; otherwise the game's values.");
			var mod = new BalanceMod(key, priority, rule);
			rule.Changed += _ => GameBalance.Recompute("lobby rule " + key);
			GameBalance.Mods.Add(mod);
			GameBalance.Recompute("registered " + key);
			return mod;
		}

		/// <summary>Every registered balance mod (for a lobby screen).</summary>
		public static IReadOnlyList<BalanceMod> Mods => GameBalance.Mods;

		/// <summary>A building type's values right now (the game's plus the active mods). Null before the game data has loaded.</summary>
		public static BuildingValues Building(GameBuildingTypeName building) {
			foreach (var bt in GameBalance.BuildingTypes() ?? Array.Empty<GameBuildingType>()) if (bt != null && bt.name == building) return GameBalance.ReadBuilding(bt.Pointer);
			return null;
		}

		/// <summary>A building type's own game values (ignoring every mod).</summary>
		public static BuildingValues DefaultBuilding(GameBuildingTypeName building) {
			foreach (var bt in GameBalance.BuildingTypes() ?? Array.Empty<GameBuildingType>()) if (bt != null && bt.name == building) return GameBalance.Original(bt.Pointer, GameBalance.ReadBuilding);
			return null;
		}

		/// <summary>A technology's cost right now / the game's own cost.</summary>
		public static ResourceValues TechnologyCost(GameTechnologyName technology) {
			foreach (var t in GameBalance.Technologies() ?? Array.Empty<GameTechnology>()) if (t != null && t.name == technology) return GameBalance.ReadRes(t.Pointer, 0x14);
			return null;
		}

		public static ResourceValues DefaultTechnologyCost(GameTechnologyName technology) {
			foreach (var t in GameBalance.Technologies() ?? Array.Empty<GameTechnology>()) if (t != null && t.name == technology) return GameBalance.Original(t.Pointer, p => GameBalance.ReadRes(p, 0x14));
			return null;
		}
	}
}
