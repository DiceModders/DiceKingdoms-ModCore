using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using GameTextBuilder = DiceKingdoms.Interface.StructuredText.Builder;

namespace DiceModders.ModCore;

public static partial class ModCore {
	/// <summary>
	/// Texts of mods in the game's languages: one system for mod screens, civilization texts and the game's own untranslated texts.
	/// A mod keeps its texts in one JSON file built into its DLL (<c>&lt;EmbeddedResource Include="texts.json" LogicalName="texts.json" /&gt;</c>);
	/// a file named <c>&lt;DllName&gt;.texts.json</c> next to the DLL, if there is one, wins line by line, so players can fix or add a language
	/// without rebuilding. Nothing is polled and nothing is added to the game's tables.
	/// </summary>
	public static class Texts {
		/// <summary>The game's current language code ("en", "de", "fr", "ru", "uk", "zh-CN", ...).</summary>
		public static string Language { get { Core.Ensure(); return TextLanguage.Current; } }

		/// <summary>Raised by the game's own language-change event: redraw texts your screen holds (texts asked with <see cref="TextSet.Get(string)"/> at draw time need nothing).</summary>
		public static event Action LanguageChanged;

		/// <summary>Every mod's texts by its assembly name (filled by <see cref="Load(Assembly, string, string)"/>): lobby rules look their titles up here.</summary>
		internal static readonly System.Collections.Generic.Dictionary<string, TextSet> ByAssembly = new();

		/// <summary>
		/// A mod's text for <paramref name="key"/> in the game's language, or <paramref name="fallback"/> when the mod has no texts or no such key.
		/// Used for texts given in code that a texts file may translate (lobby rules: <c>rule.&lt;key&gt;.title</c>, <c>.description</c>, <c>.choice.&lt;n&gt;</c>).
		/// </summary>
		internal static string Lookup(string assemblyName, string key, string fallback) {
			if (assemblyName == null || !ByAssembly.TryGetValue(assemblyName, out var set) || !set.Has(key)) return fallback;
			var text = set.Get(key);
			return string.IsNullOrEmpty(text) || text == key ? fallback : text;
		}

		/// <summary>
		/// One of the GAME's own texts by its key (e.g. <c>"unit.ARCHER.name"</c>, <c>"building.ARCHERY_RANGE.name"</c>,
		/// <c>"technology.TEMPLARS.name"</c>, <c>"civilization.EUROPE.name"</c>) in the current language, the way the game itself draws it
		/// (<c>ITextElement.L(key)</c>, then Render: icons, and any mod's <c>gamekey:</c> fix, included). <paramref name="fallback"/> (else the key)
		/// when the game has no such key.
		/// </summary>
		public static string Game(string key, string fallback = null) {
			try {
				var element = DiceKingdoms.Interface.ITextElement.L(key, new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DiceKingdoms.Interface.ITextElement>(0));
				string text = element.Render;
				if (string.IsNullOrEmpty(text) || text == key || text.StartsWith("No translation found", StringComparison.Ordinal)) return fallback ?? key;
				return text;
			}
			catch (Exception e) { CoreLog.Once("texts:game", "texts: game text '" + key + "' could not be read: " + e.Message); return fallback ?? key; }
		}

		/// <summary>The same as <see cref="Lookup"/> for a text whose owner is not known (a lobby heading): the first mod that has the key.</summary>
		internal static string LookupAny(string key, string fallback) {
			foreach (var set in ByAssembly.Values)
				if (set.Has(key)) { var text = set.Get(key); if (!string.IsNullOrEmpty(text) && text != key) return text; }
			return fallback;
		}

		// The language is noticed inside text lookups (while a screen is being built, or inside the game's own text drawing), so the handlers,
		// which rebuild screens, run after the next input update instead: never inside the lookup, and once for several changes in one frame.
		private static bool _languagePending;
		internal static void RaiseLanguageChanged() {
			if (_languagePending) return;
			_languagePending = true;
			HotkeyKeys.Later(RunLanguageChanged);
		}

		private static void RunLanguageChanged() {
			_languagePending = false;
			CoreLog.Info("texts: redrawing for the language '" + TextLanguage.Current + "'");
			var handler = LanguageChanged;
			if (handler == null) return;
			foreach (Action d in handler.GetInvocationList()) {
				try { d(); } catch (Exception e) { CoreLog.Error("Texts.LanguageChanged handler failed: " + e); }
			}
		}

		/// <summary>
		/// The calling mod's texts: the resource <paramref name="resource"/> built into its DLL, then <c>&lt;DllName&gt;.texts.json</c> next to the DLL
		/// laid over it. With <paramref name="section"/> the built-in texts are the object of that name inside a bigger file (a civilization's
		/// data file); the loose file is always plain texts. Keys starting with <c>game:</c> / <c>gamekey:</c> start translating the game's own texts.
		/// </summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static TextSet Load(string resource = "texts.json", string section = null) => Load(Assembly.GetCallingAssembly(), resource, section);

		public static TextSet Load(Assembly assembly, string resource = "texts.json", string section = null) {
			Core.Ensure();
			if (assembly == null) throw new ArgumentNullException(nameof(assembly));
			string mod = assembly.GetName().Name;
			var set = new TextSet { Source = mod };
			try {
				using var stream = FindResource(assembly, resource);
				if (stream != null) { using var reader = new StreamReader(stream); set = TextSet.Parse(reader.ReadToEnd(), section, mod); }
				else CoreLog.Warn("texts: '" + resource + "' is not built into " + mod + " (EmbeddedResource with LogicalName=\"" + resource + "\")");
			}
			catch (Exception e) { CoreLog.Error("texts: " + mod + " '" + resource + "' could not be read: " + e.Message); }

			string loose = LooseFile(assembly);
			if (loose != null && File.Exists(loose)) {
				try {
					var over = TextSet.Parse(File.ReadAllText(loose), null, loose);
					set.Merge(over);
					CoreLog.Info("texts: " + mod + " uses " + over.Count + " key(s) from " + Path.GetFileName(loose));
				}
				catch (Exception e) { CoreLog.Warn("texts: " + Path.GetFileName(loose) + " has a mistake and was left out (" + e.Message + ")"); }
			}
			var missing = set.MissingEnglish();
			if (missing.Count > 0) CoreLog.Warn("texts: " + mod + " has " + missing.Count + " key(s) without English (the fallback language), e.g. " + missing[0]);
			GameTexts.Add(set);
			ByAssembly[mod] = set;
			return set;
		}

		/// <summary>The loose file of a mod: <c>&lt;DllName&gt;.texts.json</c> in the DLL's folder.</summary>
		public static string LooseFile(Assembly assembly) {
			string dll = assembly?.Location;
			return string.IsNullOrEmpty(dll) ? null : Path.Combine(Path.GetDirectoryName(dll) ?? "", Path.GetFileNameWithoutExtension(dll) + ".texts.json");
		}

		private static Stream FindResource(Assembly assembly, string resource) {
			var stream = assembly.GetManifestResourceStream(resource);
			if (stream != null) return stream;
			foreach (var name in assembly.GetManifestResourceNames())
				if (name.EndsWith("." + resource, StringComparison.OrdinalIgnoreCase)) return assembly.GetManifestResourceStream(name);
			return null;
		}
	}
}

/// <summary>
/// The game's own texts that have no translation.
/// <para><c>game:&lt;text&gt;</c>: a text written in the game's code. Every such text is built by <c>StructuredText.Builder.Add(FormattableString, ...)</c>
/// (for example the unit card's range, <c>"{0} tiles"</c>); the prefix here swaps the format for the current language's one before the game cuts it into
/// parts. A text made before a language change keeps its language until the game makes it again (its screen is opened again).</para>
/// <para><c>gamekey:&lt;key&gt;</c>: one of the game's localization keys, answered by <see cref="CivilizationText"/> in the languages the file has.</para>
/// </summary>
internal static class GameTexts {
	private static readonly Dictionary<string, List<(TextSet set, string key)>> Literals = new(StringComparer.Ordinal);
	internal static readonly Dictionary<string, List<TextSet>> Keys = new(StringComparer.Ordinal);
	private static bool _patched;

	public static void Add(TextSet set) {
		foreach (var key in set.Keys) {
			if (key.StartsWith(TextSet.GameLiteralPrefix, StringComparison.Ordinal)) {
				string literal = key.Substring(TextSet.GameLiteralPrefix.Length).Trim();
				if (literal.Length == 0) continue;
				if (!Literals.TryGetValue(literal, out var list)) Literals[literal] = list = new List<(TextSet, string)>();
				list.Insert(0, (set, key));   // the mod loaded last answers first
			}
			else if (key.StartsWith(TextSet.GameKeyPrefix, StringComparison.Ordinal)) {
				string gameKey = key.Substring(TextSet.GameKeyPrefix.Length).Trim();
				if (!Keys.TryGetValue(gameKey, out var list)) Keys[gameKey] = list = new List<TextSet>();
				list.Insert(0, set);
			}
		}
		if (Literals.Count > 0 && !_patched) {
			_patched = true;   // only in the game once a mod has such a text
			try {
				MethodInfo target = null;   // Add(FormattableString formattable, ITextElement[] arguments)
				foreach (var m in AccessTools.GetDeclaredMethods(typeof(GameTextBuilder))) {
					var p = m.GetParameters();
					if (m.Name == "Add" && p.Length > 0 && p[0].ParameterType == typeof(Il2CppSystem.FormattableString)) { target = m; break; }
				}
				if (target == null) { CoreLog.Warn("texts: StructuredText.Builder.Add not found; texts written in the game's code stay untranslated"); return; }
				new Harmony("DiceModders.ModCore.texts").Patch(target, prefix: new HarmonyMethod(typeof(GameTexts), nameof(AddPrefix)));
			}
			catch (Exception e) { CoreLog.Error("texts: hook for texts written in the game's code failed: " + e); }
		}
	}

	public static void AddPrefix(ref Il2CppSystem.FormattableString formattable) {
		try {
			if (formattable == null) return;
			string format = formattable.Format;
			if (string.IsNullOrEmpty(format)) return;
			string core = format.Trim();   // "\n{0} tiles" is the same text as "{0} tiles"
			if (!Literals.TryGetValue(core, out var list)) return;
			string language = TextLanguage.Current;
			foreach (var (set, key) in list) {
				if (!set.TryGetOwn(key, language, out string text, out string used)) continue;
				var values = formattable.GetArguments();
				text = TextPlural.Resolve(text, used, i => WholeNumber(values, i));
				int at = format.IndexOf(core, StringComparison.Ordinal);
				formattable = Il2CppSystem.Runtime.CompilerServices.FormattableStringFactory.Create(format.Substring(0, at) + text + format.Substring(at + core.Length), values);
				return;
			}
		}
		catch (Exception e) { CoreLog.Once("texts:literal", "texts: translating a text written in the game's code failed: " + e.Message); }
	}

	private static long? WholeNumber(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object> values, int index) {
		if (values == null || index < 0 || index >= values.Length) return null;
		var value = values[index];
		if (value == null) return null;
		IntPtr klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(value.Pointer);
		if (klass == Il2CppInterop.Runtime.Il2CppClassPointerStore<int>.NativeClassPtr) return value.Unbox<int>();
		if (klass == Il2CppInterop.Runtime.Il2CppClassPointerStore<long>.NativeClassPtr) return value.Unbox<long>();
		return null;
	}
}
