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

	public static ModCivilization Add(string key, string name, Func<GameTechTree> techTree, int skinsLike) {
		int id = AllocateId(key);
		var replaced = Items.Find(c => c.Id == id);
		if (replaced != null && replaced.Name != name) CoreLog.Warn($"civilization id {id} is already used by '{replaced.Name}' - '{name}' replaces it.");
		if (replaced != null) { Items.Remove(replaced); ModCore.Civilizations.RaiseRemoved(replaced); }
		var civ = new ModCivilization(key, id, name, techTree, skinsLike);
		Items.Add(civ);
		LobbyState.PublishMods();   // the others learn that this client knows the civilization
		ModCore.Civilizations.RaiseAdded(civ);
		return civ;
	}

	public static void Remove(ModCivilization civ) { if (Items.Remove(civ)) { LobbyState.PublishMods(); ModCore.Civilizations.RaiseRemoved(civ); } }

	public static bool TryGetSkinsLike(int id, out int like) {
		foreach (var c in Items) if (c.Id == id) { like = c.SkinsLike; return true; }
		like = 0;
		return false;
	}

	public static bool TryGetTree(int id, out GameTechTree tree) {
		tree = null;
		foreach (var c in Items) {
			if (c.Id != id || c.TechTree == null) continue;
			CoreLog.Once("civ:treeprovider:" + id, $"tech tree of '{c.Name}' ({id}) requested, asking its provider");
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
	public static class Civilizations {
		public static IReadOnlyList<ModCivilization> All { get { Core.Ensure(); return CivilizationRegistry.All; } }

		public static event Action<ModCivilization> Added;
		public static event Action<ModCivilization> Removed;
		internal static void RaiseAdded(ModCivilization civ) => Raise(Added, civ, "Civilizations.Added");
		internal static void RaiseRemoved(ModCivilization civ) => Raise(Removed, civ, "Civilizations.Removed");

		private static void Raise(Action<ModCivilization> handler, ModCivilization civ, string what) {
			if (handler == null) return;
			foreach (Action<ModCivilization> d in handler.GetInvocationList()) {
				try { d(civ); }
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
		public static ModCivilization Add(string key, string name, Func<GameTechTree> techTree = null, int skinsLike = 0) {
			Core.Ensure();
			if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));
			if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
			return CivilizationRegistry.Add(key, name, techTree, skinsLike);
		}

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
