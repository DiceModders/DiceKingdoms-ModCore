using System;
using UnityEngine.Localization.Settings;
using GameStaticReferences = DiceKingdoms.Interface.StructuredTextStaticReferences;

namespace DiceModders.ModCore;

/// <summary>
/// The game writes a civilization's name from the localization key <c>civilization.{value}.name</c> (the built-in ones are <c>civilization.EUROPE.name</c> and so
/// on, a registered one is <c>civilization.14.name</c>), looked up in the string table named by <c>StructuredTextStaticReferences</c>. A key the table lacks shows
/// up as a "missing translation" text, so the display name of every registered civilization is added to the table of the current language. The table of a
/// language is created when the language is loaded, so this repeats about once a second and only does anything when an entry is missing.
/// </summary>
internal static class CivilizationText {
	private static float _next;

	public static string KeyFor(int id) => "civilization." + id + ".name";

	public static void Tick() {
		if (CivilizationRegistry.All.Count == 0 || LobbyState.MatchRunning) return;   // not during a match; the game is usually in its (solo) lobby already, which needs the names
		if (UnityEngine.Time.unscaledTime < _next) return;
		_next = UnityEngine.Time.unscaledTime + 2f;
		try {
			var references = GameStaticReferences.instance;
			if (references == null) return;
			var database = LocalizationSettings.StringDatabase;
			if (database == null) return;
			var table = database.GetTable(references.localizationStringTable, null);
			if (table == null) return;
			foreach (var civ in CivilizationRegistry.All) {
				string key = KeyFor(civ.Id);
				if (table.GetEntry(key) == null) { table.AddEntry(key, civ.Name); CoreLog.Once("civ:text:" + civ.Id, "added the name of civilization " + civ.Id + " ('" + civ.Name + "') to the text table " + table.name); }
			}
		}
		catch (Exception e) { CoreLog.Once("civ:text", "adding civilization names to the game's text table: " + e.Message); }
	}
}
