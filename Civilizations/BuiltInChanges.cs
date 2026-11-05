using System;
using GameTechTree = DiceKingdoms.Game.TechTree;

namespace DiceModders.ModCore;

/// <summary>
/// Changes to the five BUILT-IN civilizations. Two kinds, with different safety rules:
/// <list type="bullet">
/// <item>Looks (models, icons, island colours, texts): <see cref="ModCore.Civilizations.Reskin"/>. Visual and local, so nothing is synced and players
/// with or without the mod can play together. Anything a reskin leaves out keeps the game's original.</item>
/// <item>Gameplay (the tech tree): <see cref="ModCore.Civilizations.ReplaceTree"/>. Used only when it is safe: with no Steam lobby (local game), or in a
/// lobby when the host's rule is on AND every member has the mod; otherwise everybody plays the game's original tree. The decision is frozen when the
/// match starts, so it never changes during a match.</item>
/// </list>
/// </summary>
internal static class BuiltInChanges {
	internal sealed class TreeReplacement {
		public Func<GameTechTree> Provider;
		public LobbyRule<bool> Rule;
		public GameTechTree Cached;
		public bool? Frozen;
	}
	internal static readonly TreeReplacement[] Trees = new TreeReplacement[5];

	public static bool TryGetTree(int id, out GameTechTree tree) {
		tree = null;
		if (id < 0 || id >= Trees.Length) return false;
		var r = Trees[id];
		if (r == null || !Active(r)) return false;
		if (r.Cached == null) {
			try { r.Cached = r.Provider(); }
			catch (Exception e) { CoreLog.Once("builtin:tree:" + id, "replacement tech tree of " + (DiceKingdoms.Interface.Civilization)id + " failed: " + e); }
		}
		tree = r.Cached;
		return tree != null;
	}

	private static bool Active(TreeReplacement r) {
		if (!ModSwitches.IsOn(r.Rule.ModId)) { r.Frozen = null; return false; }   // switched off here
		if (!LobbyState.InLobby) { r.Frozen = null; return r.Rule.Value; }   // no Steam lobby (local game): the locally set switch
		if (LobbyState.MatchRunning) return r.Frozen ??= Live(r);
		r.Frozen = null;
		return Live(r);
	}

	private static bool Live(TreeReplacement r) => r.Rule.Value && EveryoneHas(r.Rule);

	/// <summary>Every lobby member published this rule with the same mod version (has the same mod); the local player always has it.</summary>
	internal static bool EveryoneHas(ILobbyRule rule) {
		string tag = LobbyRuleRegistry.TagOf(rule);
		foreach (var m in LobbyState.Members)
			if (!m.IsLocal && !LobbyState.MemberHasKey(m.SteamId, tag)) return false;
		return true;
	}
}

public static partial class ModCore {
	public static partial class Civilizations {
		/// <summary>
		/// New looks for a BUILT-IN civilization (0 Europe, 1 Middle East, 2 Viking, 3 Ancient, 4 Maya) without adding a civilization: the same
		/// <see cref="CivilizationSkins"/> as a mod civilization (buildings, units, nature, ship, island colours), applied whenever the game looks up
		/// that civilization's models. Visual and local (no sync needed): every player sees the looks they have installed. Everything you leave out
		/// stays the game's original. For names and texts use <see cref="Rename"/> / <see cref="SetText"/>, which also work for built-in civilizations.
		/// </summary>
		public static CivilizationSkins Reskin(int builtInCivilization) {
			Core.Ensure();
			if (builtInCivilization < 0 || builtInCivilization > 4) throw new ArgumentOutOfRangeException(nameof(builtInCivilization), "0..4 (for a mod civilization use its own Skins)");
			return CivilizationRegistry.BuiltInSkins[builtInCivilization] ??= new CivilizationSkins();
		}

		/// <summary>
		/// A different tech tree for a BUILT-IN civilization. Gameplay changes must be the same for everybody, so ModCore registers a lobby rule
		/// (<paramref name="ruleKey"/>, on by default) and uses your tree only when: there is no Steam lobby, or the host's rule is on and every member
		/// has your mod. In every other case everybody plays the original tree. The decision is frozen at match start. <paramref name="tree"/> is asked
		/// once, after the game data has loaded; start from <see cref="CloneTree"/>.
		/// </summary>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static LobbyRule<bool> ReplaceTree(int builtInCivilization, Func<GameTechTree> tree, string ruleKey, string title, string description = null) {
			Core.Ensure();
			var owner = LobbyMods.Identify(System.Reflection.Assembly.GetCallingAssembly());
			if (builtInCivilization < 0 || builtInCivilization > 4) throw new ArgumentOutOfRangeException(nameof(builtInCivilization), "0..4 (a mod civilization gives its tree to Add)");
			if (tree == null) throw new ArgumentNullException(nameof(tree));
			var rule = Lobby.Rules.ToggleFor(owner, ruleKey, title, true,
				description ?? "Uses this mod's tech tree for " + (DiceKingdoms.Interface.Civilization)builtInCivilization + ". Only when every player has the mod; otherwise the original tree.")
				.InGroup(LobbyGroupRegistry.ForCivilization(builtInCivilization));
			BuiltInChanges.Trees[builtInCivilization] = new BuiltInChanges.TreeReplacement { Provider = tree, Rule = rule };
			return rule;
		}
	}
}
