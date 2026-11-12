using System;
using System.Collections.Generic;
using GameTechTree = DiceKingdoms.Game.TechTree;
using GameTechnologies = DiceKingdoms.Game.Technologies;
using GameCivilization = DiceKingdoms.Interface.Civilization;

namespace DiceModders.ModCore;

/// <summary>A civilization a mod added. <see cref="Remove"/> takes it out again.</summary>
public sealed class ModCivilization {
	internal ModCivilization(string key, int id, string name, Func<GameTechTree> techTree, int skinsLike) { Key = key; Id = id; Name = name; TechTree = techTree; SkinsLike = skinsLike; }
	public string Key { get; }
	/// <summary>The value used as <c>Civilization</c> (the game's enum has 0..4; mod civilizations get 5..127, the range the game can send over the network).</summary>
	public int Id { get; }
	public string Name { get; }
	internal Func<GameTechTree> TechTree { get; }
	/// <summary>The built-in civilization whose biome and visuals this one borrows (the game only knows how to skin 0..4).</summary>
	public int SkinsLike { get; }
	/// <summary>Own models for buildings, units, nature and ships (everything left out looks like <see cref="SkinsLike"/>).</summary>
	public CivilizationSkins Skins { get; } = new CivilizationSkins();
	public void Remove() => CivilizationRegistry.Remove(this);
	/// <summary>The plugin that added the civilization and its version: other players must have the same version to play it together.</summary>
	public string ModId { get; internal set; } = "";
	public string ModVersion { get; internal set; } = "0";
}

internal static class CivilizationRegistry {
	private static readonly List<ModCivilization> Items = new();

	public static IReadOnlyList<ModCivilization> All => Items;

	/// <summary>
	/// The game sends a player's civilization over the network as one signed byte (<c>PlayerSkinsSerialization</c> writes a byte and reads an sbyte), so a number
	/// above 127 arrives as another number. The built-in civilizations are 0..4; registered ones get 5..127.
	/// </summary>
	public const int FirstId = 5, LastId = 127;

	public static ModCivilization Find(int id) { foreach (var c in Items) if (c.Id == id) return c; return null; }

	/// <summary>Stable id from a key: the same key gives the same id on every machine (needed for multiplayer); a taken id moves to the next free one.</summary>
	public static int AllocateId(string key) {
		uint h = 2166136261;
		foreach (char ch in key) { h ^= ch; h *= 16777619; }
		int id = FirstId + (int)(h % (uint)(LastId - FirstId + 1));
		for (int tries = 0; tries <= LastId - FirstId && Items.Exists(c => c.Id == id && c.Key != key); tries++)
			id = id == LastId ? FirstId : id + 1;
		return id;
	}

	public static ModCivilization Add(string key, string name, Func<GameTechTree> techTree, int skinsLike, LobbyMod owner = null) {
		int id = AllocateId(key);
		var replaced = Items.Find(c => c.Id == id);
		if (replaced != null && replaced.Name != name) CoreLog.Warn($"civilization id {id} is already used by '{replaced.Name}' - '{name}' replaces it.");
		if (replaced != null) { Items.Remove(replaced); ModCore.Civilizations.RaiseRemoved(replaced); }
		var civ = new ModCivilization(key, id, name, techTree, skinsLike) { ModId = owner?.Id ?? "", ModVersion = owner?.Version ?? "0" };
		Items.Add(civ);
		CivilizationText.Set(id, new CivilizationName(name));
		LobbyGroupRegistry.ForCivilization(id);   // its heading in the lobby rules list
		LobbyState.PublishMods();   // the others learn that this client knows the civilization (the host also adds it to the lobby's table)
		ModCore.Civilizations.RaiseAdded(civ);
		return civ;
	}

	public static void Remove(ModCivilization civ) {
		if (!Items.Remove(civ)) return;
		CivilizationText.Set(civ.Id, null);
		CivilizationText.RemoveTexts(civ.Id);
		LobbyState.PublishMods();   // the others learn this client no longer has it; the table keeps its number (numbers never move)
		CivilizationGuard.Run();    // host: players standing on it move to the civilization it looks like
		ModCore.Civilizations.RaiseRemoved(civ);
	}

	// models/colours a mod gave a BUILT-IN civilization (0..4); null slot = untouched
	internal static readonly CivilizationSkins[] BuiltInSkins = new CivilizationSkins[5];

	/// <summary>The models/colours for a civilization number: a registered civilization's, or a reskinned built-in one's; null when there are none.</summary>
	public static CivilizationSkins SkinsOf(int id) {
		if (id >= 0 && id < BuiltInSkins.Length) return BuiltInSkins[id];
		return Find(id)?.Skins;
	}

	public static IEnumerable<CivilizationSkins> AllSkins() {
		foreach (var s in BuiltInSkins) if (s != null) yield return s;
		foreach (var c in Items) yield return c.Skins;
	}

	public static bool TryGetSkinsLike(int id, out int like) {
		foreach (var c in Items) if (c.Id == id) { like = c.SkinsLike; return true; }
		like = 0;
		return false;
	}

	public static bool TryGetTree(int id, out GameTechTree tree) {
		tree = null;
		foreach (var c in Items) {
			if (c.Id != id || c.TechTree == null) continue;
			try { tree = c.TechTree(); } catch (Exception e) { CoreLog.Error($"tech tree provider of '{c.Name}': {e}"); }
			if (tree != null) return true;
		}
		return false;
	}
}

public static partial class ModCore {
	/// <summary>
	/// Civilizations added by mods. The game only knows five civilizations (a switch over 0..4 and a list of tech trees); ModCore makes it accept more:
	/// it returns your tech tree whenever the game asks for the tree of that number and lets the player skins borrow a built-in civilization's biome.
	/// A screen that shows them (for example the Rules drop-down, see the ModBridge) listens to <see cref="Added"/>.
	/// </summary>
	public static partial class Civilizations {
		public static IReadOnlyList<ModCivilization> All { get { Core.Ensure(); return CivilizationRegistry.All; } }

		/// <summary>
		/// A civilization number to put into data other players read (a lobby value, a message): this computer's number of a mod civilization can differ
		/// from another computer's, so it is turned into the lobby host's number. Built-in civilizations (0..4) and games without a lobby are unchanged.
		/// Read such a number back with <see cref="FromLobbyNumber"/>. -1 when the host's table does not list this civilization (nobody else can know it).
		/// The player skins the game sends are translated by ModCore itself.
		/// </summary>
		public static int ToLobbyNumber(int civilization) { Core.Ensure(); return CivilizationTable.Number(civilization); }

		/// <summary>A civilization number written by another player with <see cref="ToLobbyNumber"/> -> this computer's number; -1 when this computer does not have that civilization.</summary>
		public static int FromLobbyNumber(int civilization) { Core.Ensure(); return CivilizationTable.Lookup(civilization); }

		public static event Action<ModCivilization> Added;
		public static event Action<ModCivilization> Removed;

		/// <summary>
		/// Raised once, when the game's data has loaded (main menu): every building, unit, ship and ragdoll prefab, the tech trees and the text sprite sheets
		/// exist from then on. Build your civilization's prefab copies (<see cref="Tools.Prefabs"/>) and text pictures here. Use <see cref="WhenGameDataLoaded"/>
		/// if you may subscribe late.
		/// </summary>
		public static event Action GameDataLoaded;
		public static bool IsGameDataLoaded => GameData.Loaded;

		/// <summary>Runs <paramref name="action"/> now if the game's data is loaded, otherwise once when it is.</summary>
		public static void WhenGameDataLoaded(Action action) {
			Core.Ensure();
			if (action == null) return;
			if (GameData.Loaded) { try { action(); } catch (Exception e) { CoreLog.Error("GameDataLoaded handler failed: " + e); } }
			else GameDataLoaded += action;
		}

		internal static void RaiseGameDataLoaded() {
			var handler = GameDataLoaded;
			if (handler != null) {
				foreach (Action d in handler.GetInvocationList()) {
					try { using (CoreLog.Slow("handler " + CoreLog.NameOf(d))) d(); }
					catch (Exception e) { CoreLog.Error("GameDataLoaded handler failed: " + e); }
				}
			}
			GameBalance.Recompute("game data loaded");
			Tools.DiskCache.Prune();   // the mods have asked for their caches by now: remove files nobody used for a few launches
		}
		internal static void RaiseAdded(ModCivilization civ) => Raise(Added, civ, "Civilizations.Added");
		internal static void RaiseRemoved(ModCivilization civ) => Raise(Removed, civ, "Civilizations.Removed");

		private static void Raise(Action<ModCivilization> handler, ModCivilization civ, string what) {
			if (handler == null) return;
			foreach (Action<ModCivilization> d in handler.GetInvocationList()) {
				try { using (CoreLog.Slow("handler " + CoreLog.NameOf(d))) d(civ); }
				catch (Exception e) { CoreLog.Error(what + " handler failed: " + e); }
			}
		}

		/// <param name="key">Unique, stable name of the civilization, e.g. "authorname.civname". It becomes a fixed id (5..127, the range the game can send over the network), the same on every machine, so multiplayer and saves agree.</param>
		/// <param name="name">Display name.</param>
		/// <param name="techTree">
		/// Returns the <c>TechTree</c> of that civilization. The game looks trees up in its own list (and throws for an unknown civilization), so this is used
		/// whenever the game asks for the tree. May be null if the mod adds the tree to the game's list itself.
		/// </param>
		/// <param name="skinsLike">
		/// The game sets a player's biome from the civilization with a switch over 0..4 and throws for anything else. The biome and visuals of this built-in
		/// civilization (0 Europe, 1 Middle East, 2 Viking, 3 Ancient, 4 Maya) are used instead; the civilization itself keeps its own id.
		/// </param>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static ModCivilization Add(string key, string name, Func<GameTechTree> techTree = null, int skinsLike = 0) {
			var owner = LobbyMods.Identify(System.Reflection.Assembly.GetCallingAssembly());
			Core.Ensure();
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));
			if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
			return CivilizationRegistry.Add(key, name, techTree, skinsLike, owner);
		}

		/// <summary>
		/// The name the game shows for a civilization (built-in 0..4 or registered), with optional translations by language code. Answered when the game
		/// draws the name, so it follows a language change at once. Visual and local: other players see their own names. <c>null</c> gives the game's name back
		/// (for a registered civilization: the name it was added with).
		/// </summary>
		public static void Rename(int civilization, string name, IReadOnlyDictionary<string, string> byLanguage = null) {
			Core.Ensure();
			if (name != null) { CivilizationText.Set(civilization, new CivilizationName(name, byLanguage)); return; }
			var civ = CivilizationRegistry.Find(civilization);
			CivilizationText.Set(civilization, civ != null ? new CivilizationName(civ.Name) : null);
		}

		/// <summary>
		/// Replaces one of the game's texts (its localization key, e.g. <c>unit.SOLDIER.name</c>, <c>unit.SOLDIER.lore</c>, <c>building.WINDMILL.name</c>,
		/// <c>technology.TEMPLARS.description</c>) while the local player's civilization is <paramref name="civilization"/>: in the lobby, the Rules screen and the
		/// match. Works for built-in (0..4) and registered civilizations. Answered when the game draws the text, nothing is polled. Visual and local: every player
		/// reads the texts of their own civilization, nothing is sent to others. <c>null</c> gives the game's text back.
		/// </summary>
		public static void SetText(int civilization, string key, string text, IReadOnlyDictionary<string, string> byLanguage = null) {
			Core.Ensure();
			CivilizationText.SetText(civilization, key, text != null ? new CivilizationName(text, byLanguage) : null);
		}

		/// <summary>A unit's name for a civilization (<c>unit.TYPE.name</c>); <paramref name="unit"/> is the game's unit type, e.g. <c>Unit.Type.SOLDIER</c>.</summary>
		public static void RenameUnit(int civilization, DiceKingdoms.Game.Unit.Type unit, string name, IReadOnlyDictionary<string, string> byLanguage = null)
			=> SetText(civilization, "unit." + unit + ".name", name, byLanguage);

		/// <summary>A unit's one-line description in the Rules screen (<c>unit.TYPE.lore</c>).</summary>
		public static void SetUnitLore(int civilization, DiceKingdoms.Game.Unit.Type unit, string lore, IReadOnlyDictionary<string, string> byLanguage = null)
			=> SetText(civilization, "unit." + unit + ".lore", lore, byLanguage);

		/// <summary>A building's name for a civilization (<c>building.TYPE.name</c>), e.g. <c>"WINDMILL"</c>.</summary>
		public static void RenameBuilding(int civilization, string buildingType, string name, IReadOnlyDictionary<string, string> byLanguage = null)
			=> SetText(civilization, "building." + buildingType + ".name", name, byLanguage);

		/// <summary>
		/// A copy of a built-in civilization's tech tree (0 Europe .. 4 Maya) to start a tree of your own from: change its <c>Slots</c> (which technologies appear, where, and
		/// what they unlock) and return it from the <c>techTree</c> provider. The game's tree is a data asset (technology, grid position and links per slot) and the
		/// technologies themselves are shared by every tree, so a tree only chooses and arranges them. The game asks the provider on every lookup, so
		/// call this ONCE (keep the result in a field or <c>Lazy</c>) and return the same instance each time. Call it after the game has loaded (the provider runs late enough).
		/// </summary>
		public static GameTechTree CloneTree(int fromCivilization) {
			Core.Ensure();
			if (fromCivilization < 0 || fromCivilization > 4) throw new ArgumentOutOfRangeException(nameof(fromCivilization), "only the built-in civilizations 0..4 have a tree to copy");
			var technologies = UnityEngine.Resources.FindObjectsOfTypeAll<GameTechnologies>();
			if (technologies.Length == 0) throw new InvalidOperationException("The game's technologies are not loaded yet.");
			var source = technologies[0].TechTree((GameCivilization)fromCivilization);
			var copy = UnityEngine.Object.Instantiate(source);
			copy.name = source.name + " (mod copy)";
			copy.hideFlags = UnityEngine.HideFlags.HideAndDontSave;   // Unity must not unload the copy when a scene changes
			return copy;
		}
	}
}
