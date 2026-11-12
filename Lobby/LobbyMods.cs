using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Unity.IL2CPP;

namespace DiceModders.ModCore;

/// <summary>
/// The mod a rule belongs to: the BepInEx plugin whose assembly registered it. <see cref="Build"/> is a fingerprint of its DLL file, so two players
/// with the same version but different files (a changed build) can be told apart.
/// </summary>
internal sealed record LobbyMod(string Id, string Name, string Version, string Build = "");

internal static class LobbyMods {
	private static readonly Dictionary<string, LobbyMod> Cache = new();
	private static readonly List<LobbyMod> SimulationMods = new();

	/// <summary>Finds the BepInEx plugin that was loaded from <paramref name="assembly"/> (its name and version come from its [BepInPlugin]).</summary>
	public static LobbyMod Identify(Assembly assembly) {
		string location = null;
		try { location = assembly.Location; } catch { /* dynamic assembly */ }
		string cacheKey = location ?? assembly.FullName;
		if (Cache.TryGetValue(cacheKey, out var known)) return known;

		LobbyMod mod = null;
		string build = Fingerprint(location);
		try {
			foreach (var info in IL2CPPChainloader.Instance.Plugins.Values) {
				if (info?.Metadata == null || location == null) continue;
				if (string.Equals(info.Location, location, StringComparison.OrdinalIgnoreCase)) {
					mod = new LobbyMod(info.Metadata.GUID, info.Metadata.Name, info.Metadata.Version?.ToString() ?? "0", build);
					break;
				}
			}
		}
		catch (Exception e) { CoreLog.Once("lobbymods", "lobby rules: could not look the plugin up, using the assembly name: " + e.Message); }

		if (mod == null) {
			var name = assembly.GetName();
			mod = new LobbyMod(name.Name, name.Name, name.Version?.ToString() ?? "0", build);
		}
		Cache[cacheKey] = mod;
		return mod;
	}

	/// <summary>The calling mod changes the simulation (a rule, a civilization, build orders, balance): its fingerprint is compared in the lobby.</summary>
	public static LobbyMod IdentifySimulation(Assembly assembly) {
		var mod = Identify(assembly);
		MarkSimulation(mod);
		return mod;
	}

	public static void MarkSimulation(LobbyMod mod) {
		if (mod == null || SimulationMods.Contains(mod)) return;
		SimulationMods.Add(mod);
		LobbyState.PublishMods();   // already in a lobby: tell the others
	}

	/// <summary>"modcore.builds" member data: "b1" + ";id|version|fingerprint" for every mod that changes the simulation.</summary>
	public static string DescribeBuilds() {
		var sb = new StringBuilder("b1");
		foreach (var m in SimulationMods)
			if (ModSwitches.IsOn(m.Id)) sb.Append(';').Append(Clean(m.Id)).Append('|').Append(Clean(m.Version)).Append('|').Append(m.Build);
		return sb.ToString();
	}

	/// <summary>Mod id -> (version, fingerprint) another member published, or null.</summary>
	public static Dictionary<string, (string Version, string Build)> ParseBuilds(string raw) {
		if (string.IsNullOrEmpty(raw) || !raw.StartsWith("b1")) return null;
		var map = new Dictionary<string, (string, string)>();
		foreach (var part in raw.Split(';')) {
			var f = part.Split('|');
			if (f.Length == 3 && f[0].Length > 0) map[f[0]] = (f[1], f[2]);
		}
		return map;
	}

	public static IReadOnlyList<LobbyMod> Simulation => SimulationMods;

	public static string Clean(string s) => (s ?? "").Replace(';', '_').Replace('|', '_');

	/// <summary>The first 12 hex digits of the DLL's SHA-256 ("" when the file cannot be read). Read once per mod, at registration.</summary>
	private static string Fingerprint(string path) {
		if (string.IsNullOrEmpty(path)) return "";
		try {
			using var sha = SHA256.Create();
			using var file = File.OpenRead(path);
			var hash = sha.ComputeHash(file);
			var sb = new StringBuilder(12);
			for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
			return sb.ToString();
		}
		catch (Exception e) { CoreLog.Once("fingerprint:" + path, "could not fingerprint " + Path.GetFileName(path) + ": " + e.Message); return ""; }
	}
}
