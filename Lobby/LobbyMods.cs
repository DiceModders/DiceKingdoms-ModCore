using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Unity.IL2CPP;

namespace DiceModders.ModCore;

/// <summary>The mod a rule belongs to: the BepInEx plugin whose assembly registered it.</summary>
internal sealed record LobbyMod(string Id, string Name, string Version);

internal static class LobbyMods {
	private static readonly Dictionary<string, LobbyMod> Cache = new();

	/// <summary>Finds the BepInEx plugin that was loaded from <paramref name="assembly"/> (its name and version come from its [BepInPlugin]).</summary>
	public static LobbyMod Identify(Assembly assembly) {
		string location = null;
		try { location = assembly.Location; } catch { /* dynamic assembly */ }
		string cacheKey = location ?? assembly.FullName;
		if (Cache.TryGetValue(cacheKey, out var known)) return known;

		LobbyMod mod = null;
		try {
			foreach (var info in IL2CPPChainloader.Instance.Plugins.Values) {
				if (info?.Metadata == null || location == null) continue;
				if (string.Equals(info.Location, location, StringComparison.OrdinalIgnoreCase)) {
					mod = new LobbyMod(info.Metadata.GUID, info.Metadata.Name, info.Metadata.Version?.ToString() ?? "0");
					break;
				}
			}
		}
		catch (Exception e) { CoreLog.Once("lobbymods", "lobby rules: could not look the plugin up, using the assembly name: " + e.Message); }

		if (mod == null) {
			var name = assembly.GetName();
			mod = new LobbyMod(name.Name, name.Name, name.Version?.ToString() ?? "0");
		}
		Cache[cacheKey] = mod;
		return mod;
	}
}
