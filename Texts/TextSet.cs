using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiceModders.ModCore;

/// <summary>
/// The texts of one mod in every language it has: <c>{ "key": { "en": "...", "ru": "..." } }</c>. Made by <see cref="ModCore.Texts.Load"/> from the file
/// built into the mod's DLL, with the loose file next to the DLL laid over it line by line. <see cref="Get(string)"/> answers in the game's current
/// language: the exact language ("zh-CN"), then its base ("zh"), then English, then the key itself, so a missing text is visible instead of blank.
/// </summary>
public sealed class TextSet {
	/// <summary>Keys with this prefix translate a text that is hard-coded in the game, e.g. <c>"game:{0} tiles": { "ru": "{0} клетки" }</c>.</summary>
	public const string GameLiteralPrefix = "game:";
	/// <summary>Keys with this prefix replace one of the game's own localization keys in the languages given, e.g. <c>"gamekey:unit.rules.range"</c>.</summary>
	public const string GameKeyPrefix = "gamekey:";

	private readonly Dictionary<string, Dictionary<string, string>> _texts = new(StringComparer.Ordinal);

	/// <summary>Where the set came from, for log lines.</summary>
	public string Source { get; internal set; } = "";
	public IEnumerable<string> Keys => _texts.Keys;
	public int Count => _texts.Count;
	public bool Has(string key) => key != null && _texts.ContainsKey(key);

	/// <summary>Every language code that at least one key has.</summary>
	public IReadOnlyCollection<string> Languages {
		get {
			var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var byLanguage in _texts.Values) foreach (var code in byLanguage.Keys) all.Add(code);
			return all;
		}
	}

	/// <summary>The text in the game's current language (exact language, base language, English, then the key).</summary>
	public string Get(string key) => Get(key, TextLanguage.Current);

	/// <summary>
	/// The text with values put in: <c>{0}</c>, <c>{1}</c> as in <c>string.Format</c>. A word that depends on a number is written
	/// <c>{0:tile|tiles}</c> (English, German, French: one | many) or <c>{0:клетка|клетки|клеток}</c> (Russian, Ukrainian: 1 | 2-4 | 5+).
	/// </summary>
	public string Get(string key, params object[] values) {
		string code = TextLanguage.Current;
		string text = Get(key, code);
		if (values == null || values.Length == 0) return text;
		try {
			text = TextPlural.Resolve(text, UsedLanguage(key, code), i => i < values.Length && IsWhole(values[i]) ? Convert.ToInt64(values[i], CultureInfo.InvariantCulture) : (long?)null);
			return string.Format(CultureInfo.InvariantCulture, text, values);
		}
		catch (FormatException) { return text; }
	}

	/// <summary>The text for a language code.</summary>
	public string Get(string key, string language) {
		if (key == null) return "";
		if (_texts.TryGetValue(key, out var byLanguage) && TextLanguage.Pick(byLanguage, language, true, out string text, out _)) return text;
		return key;
	}

	/// <summary>Only a text written for this language or its base language: no English, no key. For texts that have their own fallback (the game's).</summary>
	public bool TryGetOwn(string key, string language, out string text, out string usedLanguage) {
		text = null; usedLanguage = null;
		return key != null && _texts.TryGetValue(key, out var byLanguage) && TextLanguage.Pick(byLanguage, language, false, out text, out usedLanguage);
	}

	private string UsedLanguage(string key, string language) =>
		_texts.TryGetValue(key, out var byLanguage) && TextLanguage.Pick(byLanguage, language, true, out _, out string used) ? used : "en";

	private static bool IsWhole(object value) => value is int or long or short or byte or uint or ushort or sbyte;

	/// <summary>Lays another set over this one: every text it has replaces this set's text for that key and language; everything else stays.</summary>
	public void Merge(TextSet over) {
		if (over == null) return;
		foreach (var kv in over._texts) {
			if (!_texts.TryGetValue(kv.Key, out var mine)) _texts[kv.Key] = mine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var text in kv.Value) mine[text.Key] = text.Value;
		}
	}

	/// <summary>Keys without an English text (the fallback of every other language). The game's own texts (<c>game:</c>, <c>gamekey:</c>) are English already.</summary>
	public List<string> MissingEnglish() {
		var missing = new List<string>();
		foreach (var kv in _texts)
			if (!kv.Key.StartsWith(GameLiteralPrefix, StringComparison.Ordinal) && !kv.Key.StartsWith(GameKeyPrefix, StringComparison.Ordinal) && !kv.Value.ContainsKey("en")) missing.Add(kv.Key);
		return missing;
	}

	/// <summary>
	/// Reads the texts from JSON: <c>{ "key": { "en": "...", "ru": "..." } }</c>. A key may also have one plain string (English). With
	/// <paramref name="section"/> the texts are the object of that name inside a bigger file. Comments and trailing commas are allowed.
	/// </summary>
	public static TextSet Parse(string json, string section = null, string source = "") {
		var set = new TextSet { Source = source };
		using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
		var root = doc.RootElement;
		if (section != null && (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(section, out root))) return set;
		if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("the texts must be an object: { \"key\": { \"en\": \"...\" } }");
		foreach (var entry in root.EnumerateObject()) {
			var byLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (entry.Value.ValueKind == JsonValueKind.String) byLanguage["en"] = entry.Value.GetString();
			else if (entry.Value.ValueKind == JsonValueKind.Object)
				foreach (var language in entry.Value.EnumerateObject())
					if (language.Value.ValueKind == JsonValueKind.String && language.Value.GetString().Length > 0) byLanguage[language.Name] = language.Value.GetString();
			if (byLanguage.Count > 0) set._texts[entry.Name] = byLanguage;
		}
		return set;
	}
}

/// <summary>The game's current language and the fallback rule shared by every text of ModCore.</summary>
internal static class TextLanguage {
	private static string _current;
	private static bool _listening;

	/// <summary>
	/// The selected language code ("en", "ru", "zh-CN", ...), or "" while the game's localization is still starting (texts then use English).
	/// Kept up to date by Unity's own language-change event once the game data has loaded.
	/// </summary>
	public static string Current {
		get {
			// Read again once per frame: Unity's SelectedLocaleChanged event did not reach us reliably (2026-11-18: the game was Ukrainian, the
			// mod texts stayed in the language read first). Reading the selected locale is cheap; once per frame keeps per-text costs at zero.
			int frame = UnityEngine.Time.frameCount;
			if (_current != null && frame == _frame) return _current;
			string code = Read();
			if (code == null) { _early = true; return _current ?? ""; }
			_frame = frame;
			string before = _current;
			_current = code;
			if (before != code) CoreLog.Info("texts: the game's language is '" + code + "'");
			if (before != null && before != code) ModCore.Texts.RaiseLanguageChanged();
			// texts asked for before the localization was ready got English (a name put into a drop-down at start-up): tell them once
			else if (before == null && _early && code != "") { _early = false; ModCore.Texts.RaiseLanguageChanged(); }   // English too: names drawn early wait for it
			return code;
		}
	}
	private static bool _early, _ready;
	/// <summary>True once the game's texts and language can be read without making the game wait (from the frame after the game data loaded).</summary>
	internal static bool Ready => _ready;
	private static int _frame = -1;

	/// <summary>
	/// The selected locale, or null while Unity's localization has not finished starting. Reading <c>SelectedLocale</c> before that makes Unity wait
	/// for its own loading on the main thread (WaitForCompletion); done from the game-data-loaded callback that froze the game on the loading screen.
	/// </summary>
	private static string Read() {
		// Only after the game data has loaded (Listen): Unity's localization is ready by then. The old readiness test,
		// InitializationOperation.IsDone, goes through a generic struct (AsyncOperationHandle<T>) the interop does not read reliably;
		// when it failed every text fell back to English in every language (2026-11-18).
		if (!_ready) return null;
		try { return UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale?.Identifier.Code ?? ""; }
		catch (Exception e) { CoreLog.Once("texts:locale", "texts: the game's language could not be read: " + e.Message); return null; }
	}

	/// <summary>
	/// Once, when the game data has loaded: subscribes to Unity's language-change event (no read here, see <see cref="Read"/>); the language is read
	/// on first use after the localization is ready and then remembered, and <see cref="ModCore.Texts.LanguageChanged"/> follows Unity's event.
	/// </summary>
	public static void Listen() {
		// Not inside this callback: reading SelectedLocale while the game is still loading makes Unity wait for its own loading on the main thread
		// and the game hangs on a black screen (2026-11-18, twice). The read is allowed from the next frame on, after the loading step has returned.
		HotkeyKeys.Later(() => { _ready = true; CoreLog.Info("texts: the game's language can be read now"); });
		if (_listening) return;
		try {
			var handler = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UnityEngine.Localization.Locale>>(new Action<UnityEngine.Localization.Locale>(OnLocale));
			UnityEngine.Localization.Settings.LocalizationSettings.add_SelectedLocaleChanged(handler);
			_listening = true;
		}
		catch (Exception e) { CoreLog.Warn("texts: the game's language-change event is not available, the language is read on every use: " + e.Message); }
	}

	private static void OnLocale(UnityEngine.Localization.Locale locale) {
		string before = _current;
		try { _current = locale != null ? locale.Identifier.Code ?? "" : ""; } catch { _current = null; }
		if (_current != before) { CoreLog.Info("texts: the game switched its language to '" + _current + "'"); ModCore.Texts.RaiseLanguageChanged(); }
	}

	private static string BaseOf(string code) { int dash = code.IndexOf('-'); return dash > 0 ? code.Substring(0, dash) : code; }

	/// <summary>
	/// The exact language ("zh-CN"), its base ("zh"), another variety of the same base (the game's Chinese locale may be "zh", files say "zh-CN"), then,
	/// with <paramref name="english"/>, English.
	/// </summary>
	public static bool Pick(Dictionary<string, string> byLanguage, string code, bool english, out string text, out string used) {
		text = null; used = null;
		if (byLanguage == null) return false;
		if (!string.IsNullOrEmpty(code)) {
			if (byLanguage.TryGetValue(code, out text)) { used = code; return true; }
			string baseCode = BaseOf(code);
			if (baseCode.Length != code.Length && byLanguage.TryGetValue(baseCode, out text)) { used = baseCode; return true; }
			foreach (var kv in byLanguage)
				if (kv.Key.IndexOf('-') > 0 && BaseOf(kv.Key).Equals(baseCode, StringComparison.OrdinalIgnoreCase)) { text = kv.Value; used = kv.Key; return true; }
		}
		if (english && byLanguage.TryGetValue("en", out text)) { used = "en"; return true; }
		return false;
	}
}

/// <summary>Words that depend on a number: <c>{0:one|many}</c> / <c>{0:1|2-4|5+}</c>, chosen by the numbered value and the language's rule.</summary>
internal static class TextPlural {
	private static readonly Regex Choice = new(@"\{(\d+):([^{}|]*(?:\|[^{}|]*)+)\}", RegexOptions.Compiled);

	public static string Resolve(string text, string language, Func<int, long?> number) {
		if (text == null || text.IndexOf('|') < 0) return text;
		return Choice.Replace(text, m => {
			long? n = number(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
			var forms = m.Groups[2].Value.Split('|');
			return forms[Math.Min(n.HasValue ? Form(language, Math.Abs(n.Value)) : forms.Length - 1, forms.Length - 1)];
		});
	}

	private static int Form(string language, long n) {
		string b = language ?? "";
		int dash = b.IndexOf('-');
		if (dash > 0) b = b.Substring(0, dash);
		switch (b.ToLowerInvariant()) {
			case "ru": case "uk": case "be":
				if (n % 10 == 1 && n % 100 != 11) return 0;
				return n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? 1 : 2;
			case "zh": case "ja": case "ko": case "tr": case "vi": case "th": return 0;
			case "fr": case "pt": return n <= 1 ? 0 : 1;
			default: return n == 1 ? 0 : 1;
		}
	}
}
