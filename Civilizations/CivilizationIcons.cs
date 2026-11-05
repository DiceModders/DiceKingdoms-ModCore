using System;
using System.Collections.Generic;
using UnityEngine;
using GameBuildingTypeName = DiceKingdoms.Game.BuildingTypeName;
using GameBuildingTypes = DiceKingdoms.Game.BuildingTypes;
using GameCivilization = DiceKingdoms.Interface.Civilization;
using GamePlayerSkins = DiceKingdoms.Game.PlayerSkins;
using GameSkinRegistry = DiceKingdoms.Interface.SkinRegistry;

namespace DiceModders.ModCore;

/// <summary>Pictures for a civilization's own buildings, taken in the game by <see cref="Tools.IconRenderer"/>.</summary>
internal static class CivilizationIcons {
	public const int Width = 237, Height = 235;   // the game's building pictures

	public static void Render(int civilization, Tools.IconView view, GameBuildingTypeName[] only, Action<GameBuildingTypeName, Sprite[]> rendered) {
		var skins = CivilizationRegistry.SkinsOf(civilization);
		if (skins == null) { CoreLog.Warn("RenderIcons: civilization " + civilization + " has no own models"); return; }
		var wanted = new HashSet<int>();
		if (only != null && only.Length > 0) foreach (var t in only) wanted.Add((int)t);
		else {
			foreach (var k in skins.Buildings.Keys) wanted.Add(k);
			foreach (var kv in skins.BuildingModels) if (kv.Value.Icons == null) wanted.Add(kv.Key);   // a model with its own pictures keeps them
		}
		if (wanted.Count == 0) return;

		var registry = Resources.FindObjectsOfTypeAll<GameSkinRegistry>();
		var types = Resources.FindObjectsOfTypeAll<GameBuildingTypes>();
		if (registry.Length == 0 || types.Length == 0) { CoreLog.Warn("RenderIcons: the game's skins are not loaded"); return; }
		// the constructor only knows 0..4: build the look-alike's and put the real number in, as ModCore's skin hooks do; the lookups below then answer for it
		int look = civilization >= 0 && civilization <= 4 ? civilization : CivilizationRegistry.TryGetSkinsLike(civilization, out int l) ? l : 0;
		var playerSkins = new GamePlayerSkins((GameCivilization)look, registry[0]);
		playerSkins.civilization = (GameCivilization)civilization;
		string civName = CivilizationRegistry.Find(civilization)?.Name ?? ((GameCivilization)civilization).ToString();
		string owner = CivilizationRegistry.Find(civilization)?.Key ?? "builtin-" + civilization;

		foreach (var type in types[0].Types) {
			if (type == null || !wanted.Contains((int)type.name)) continue;
			var visual = playerSkins.GetBuildingVisualPrefab(type);   // the civilization's own prefab or model copy (ModCore's lookup hook)
			if (visual == null) continue;
			var name = type.name;
			Tools.IconRenderer.PlayerColours(owner + " " + name, visual.gameObject, view ?? Tools.IconView.Building, Width, Height, sprites => {
				if (visual == null) return;
				int needed = visual.icons != null ? visual.icons.Length : 0;
				if (needed == sprites.Length) visual.icons = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Sprite>(sprites);
				else CoreLog.Warn("RenderIcons: " + civName + " " + name + " expects " + needed + " pictures, rendered " + sprites.Length);
				rendered?.Invoke(name, sprites);
			});
		}
	}
}

public static partial class ModCore {
	public static partial class Civilizations {
		/// <summary>
		/// Takes the pictures of a civilization's own buildings in the game (one per player colour, the size of the game's pictures) and gives them to the
		/// buildings, so the lobby, build menu, scoreboard and Rules screen show your models. Without <paramref name="buildings"/>: every building given with
		/// <see cref="CivilizationSkins.Building"/>, and every <see cref="CivilizationSkins.BuildingModel"/> without pictures of its own. Works for a registered
		/// civilization and a reskinned built-in one. Rendered once (spread over frames after the game data has loaded) and cached; later launches read one
		/// file. <paramref name="rendered"/> receives the pictures too, e.g. for <see cref="Tools.TextSprites"/>.
		/// </summary>
		public static void RenderIcons(int civilization, Tools.IconView view = null, Action<GameBuildingTypeName, Sprite[]> rendered = null, params GameBuildingTypeName[] buildings) {
			Core.Ensure();
			WhenGameDataLoaded(() => CivilizationIcons.Render(civilization, view, buildings, rendered));
		}
	}
}
