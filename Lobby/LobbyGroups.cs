using System;
using System.Collections.Generic;
using System.Linq;

namespace DiceModders.ModCore;

/// <summary>
/// A heading that lobby rules are listed under, so a lobby screen can show a short list of headings and open one at a time instead of every rule at once.
/// Each civilization has one (<see cref="ModCore.Lobby.Groups.ForCivilization"/>); a mod can make its own (<see cref="ModCore.Lobby.Groups.Get"/>). Rules
/// without a group belong to the general part of the screen. Only data: how the headings are drawn and which one is open is up to the screen.
/// </summary>
public sealed class LobbyGroup {
	internal LobbyGroup(string key, string title, int order, int? civilization) { Key = key; _title = title; Order = order; Civilization = civilization; }
	private readonly string _title;

	public string Key { get; }
	/// <summary>The heading. For a civilization's group: the civilization's name (a registered one's, or a built-in one's given or game name).</summary>
	public string Title {
		get {
			if (Civilization is int c) {
				var civ = CivilizationRegistry.Find(c);
				if (civ != null) return civ.Name;
				if (c >= 0 && c <= 4) return Pretty(((DiceKingdoms.Interface.Civilization)c).ToString());
			}
			return _title;
		}
	}
	/// <summary>Sort order of the headings: built-in civilizations 100..104, registered ones 200+, own groups whatever the mod asked for.</summary>
	public int Order { get; }
	/// <summary>The civilization this group is about (built-in 0..4 or registered), null for a mod's own group. A screen can draw the civilization's localized name and icon from it.</summary>
	public int? Civilization { get; }
	/// <summary>A hint for the screen: show the group closed until clicked (default true).</summary>
	public bool StartsCollapsed { get; set; } = true;

	/// <summary>The rules in this group, in registration order (including hidden ones; check <see cref="ILobbyRule.Visible"/>).</summary>
	public IReadOnlyList<ILobbyRule> Rules => LobbyRuleRegistry.All.Where(r => r.Group == this).ToList();

	private static string Pretty(string enumName) => string.Join(" ", enumName.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant()));
}

internal static class LobbyGroupRegistry {
	private static readonly Dictionary<string, LobbyGroup> Items = new();

	public static LobbyGroup Get(string key, string title, int order, int? civilization) {
		if (Items.TryGetValue(key, out var g)) return g;
		g = new LobbyGroup(key, title, order, civilization);
		Items[key] = g;
		ModCore.Lobby.Groups.RaiseChanged();
		return g;
	}

	public static LobbyGroup ForCivilization(int civ) {
		int order = civ >= 0 && civ <= 4 ? 100 + civ : 200 + civ;
		return Get("civilization." + civ, "Civilization " + civ, order, civ);
	}

	public static IReadOnlyList<LobbyGroup> All => Items.Values.OrderBy(g => g.Order).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
}

public static partial class ModCore {
	public static partial class Lobby {
		/// <summary>Headings for lobby rules (see <see cref="LobbyGroup"/>). A rule is put under one with <see cref="LobbyRule{T}.InGroup"/>.</summary>
		public static class Groups {
			/// <summary>Every group that exists, sorted by <see cref="LobbyGroup.Order"/>. Empty groups are listed too; a screen may skip them.</summary>
			public static IReadOnlyList<LobbyGroup> All { get { Core.Ensure(); return LobbyGroupRegistry.All; } }

			/// <summary>The group of one civilization (built-in 0..4 or registered). The same object every time.</summary>
			public static LobbyGroup ForCivilization(int civilization) { Core.Ensure(); return LobbyGroupRegistry.ForCivilization(civilization); }

			/// <summary>A mod's own group (the same object for the same key).</summary>
			public static LobbyGroup Get(string key, string title, int order = 500) {
				Core.Ensure();
				if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required", nameof(key));
				return LobbyGroupRegistry.Get("mod." + key, title ?? key, order, null);
			}

			/// <summary>A group was created or a rule moved into a group: a lobby screen rebuilds its list.</summary>
			public static event Action Changed;
			internal static void RaiseChanged() {
				var handler = Changed;
				if (handler == null) return;
				foreach (Action d in handler.GetInvocationList()) {
					try { d(); } catch (Exception e) { CoreLog.Error("Lobby.Groups.Changed handler failed: " + e); }
				}
			}
		}
	}
}
