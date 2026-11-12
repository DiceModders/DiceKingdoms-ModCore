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
	/// <summary>
	/// Fixes every rule for the match. A client uses the host's decision (<see cref="DecisionKey"/>) when it is there: the host decided with its own
	/// view of the lobby, and a client whose lobby data or member list is late (or a player who rejoins after a member without the mod left) could
	/// otherwise decide differently and build islands of another size. Without a decision every computer decides by itself, as before.
	/// </summary>
	public static void FreezeAll() {
		var decided = LobbyState.IsHost ? null : ReadDecision(LobbyState.GetLobbyData(DecisionKey));
		int used = 0;
		foreach (var r in Rules.ToArray())
			Safe(() => {
				if (decided != null && decided.TryGetValue(Clean(r.Key), out var raw) && r.FreezeFrom(raw)) used++;
				else r.Freeze();
			});
		if (decided != null) CoreLog.Info($"lobby rules: {used} value(s) taken from the host's decision");
	}

	/// <summary>Lobby data the host writes when it fixes the rules: every rule's value for this match. Cleared when the match ends.</summary>
	public const string DecisionKey = "modcore.decision";
	private const char EntrySep = '\u001e', FieldSep = '\u001f';

	public static string DescribeDecision() {
		var sb = new StringBuilder("d1");
		foreach (var r in Rules) {
			// a mod the host switched off is announced as missing: leave its rules out, so clients decide "default" by themselves as before
			if (!ModSwitches.IsOn(r.Mod.Id)) continue;
			string raw;
			try { raw = r.FrozenRaw; } catch (Exception e) { CoreLog.Once("decision:" + r.Key, "lobby rule '" + r.Key + "': " + e.Message); continue; }
			if (raw == null) continue;
			sb.Append(EntrySep).Append(Clean(r.Key)).Append(FieldSep).Append(raw.Replace(EntrySep, ' ').Replace(FieldSep, ' '));
		}
		return sb.ToString();
	}

	private static Dictionary<string, string> ReadDecision(string data) {
		if (string.IsNullOrEmpty(data) || !data.StartsWith("d1")) return null;
		var map = new Dictionary<string, string>();
		foreach (var entry in data.Split(EntrySep)) {
			int at = entry.IndexOf(FieldSep);
			if (at > 0) map[entry.Substring(0, at)] = entry.Substring(at + 1);
		}
		return map;
	}
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
			if (!ModSwitches.IsOn(r.Mod.Id)) continue;   // a mod switched off is announced as missing: everybody falls back to the game's own rules
			if (!byMod.TryGetValue(r.Mod, out var keys)) { byMod[r.Mod] = keys = new List<string>(); order.Add(r.Mod); }
			keys.Add(Clean(r.Key));
		}
		foreach (var mod in order)
			sb.Append(';').Append(Clean(mod.Id)).Append('|').Append(Clean(mod.Name)).Append('|').Append(Clean(mod.Version)).Append('|').Append(string.Join(",", byMod[mod]));
		return sb.ToString();
	}

	private static string Clean(string s) => (s ?? "").Replace(';', '_').Replace('|', '_').Replace(',', '_').Replace('@', '_');

	/// <summary>"key@version": what a member must have published for this rule (the same mod version: another version may play differently).</summary>
	public static string TagOf(ILobbyRule rule) => Clean(rule.Key) + "@" + Clean(rule.ModVersion);

	/// <summary>Rule tags ("key@version") another member published (null when it published nothing, i.e. it has no ModCore).</summary>
	public static HashSet<string> ParseKeys(string raw) {
		if (string.IsNullOrEmpty(raw) || !raw.StartsWith("v1")) return null;
		var keys = new HashSet<string>();
		var parts = raw.Split(';');
		for (int i = 1; i < parts.Length; i++) {
			var fields = parts[i].Split('|');
			if (fields.Length < 4 || fields[3].Length == 0) continue;
			foreach (var k in fields[3].Split(',')) keys.Add(k + "@" + fields[2]);
		}
		return keys;
	}

	/// <summary>The name of the first other lobby member who has not published this rule in the same mod version, or null when everybody has it.</summary>
	public static string FirstMissing(ILobbyRule rule) {
		if (!LobbyState.InLobby) return null;
		foreach (var member in LobbyState.Members)
			if (!member.IsLocal && !LobbyState.MemberHasKey(member.SteamId, TagOf(rule))) return member.Name;
		return null;
	}

	/// <summary>The first rule this match uses that the member has not published in the same mod version, or null when it can play the match.</summary>
	public static ILobbyRule FirstInUseMissing(ulong steamId) {
		foreach (var rule in Rules)
			if (rule.InUse && !LobbyState.MemberHasKey(steamId, TagOf(rule))) return rule;
		return null;
	}

	public static IReadOnlyList<string> Warnings() {
		var lines = new List<string>();
		if (!LobbyState.InLobby || LobbyState.MatchRunning) return lines;
		var seen = new HashSet<string>();
		foreach (var rule in Rules) {
			if (!rule.AffectsSimulation || !rule.IsNonDefault) continue;
			foreach (var member in LobbyState.Members) {
				if (member.IsLocal || LobbyState.MemberHasKey(member.SteamId, TagOf(rule))) continue;
				if (!seen.Add(member.SteamId + "|" + rule.Mod.Id)) continue;
				string other = LobbyState.MemberVersionOf(member.SteamId, Clean(rule.Key));
				lines.Add(other != null
					? $"{member.Name} has {rule.Mod.Name} {other}, the host has {rule.ModVersion}: this rule will be off for everyone in the match."
					: $"{member.Name} does not have {rule.Mod.Name}, this rule will be off for everyone in the match.");
			}
		}
		// same version, different file: a changed build (or a Debug build) may play differently. Only a warning: the match still uses the mod.
		foreach (var mod in LobbyMods.Simulation) {
			if (string.IsNullOrEmpty(mod.Build) || !ModSwitches.IsOn(mod.Id)) continue;
			foreach (var member in LobbyState.Members) {
				if (member.IsLocal) continue;
				var theirs = LobbyState.MemberBuildOf(member.SteamId, mod.Id);
				if (theirs == null || theirs.Value.Version != LobbyMods.Clean(mod.Version) || string.IsNullOrEmpty(theirs.Value.Build) || theirs.Value.Build == mod.Build) continue;
				lines.Add($"{member.Name} has a different build of {mod.Name} {mod.Version}: if the match desyncs, use the same files.");
			}
		}
		return lines;
	}
}
