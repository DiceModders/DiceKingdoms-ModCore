using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine.Localization.Settings;
using GameLocalized = DiceKingdoms.Interface.ITextElement.Localized;

namespace DiceModders.ModCore;

/// <summary>
/// The game writes a civilization's name from the localization key <c>civilization.{value}.name</c> (<c>civilization.EUROPE.name</c> for a built-in one,
/// <c>civilization.14.name</c> for a registered one), rendered by <c>ITextElement.Localized.Render</c>. That getter is hooked: for a registered civilization,
/// or any civilization a mod renamed, it returns the mod's name in the current language. Nothing is added to the game's text tables and nothing is polled;
/// the name is answered at the moment the game draws it, in whatever language is selected.
/// </summary>
internal static class CivilizationText {
	private const string Prefix = "civilization.", Suffix = ".name";
	private static readonly Dictionary<string, CivilizationName> Names = new();   // middle part of the key ("VIKING" or "14") -> name

	/// <summary>The middle part of the game's key: the enum name for a built-in civilization, the number for a registered one.</summary>
	public static string KeyPart(int id) => id >= 0 && id <= 4 ? ((DiceKingdoms.Interface.Civilization)id).ToString() : id.ToString();

	public static void Set(int id, CivilizationName name) {
		if (name == null) Names.Remove(KeyPart(id)); else Names[KeyPart(id)] = name;
	}

	// any game text key ("unit.SOLDIER.name") -> civilization -> text, used while the local player's civilization is that one
	private static readonly Dictionary<string, Dictionary<int, CivilizationName>> Texts = new(StringComparer.Ordinal);

	public static void SetText(int civilization, string key, CivilizationName text) {
		if (string.IsNullOrEmpty(key)) throw new ArgumentException("empty text key", nameof(key));
		if (!Texts.TryGetValue(key, out var byCiv)) {
			if (text == null) return;
			Texts[key] = byCiv = new Dictionary<int, CivilizationName>();
		}
		if (text == null) { byCiv.Remove(civilization); if (byCiv.Count == 0) Texts.Remove(key); }
		else byCiv[civilization] = text;
	}

	public static void RemoveTexts(int civilization) {
		var empty = new List<string>();
		foreach (var kv in Texts) { kv.Value.Remove(civilization); if (kv.Value.Count == 0) empty.Add(kv.Key); }
		foreach (var k in empty) Texts.Remove(k);
	}

	// the open Rules window (OnEnable/OnDisable postfixes): it shows the civilization picked in its own drop-down, not necessarily the player's
	private static DiceKingdoms.Meta.Rules _rules;
	public static void RulesOpened(DiceKingdoms.Meta.Rules __instance) => _rules = __instance;
	public static void RulesClosed(DiceKingdoms.Meta.Rules __instance) { if (_rules != null && _rules.Pointer == __instance.Pointer) _rules = null; }

	/// <summary>The civilization picked in the open Rules window, or -1.</summary>
	internal static int RulesCivilization() {
		try { var rs = _rules != null ? _rules.Skins : null; return rs != null ? (int)rs.civilization : -1; }
		catch { return -1; }
	}

	/// <summary>The civilization the texts are shown for: the one picked in an open Rules window, else the local player's (lobby, match). -1 when there is none.</summary>
	private static int ViewerCivilization() {
		try {
			if (_rules != null) { var rs = _rules.Skins; if (rs != null) return (int)rs.civilization; }
		}
		catch { _rules = null; }
		try {
			var local = DiceKingdoms.Game.Player.LocalPlayer;
			var skins = local != null ? local.Skins : null;
			return skins != null ? (int)skins.civilization : -1;
		}
		catch { return -1; }
	}

	public static void Install(Harmony harmony) {
		try {
			var getter = AccessTools.PropertyGetter(typeof(GameLocalized), "Render");
			if (getter == null) { CoreLog.Warn("ITextElement.Localized.Render not found; civilization names are not shown"); return; }
			harmony.Patch(getter, prefix: new HarmonyMethod(typeof(CivilizationText), nameof(RenderPrefix)));
			var opened = AccessTools.Method(typeof(DiceKingdoms.Meta.Rules), "OnEnable");
			var closed = AccessTools.Method(typeof(DiceKingdoms.Meta.Rules), "OnDisable");
			if (opened != null) harmony.Patch(opened, postfix: new HarmonyMethod(typeof(CivilizationText), nameof(RulesOpened)));
			if (closed != null) harmony.Patch(closed, postfix: new HarmonyMethod(typeof(CivilizationText), nameof(RulesClosed)));
		}
		catch (Exception e) { CoreLog.Error("civilization name hook failed: " + e); }
	}

	public static bool RenderPrefix(GameLocalized __instance, ref string __result) {
		try {
			if (Names.Count == 0 && Texts.Count == 0) return true;
			string key = __instance.key;
			if (key == null) return true;
			if (Texts.TryGetValue(key, out var byCiv) && byCiv.TryGetValue(ViewerCivilization(), out var text)) {
				__result = text.For(Language());
				return false;
			}
			if (Names.Count == 0 || key.Length <= Prefix.Length + Suffix.Length || !key.StartsWith(Prefix, StringComparison.Ordinal) || !key.EndsWith(Suffix, StringComparison.Ordinal)) return true;
			if (!Names.TryGetValue(key.Substring(Prefix.Length, key.Length - Prefix.Length - Suffix.Length), out var name)) return true;
			__result = name.For(Language());
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:text", "civilization name hook: " + e.Message); return true; }
	}

	private static string Language() {
		try { return LocalizationSettings.SelectedLocale?.Identifier.Code ?? ""; }
		catch { return ""; }
	}
}

/// <summary>A civilization's display name: one default and optional translations by language code ("de", "fr", "ru", "zh-CN", ...).</summary>
public sealed class CivilizationName {
	public CivilizationName(string name, IReadOnlyDictionary<string, string> byLanguage = null) {
		Default = name ?? throw new ArgumentNullException(nameof(name));
		if (byLanguage != null) foreach (var kv in byLanguage) if (!string.IsNullOrEmpty(kv.Value)) ByLanguage[kv.Key] = kv.Value;
	}
	public string Default { get; }
	public Dictionary<string, string> ByLanguage { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The exact language ("zh-CN"), then its base ("zh"), then the default.</summary>
	public string For(string code) {
		if (string.IsNullOrEmpty(code)) return Default;
		if (ByLanguage.TryGetValue(code, out var s)) return s;
		int dash = code.IndexOf('-');
		if (dash > 0 && ByLanguage.TryGetValue(code.Substring(0, dash), out s)) return s;
		foreach (var kv in ByLanguage) if (kv.Key.Split('-')[0].Equals(code.Split('-')[0], StringComparison.OrdinalIgnoreCase)) return kv.Value;
		return Default;
	}
}
