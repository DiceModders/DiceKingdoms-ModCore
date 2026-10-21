using System;
using System.Collections.Generic;
using System.Text;

namespace DiceModders.ModCore;

/// <summary>All registered lobby rules, in registration order, and the checks that apply to all of them together.</summary>
internal static class LobbyRuleRegistry {
	private static readonly List<ILobbyRuleInternal> Rules = new();
	private static readonly Dictionary<string, ILobbyRuleInternal> ByKey = new();

	public static IReadOnlyList<ILobbyRule> All => Rules;

	public static LobbyRule<T> Register<T>(string key, Func<LobbyRule<T>> create) {
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A lobby rule needs a key.", nameof(key));
		Core.Ensure();
		if (ByKey.TryGetValue(key, out var existing)) {
			if (existing is LobbyRule<T> same) return same;
			throw new InvalidOperationException($"Lobby rule '{key}' is already registered with another type.");
		}
		var rule = create();
		Rules.Add(rule);
		ByKey[key] = rule;
		LobbyState.PublishMods();   // already in a lobby (a mod registering late): tell the others about the new key
		ModCore.Lobby.Rules.RaiseRegistered(rule);
		return rule;
	}

	public static void ReevaluateAll() { foreach (var r in Rules.ToArray()) Safe(r.Reevaluate); }
	public static void FreezeAll() { foreach (var r in Rules.ToArray()) Safe(r.Freeze); }
	public static void UnfreezeAll() { foreach (var r in Rules.ToArray()) Safe(r.Unfreeze); }

	private static void Safe(Action a) {
		try { a(); }
		catch (Exception e) { CoreLog.Error("lobby rule: " + e); }
	}

	/// <summary>"modcore.mods" member data: every mod with its rule keys, so the others can tell who does not have a rule.</summary>
	public static string DescribeMods() {
		var sb = new StringBuilder("v1");
		var byMod = new Dictionary<LobbyMod, List<string>>();
		var order = new List<LobbyMod>();
		foreach (var r in Rules) {
			if (!byMod.TryGetValue(r.Mod, out var keys)) { byMod[r.Mod] = keys = new List<string>(); order.Add(r.Mod); }
			keys.Add(Clean(r.Key));
		}
		foreach (var mod in order)
			sb.Append(';').Append(Clean(mod.Id)).Append('|').Append(Clean(mod.Name)).Append('|').Append(Clean(mod.Version)).Append('|').Append(string.Join(",", byMod[mod]));
		return sb.ToString();
	}

	private static string Clean(string s) => (s ?? "").Replace(';', '_').Replace('|', '_').Replace(',', '_');

	/// <summary>Rule keys another member published (null when it published nothing, i.e. it has no ModCore).</summary>
	public static HashSet<string> ParseKeys(string raw) {
		if (string.IsNullOrEmpty(raw) || !raw.StartsWith("v1")) return null;
		var keys = new HashSet<string>();
		var parts = raw.Split(';');
		for (int i = 1; i < parts.Length; i++) {
			var fields = parts[i].Split('|');
			if (fields.Length < 4 || fields[3].Length == 0) continue;
			foreach (var k in fields[3].Split(',')) keys.Add(k);
		}
		return keys;
	}

	public static IReadOnlyList<string> Warnings() {
		var lines = new List<string>();
		if (!LobbyState.InLobby || LobbyState.MatchRunning) return lines;
		var seen = new HashSet<string>();
		foreach (var rule in Rules) {
			if (!rule.AffectsSimulation || !rule.IsNonDefault) continue;
			foreach (var member in LobbyState.Members) {
				if (LobbyState.MemberHasKey(member.SteamId, Clean(rule.Key))) continue;
				if (seen.Add(member.SteamId + "|" + rule.Mod.Id))
					lines.Add($"{member.Name} does not have {rule.Mod.Name}, this rule may desync.");
			}
		}
		return lines;
	}
}
