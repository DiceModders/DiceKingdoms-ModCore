using System;
using System.Collections.Generic;
using HarmonyLib;
using GameLocalized = DiceKingdoms.Interface.ITextElement.Localized;

namespace DiceModders.ModCore;

/// <summary>A text that answers in a language (see <see cref="TextLanguage.Pick"/> for the fallback).</summary>
public interface ILocalText { string For(string language); }

/// <summary>One key of a mod's <see cref="TextSet"/>.</summary>
internal sealed class TextSetEntry : ILocalText {
	private readonly TextSet _set; private readonly string _key;
	public TextSetEntry(TextSet set, string key) { _set = set; _key = key; }
	public string For(string language) => _set.Get(_key, language);
}

/// <summary>
/// The game writes a civilization's name from the localization key <c>civilization.{value}.name</c> (<c>civilization.EUROPE.name</c> for a built-in one,
/// <c>civilization.14.name</c> for a registered one), rendered by <c>ITextElement.Localized.Render</c>. That getter is hooked: for a registered civilization,
/// or any civilization a mod renamed, it returns the mod's name in the current language; the same hook answers the other texts of a civilization and the
/// game keys a mod translates (<c>gamekey:</c> in a <see cref="TextSet"/>). Nothing is added to the game's text tables and nothing is polled;
/// a text is answered at the moment the game draws it, in whatever language is selected.
/// </summary>
internal static class CivilizationText {
	private const string Prefix = "civilization.", Suffix = ".name";
	/// <summary>The key of a civilization's own name inside its <see cref="TextSet"/>.</summary>
	public const string NameKey = "civilization.name";
	private static readonly Dictionary<string, ILocalText> Names = new();   // middle part of the key ("VIKING" or "14") -> name

	/// <summary>The middle part of the game's key: the enum name for a built-in civilization, the number for a registered one.</summary>
	public static string KeyPart(int id) => id >= 0 && id <= 4 ? ((DiceKingdoms.Interface.Civilization)id).ToString() : id.ToString();

	public static void Set(int id, ILocalText name) {
		if (name == null) Names.Remove(KeyPart(id)); else Names[KeyPart(id)] = name;
	}

	/// <summary>The civilization's name in the game's current language (Rename / SetTexts), or <paramref name="fallback"/> when it has none.</summary>
	public static string NameFor(int id, string fallback) {
		try { return Names.TryGetValue(KeyPart(id), out var name) ? name.For(TextLanguage.Current) ?? fallback : fallback; }
		catch { return fallback; }
	}

	// any game text key ("unit.SOLDIER.name") -> civilization -> text, used while the local player's civilization is that one
	private static readonly Dictionary<string, Dictionary<int, ILocalText>> Texts = new(StringComparer.Ordinal);

	public static void SetText(int civilization, string key, ILocalText text) {
		if (string.IsNullOrEmpty(key)) throw new ArgumentException("empty text key", nameof(key));
		if (!Texts.TryGetValue(key, out var byCiv)) {
			if (text == null) return;
			Texts[key] = byCiv = new Dictionary<int, ILocalText>();
		}
		if (text == null) { byCiv.Remove(civilization); if (byCiv.Count == 0) Texts.Remove(key); }
		else byCiv[civilization] = text;
	}

	/// <summary>A whole set for a civilization: its name (<see cref="NameKey"/>) and every other key as a game text key. Replaces the set given before.</summary>
	public static void SetTexts(int civilization, TextSet set) {
		RemoveTexts(civilization, onlySets: true);
		if (set == null) return;
		foreach (var key in set.Keys) {
			if (key.StartsWith(TextSet.GameLiteralPrefix, StringComparison.Ordinal) || key.StartsWith(TextSet.GameKeyPrefix, StringComparison.Ordinal)) continue;
			if (key == NameKey) Set(civilization, new TextSetEntry(set, key));
			else SetText(civilization, key, new TextSetEntry(set, key));
		}
	}

	public static void RemoveTexts(int civilization, bool onlySets = false) {
		var empty = new List<string>();
		foreach (var kv in Texts) {
			if (!onlySets || (kv.Value.TryGetValue(civilization, out var t) && t is TextSetEntry)) kv.Value.Remove(civilization);
			if (kv.Value.Count == 0) empty.Add(kv.Key);
		}
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

	/// <summary>
	/// What the game's <c>Localized.get_Render</c> does with the text it looked up (Ghidra 18058FE00): the text becomes the one hole of a <c>"{0}"</c>
	/// format, <c>StructuredText.Builder.Add(format, arguments)</c> fills <c>{a.0}</c>, <c>{a.1}</c> from the element's arguments and turns
	/// <c>{r.food}</c>, <c>{b.church}</c>, <c>{o.ship}</c> into icons, then <c>Render</c>. A replaced text that skipped this showed its placeholders raw.
	/// </summary>
	private static string RenderLikeGame(GameLocalized element, string text) {
		if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;   // nothing to fill in: as fast as before
		try {
			var hole = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(1);
			hole[0] = (Il2CppSystem.String)text;
			var builder = new DiceKingdoms.Interface.StructuredText.Builder();
			builder.Add(Il2CppSystem.Runtime.CompilerServices.FormattableStringFactory.Create("{0}", hole), element.arguments);
			return builder.Build().Render();
		}
		catch (Exception e) { CoreLog.Once("civ:text:render", "text render: " + e.Message); return text; }
	}

	public static bool RenderPrefix(GameLocalized __instance, ref string __result) {
		try {
			if (Names.Count == 0 && Texts.Count == 0 && GameTexts.Keys.Count == 0) return true;
			string key = __instance.key;
			if (key == null) return true;
			if (Texts.TryGetValue(key, out var byCiv) && byCiv.TryGetValue(ViewerCivilization(), out var text)) {
				__result = RenderLikeGame(__instance, text.For(TextLanguage.Current));
				return false;
			}
			if (GameTexts.Keys.TryGetValue(key, out var sets)) {   // a game key a mod translates: only in the languages it has, the game's text otherwise
				string language = TextLanguage.Current;
				foreach (var set in sets)
					if (set.TryGetOwn(TextSet.GameKeyPrefix + key, language, out string own, out _)) { __result = RenderLikeGame(__instance, own); return false; }
			}
			if (Names.Count == 0 || key.Length <= Prefix.Length + Suffix.Length || !key.StartsWith(Prefix, StringComparison.Ordinal) || !key.EndsWith(Suffix, StringComparison.Ordinal)) return true;
			if (!Names.TryGetValue(key.Substring(Prefix.Length, key.Length - Prefix.Length - Suffix.Length), out var name)) return true;
			__result = RenderLikeGame(__instance, name.For(TextLanguage.Current));
			return false;
		}
		catch (Exception e) { CoreLog.Once("civ:text", "civilization name hook: " + e.Message); return true; }
	}
}

/// <summary>A civilization's display name: one default and optional translations by language code ("de", "fr", "ru", "zh-CN", ...).</summary>
public sealed class CivilizationName : ILocalText {
	public CivilizationName(string name, IReadOnlyDictionary<string, string> byLanguage = null) {
		Default = name ?? throw new ArgumentNullException(nameof(name));
		if (byLanguage != null) foreach (var kv in byLanguage) if (!string.IsNullOrEmpty(kv.Value)) ByLanguage[kv.Key] = kv.Value;
	}
	public string Default { get; }
	public Dictionary<string, string> ByLanguage { get; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The exact language ("zh-CN"), then its base ("zh"), then English, then the default.</summary>
	public string For(string code) => TextLanguage.Pick(ByLanguage, code, true, out string text, out _) ? text : Default;
}
