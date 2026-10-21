using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;
using GameBuildingTypes = DiceKingdoms.Game.BuildingTypes;
using GameBuildingVisual = DiceKingdoms.Game.BuildingVisual;
using GameInnerCliffVisualHub = DiceKingdoms.Game.InnerCliffVisualHub;
using GameNatureVisualHub = DiceKingdoms.Game.NatureVisualHub;
using GameShipVisual = DiceKingdoms.Interface.ShipVisual;
using GameSkinRegistry = DiceKingdoms.Interface.SkinRegistry;
using GameSkinVariant = DiceKingdoms.Interface.SkinVariant;
using GameStaticReferences = DiceKingdoms.Interface.StructuredTextStaticReferences;
using GameTechnologies = DiceKingdoms.Game.Technologies;
using GameTechTree = DiceKingdoms.Game.TechTree;
using GameUnitVisual = DiceKingdoms.Interface.UnitVisual;

namespace DiceModders.ModCore;

/// <summary>
/// Developer tool, off by default (<c>[Debug] DumpGameData</c> in the ModCore config). Once the game's data is loaded it writes two text files next to
/// <c>LogOutput.log</c>: <c>ModCore-game-data.txt</c> (technologies, tech trees, building types, the skin registry with every model the built-in civilizations
/// use, the prefabs the game has, the text table) and <c>ModCore-sprites.txt</c> (every sprite with the texture it lives on). It is what the wiki page
/// "Making a civilization" is checked against, and what a modder needs to find the model or icon to copy for a new civilization.
/// </summary>
internal static class GameDataDump {
	private static bool _done;

	public static void Tick() {
		if (_done || Plugin.DumpGameData == null || !Plugin.DumpGameData.Value) return;
		var registries = Resources.FindObjectsOfTypeAll<GameSkinRegistry>();
		var technologies = Resources.FindObjectsOfTypeAll<GameTechnologies>();
		if (registries.Length == 0 || technologies.Length == 0) return;
		_done = true;
		try {
			var sb = new StringBuilder();
			Section(sb, "Civilization (the game's enum)", () => { for (int i = 0; i <= 4; i++) sb.AppendLine($"{i} = {(DiceKingdoms.Interface.Civilization)i}"); });
			Section(sb, "Technologies (Technologies.Values)", () => DumpTechnologies(sb, technologies[0]));
			Section(sb, "Tech trees", () => DumpTechTrees(sb));
			Section(sb, "Building types (BuildingTypes.Types)", () => DumpBuildingTypes(sb));
			Section(sb, "Skin registry entries: default prefab -> replacement (biome / civilization / variant)", () => DumpSkinRegistry(sb, registries[0]));
			Section(sb, "Default prefabs (the Europe look of units, nature, cliffs, ship)", () => DumpDefaultPrefabs(sb, registries[0]));
			Section(sb, "Prefabs found in memory", () => DumpPrefabs(sb));
			Section(sb, "Text table", () => DumpText(sb));
			string path = Path.Combine(Paths.BepInExRootPath, "ModCore-game-data.txt");
			File.WriteAllText(path, sb.ToString());
			CoreLog.Info("game data written to " + path);

			var sprites = new StringBuilder();
			Section(sprites, "Sprites: name | texture | size", () => DumpSprites(sprites));
			string spritePath = Path.Combine(Paths.BepInExRootPath, "ModCore-sprites.txt");
			File.WriteAllText(spritePath, sprites.ToString());
			CoreLog.Info("sprite list written to " + spritePath);
		}
		catch (Exception e) { CoreLog.Error("game data dump failed: " + e); }
	}

	private static void Section(StringBuilder sb, string title, Action body) {
		sb.AppendLine().AppendLine("=== " + title + " ===");
		try { body(); }
		catch (Exception e) { sb.AppendLine("(failed: " + e.Message + ")"); }
	}

	private static object Get(object o, string name) {
		if (o == null) return null;
		const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		var t = o.GetType();
		var p = t.GetProperty(name, f);
		if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
		var fi = t.GetField(name, f);
		return fi?.GetValue(o);
	}

	/// <summary>name=value for every simple (number, text, enum, bool) property of an interop object.</summary>
	private static string Describe(object o) {
		if (o == null) return "null";
		var parts = new StringBuilder();
		foreach (var p in o.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
			var pt = p.PropertyType;
			if (!(pt.IsPrimitive || pt.IsEnum || pt == typeof(string)) || p.GetIndexParameters().Length != 0) continue;
			if (p.Name == "Pointer" || p.Name == "ObjectClass" || p.Name == "WasCollected") continue;
			try { parts.Append(p.Name).Append('=').Append(p.GetValue(o)).Append(' '); } catch { }
		}
		return parts.ToString().TrimEnd();
	}

	/// <summary>Opt&lt;T&gt; (the game's optional value): "-" when empty, otherwise the value.</summary>
	private static string Opt(object opt) {
		if (opt == null) return "-";
		var t = opt.GetType();
		if (!t.Name.StartsWith("Nullable")) return Convert.ToString(opt);   // a plain value
		var has = Get(opt, "HasValue") ?? Get(opt, "hasValue");
		if (!(has is bool b) || !b) return "-";
		return Convert.ToString(Get(opt, "Value") ?? Get(opt, "value"));
	}

	private static string NameOf(object o) {
		if (o == null) return "null";
		if (o is UnityEngine.Object u) return u ? u.name : "destroyed";
		return Convert.ToString(Get(o, "name") ?? o);
	}

	private static void DumpTechnologies(StringBuilder sb, GameTechnologies technologies) {
		foreach (var t in technologies.Values)
			sb.AppendLine($"{(int)t.name,2} {t.name,-24} {t.GetType().Name,-30} cost: {Describe(t.cost)}");
	}

	private static void DumpTechTrees(StringBuilder sb) {
		foreach (var tree in Resources.FindObjectsOfTypeAll<GameTechTree>()) {
			sb.AppendLine($"tree '{tree.name}' civilization={tree.Civilization} ({(int)tree.Civilization})");
			int index = 0;
			foreach (var slot in tree.Slots) {
				var links = new StringBuilder();
				if (slot.links != null)
					foreach (var link in slot.links) links.Append($" -> {link.toTechnology} (slot {link.toSlot}, port {link.fromPort}->{link.toPort})");
				sb.AppendLine($"  slot {index++,2}: {slot.technology,-24} at ({slot.position.x},{slot.position.y}){links}");
			}
		}
	}

	private static void DumpBuildingTypes(StringBuilder sb) {
		foreach (var set in Resources.FindObjectsOfTypeAll<GameBuildingTypes>())
			foreach (var b in set.Types)
				sb.AppendLine($"{(int)b.name,2} {b.name,-16} tier={b.tier} size={b.bulkSize} health={b.health} cells={b.localPositions?.Length} prefab='{NameOf(b.visualPrefab)}' cost: {Describe(b.cost)}");
	}

	private static void DumpSkinRegistry(StringBuilder sb, GameSkinRegistry registry) {
		object entries = null;
		foreach (var name in new[] { "entries", "Entries", "_entries" }) { entries = Get(registry, name); if (entries != null) break; }
		if (entries == null) {
			// not where we expected: list what the registry has, so the next dump says where to look
			const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			foreach (var p in registry.GetType().GetProperties(f)) sb.AppendLine($"  property {p.PropertyType.Name} {p.Name}");
			return;
		}
		int n = 0;
		foreach (var entry in (IEnumerable)entries) {
			if (entry == null) continue;
			var key = Get(entry, "key") ?? Get(entry, "Key");
			var value = Get(entry, "value") ?? Get(entry, "Value");
			sb.AppendLine($"'{NameOf(Get(key, "original") ?? Get(key, "defaultPrefab"))}' -> '{NameOf(value)}'  biome={Opt(Get(key, "biome"))} civilization={Opt(Get(key, "civilization"))} variant='{Get(key, "variant")}'");
			n++;
		}
		sb.AppendLine($"({n} entries)");
	}

	private static void DumpDefaultPrefabs(StringBuilder sb, GameSkinRegistry registry) {
		var defaults = Get(registry, "defaultPrefabs");
		foreach (var field in new[] { "unitVisualPrefabs", "natureVisualPrefabs", "innerCliffVisualPrefabs" }) {
			sb.AppendLine(field + ":");
			if (Get(defaults, field) is IEnumerable list) foreach (var p in list) sb.AppendLine("  " + NameOf(p));
		}
		sb.AppendLine("shipVisualPrefab: " + NameOf(Get(defaults, "shipVisualPrefab")));
	}

	private static void DumpPrefabs(StringBuilder sb) {
		Prefabs<GameBuildingVisual>(sb, "BuildingVisual");
		Prefabs<GameUnitVisual>(sb, "UnitVisual");
		Prefabs<GameShipVisual>(sb, "ShipVisual");
		Prefabs<GameNatureVisualHub>(sb, "NatureVisualHub");
		Prefabs<GameInnerCliffVisualHub>(sb, "InnerCliffVisualHub");
		sb.AppendLine("SkinVariant:");
		foreach (var v in Resources.FindObjectsOfTypeAll<GameSkinVariant>())
			sb.AppendLine($"  '{v.gameObject.name}' biome={Opt(v.biome)} civilization={Opt(v.civilization)} variant='{v.variant}'");
	}

	private static void Prefabs<T>(StringBuilder sb, string label) where T : UnityEngine.Object {
		sb.AppendLine(label + ":");
		foreach (var o in Resources.FindObjectsOfTypeAll<T>()) {
			var c = o as Component;
			sb.AppendLine($"  '{(c != null ? c.gameObject.name : o.name)}'{(c != null && c.gameObject.scene.IsValid() ? " (in scene)" : " (prefab/asset)")}");
		}
	}

	private static void DumpText(StringBuilder sb) {
		var references = GameStaticReferences.instance;
		if (references == null) { sb.AppendLine("StructuredTextStaticReferences.instance is null"); return; }
		sb.AppendLine("table reference: " + Describe(references.localizationStringTable));
		var table = UnityEngine.Localization.Settings.LocalizationSettings.StringDatabase.GetTable(references.localizationStringTable, null);
		sb.AppendLine("table: " + (table == null ? "null" : table.name));
		if (table == null) return;
		for (int i = 0; i <= 4; i++) {
			string key = "civilization." + (DiceKingdoms.Interface.Civilization)i + ".name";
			var entry = table.GetEntry(key);
			sb.AppendLine($"{key} = {(entry == null ? "(missing)" : entry.Value)}");
		}
	}

	private static void DumpSprites(StringBuilder sb) {
		var all = Resources.FindObjectsOfTypeAll<Sprite>();
		var names = new System.Collections.Generic.List<string>();
		foreach (var s in all) names.Add($"{s.name} | {(s.texture ? s.texture.name : "-")} | {(int)s.rect.width}x{(int)s.rect.height}");
		names.Sort(StringComparer.Ordinal);
		foreach (var n in names) sb.AppendLine(n);
		sb.AppendLine($"({names.Count} sprites)");
	}
}
