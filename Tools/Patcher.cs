using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// Applies Harmony patches to game methods looked up by type name + method name, and logs <c>[patch] OK</c> / <c>[patch] FAIL</c> per patch
/// with a hint when a type or method was renamed. A failed patch never throws: the mod keeps loading without that one feature.
/// Patch methods must be static; <c>host</c> is the class that contains them.
/// </summary>
public sealed class Patcher {
	private readonly Harmony _harmony;
	private readonly ManualLogSource _log;
	private bool _describedTypes;

	/// <summary>Patches applied so far.</summary>
	public int Applied { get; private set; }
	/// <summary>Patches that could not be applied.</summary>
	public int Failed { get; private set; }
	/// <summary>The Harmony instance, for anything this helper does not cover (unpatching, attribute patches).</summary>
	public Harmony Harmony => _harmony;

	/// <param name="harmonyId">Unique id, usually your plugin GUID.</param>
	/// <param name="log">Your plugin's logger; the OK/FAIL lines go there.</param>
	public Patcher(string harmonyId, ManualLogSource log) {
		_harmony = new Harmony(harmonyId);
		_log = log;
	}

	/// <summary>Patches <paramref name="typeName"/>.<paramref name="method"/> (private methods too) with the named static methods of <paramref name="host"/>.</summary>
	/// <returns><c>true</c> when the patch was applied.</returns>
	public bool Patch(string typeName, string method, Type host, string prefix = null, string postfix = null) {
		string label = typeName + "." + method;
		try {
			var type = GameTypes.Find(typeName);
			if (type == null) {
				if (!_describedTypes) {
					_describedTypes = true;
					_log?.LogWarning("[types] " + GameTypes.Describe());
				}
				string similar = GameTypes.Suggest(typeName);
				return Fail(label, "type not found in interop assemblies" + (similar.Length > 0 ? " (similar: " + similar + ")" : ""));
			}
			return Patch(type, method, host, prefix, postfix);
		}
		catch (Exception e) {
			return Fail(label, e.GetType().Name + ": " + e.Message);
		}
	}

	/// <summary>Same as the string overload, for a type you can name in code (<c>typeof(GameState)</c>).</summary>
	public bool Patch(Type type, string method, Type host, string prefix = null, string postfix = null) {
		string label = (type?.Name ?? "?") + "." + method;
		try {
			if (type == null) return Fail(label, "type is null");
			if (host == null) return Fail(label, "host class is null");
			MethodBase target = AccessTools.Method(type, method);
			if (target == null) {
				string similar = GameTypes.SuggestMethods(type, method);
				return Fail(label, "method not found" + (similar.Length > 0 ? " (similar on " + type.Name + ": " + similar + ")" : ""));
			}
			HarmonyMethod pre = null, post = null;
			if (prefix != null) {
				var m = AccessTools.Method(host, prefix);
				if (m == null) return Fail(label, "prefix '" + prefix + "' missing in " + host.Name);
				pre = new HarmonyMethod(m);
			}
			if (postfix != null) {
				var m = AccessTools.Method(host, postfix);
				if (m == null) return Fail(label, "postfix '" + postfix + "' missing in " + host.Name);
				post = new HarmonyMethod(m);
			}
			_harmony.Patch(target, pre, post);
			Applied++;
			_log?.LogInfo("[patch] OK   " + label);
			return true;
		}
		catch (Exception e) {
			return Fail(label, e.GetType().Name + ": " + e.Message);
		}
	}

	/// <summary>
	/// A patch that is only in the game while it is switched on: <c>var s = patcher.Switch(typeof(IslandGrid), "CanPlaceBuilding", typeof(MyPatches),
	/// postfix: "After"); s.On = option.Value;</c>. Use it for methods the game calls very often (placement checks, AI scoring, every tile): a
	/// Harmony patch costs a trip from the game into C# and back on EVERY call, even when its body returns at once. Switch it from the event that
	/// changes the condition (a setting changed, the match started), never every frame. Starts off; the method is looked up now and logged like
	/// <see cref="Patch(Type, string, Type, string, string)"/>.
	/// </summary>
	public PatchSwitch Switch(Type type, string method, Type host, string prefix = null, string postfix = null) {
		string label = (type?.Name ?? "?") + "." + method;
		try {
			MethodBase target = type == null ? null : AccessTools.Method(type, method);
			if (target == null) { Fail(label, "method not found"); return new PatchSwitch(null, null, null, null, label, _log); }
			var pre = prefix == null ? null : AccessTools.Method(host, prefix);
			var post = postfix == null ? null : AccessTools.Method(host, postfix);
			if ((prefix != null && pre == null) || (postfix != null && post == null)) {
				Fail(label, "patch method missing in " + host?.Name);
				return new PatchSwitch(null, null, null, null, label, _log);
			}
			_log?.LogInfo("[patch] ready " + label + " (switched on when needed)");
			return new PatchSwitch(_harmony, target, pre, post, label, _log);
		}
		catch (Exception e) {
			Fail(label, e.GetType().Name + ": " + e.Message);
			return new PatchSwitch(null, null, null, null, label, _log);
		}
	}

	/// <summary>Same as the Type overload, with the game type looked up by name.</summary>
	public PatchSwitch Switch(string typeName, string method, Type host, string prefix = null, string postfix = null) =>
		Switch(GameTypes.Find(typeName), method, host, prefix, postfix);

	private bool Fail(string label, string why) {
		Failed++;
		_log?.LogWarning("[patch] FAIL " + label + " - " + why);
		return false;
	}

	/// <summary>Logs "<c>[patch] n/m patches active.</c>".</summary>
	public void Summary() => _log?.LogInfo("[patch] " + Applied + "/" + (Applied + Failed) + " patches active.");
}
