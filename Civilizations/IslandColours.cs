using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using GameIslandGrid = DiceKingdoms.Game.IslandGrid;

namespace DiceModders.ModCore;

/// <summary>
/// Island colours of a civilization. The island ground is one mesh coloured from palette cells that the game picks per biome (a Burst job with
/// hard-coded cells), and mountains (inner cliffs), trees and flowers are pooled models on the same 8x8 palette. A registered civilization borrows the
/// biome of its <c>skinsLike</c> civilization; these colours change chosen cells for that civilization's islands only, through a recoloured copy of the
/// renderer's palette set with a <c>MaterialPropertyBlock</c> (the shared materials stay untouched).
/// Event driven: when the islands spawn or a player rejoins (ground and the nature already there) and when the game adds a mountain or a nature visual
/// (<c>IslandGrid.AddInnerCliff</c> / <c>UpdateNatureVisual</c> postfixes). Visual and local.
/// </summary>
internal static class IslandColours {
	private static readonly Dictionary<string, Texture2D> Copies = new();   // "<civ>:<set>:<source palette id>" -> recoloured copy
	private static int _ground = Shader.PropertyToID("_ColorPalette");

	public static void Install(Harmony harmony) {
		try {
			var cliff = AccessTools.Method(typeof(GameIslandGrid), "AddInnerCliff");
			if (cliff != null) harmony.Patch(cliff, postfix: new HarmonyMethod(typeof(IslandColours), nameof(InnerCliffPostfix)));
			var nature = AccessTools.Method(typeof(GameIslandGrid), "UpdateNatureVisual");
			if (nature != null) harmony.Patch(nature, postfix: new HarmonyMethod(typeof(IslandColours), nameof(NaturePostfix)));
		}
		catch (Exception e) { CoreLog.Error("island colour hooks failed: " + e); }
		ModCore.Match.IslandsSpawned += AllIslands;
		ModCore.Match.Rejoined += AllIslands;
	}

	/// <summary>The island owner's civilization and its colours (a registered civilization, or a built-in one a mod reskinned); null when none.</summary>
	private static (int Id, CivilizationSkins Skins)? CivOf(GameIslandGrid grid) {
		try {
			var skins = grid?.island?.Skins;
			if (skins == null) return null;
			int id = (int)skins.civilization;
			var s = CivilizationRegistry.SkinsOf(id);
			return s != null && s.HasIslandColours ? (id, s) : null;
		}
		catch { return null; }
	}

	private static void AllIslands() {
		using var slow = CoreLog.Slow("island colours (all islands)");
		try {
			if (!AnyColours()) return;
			foreach (var grid in Resources.FindObjectsOfTypeAll<GameIslandGrid>()) {
				if (!grid.gameObject.scene.IsValid()) continue;
				if (CivOf(grid) is not { } civ) continue;
				var gf = grid.groundMeshFilter;
				if (gf != null) Recolour(civ, gf.GetComponent<MeshRenderer>(), civ.Skins.GroundCells, "g");
				var parent = grid.natureParent;
				if (parent != null) foreach (var r in parent.GetComponentsInChildren<MeshRenderer>(true)) Recolour(civ, r, civ.Skins.NatureCells, "n");
			}
		}
		catch (Exception e) { CoreLog.Once("island:all", "island colours: " + e.Message); }
	}

	public static void InnerCliffPostfix(GameIslandGrid __instance) {
		try {
			if (CivOf(__instance) is not { } civ) return;
			var list = __instance.innerCliffVisuals;
			if (list == null || list.Count == 0) return;
			var t = list[list.Count - 1];   // AddInnerCliff appends the new visual
			if (t != null) foreach (var r in t.GetComponentsInChildren<MeshRenderer>(true)) Recolour(civ, r, civ.Skins.NatureCells, "n");
		}
		catch (Exception e) { CoreLog.Once("island:cliff", "island colours (mountain): " + e.Message); }
	}

	public static void NaturePostfix(GameIslandGrid __instance, Unity.Mathematics.int2 position) {
		try {
			if (CivOf(__instance) is not { } civ) return;
			var layer = __instance.natureVisuals;
			var v = layer != null ? layer[position] : null;
			if (v != null) foreach (var r in v.GetComponentsInChildren<MeshRenderer>(true)) Recolour(civ, r, civ.Skins.NatureCells, "n");
		}
		catch (Exception e) { CoreLog.Once("island:nature", "island colours (nature): " + e.Message); }
	}

	private static bool AnyColours() { foreach (var s in CivilizationRegistry.AllSkins()) if (s.HasIslandColours) return true; return false; }

	private static void Recolour((int Id, CivilizationSkins Skins) civ, MeshRenderer r, List<(int x, int y, Color c)> cells, string set) {
		if (r == null || cells.Count == 0) return;
		var mat = r.sharedMaterial;
		if (mat == null || !mat.HasProperty(_ground)) return;
		var src = mat.GetTexture(_ground)?.TryCast<Texture2D>();
		if (src == null) return;
		var key = civ.Id + ":" + set + ":" + src.GetInstanceID();
		if (!Copies.TryGetValue(key, out var tex) || tex == null) {
			tex = new Texture2D(src.width, src.height, src.format, src.mipmapCount > 1) { name = "modcore-" + set + "-" + civ.Id + "-" + src.name, filterMode = src.filterMode, wrapMode = src.wrapMode };
			Graphics.CopyTexture(src, tex);
			int cs = Math.Max(1, src.width / 8);
			foreach (var (x, y, c) in cells)
				for (int a = 0; a < cs; a++) for (int b = 0; b < cs; b++) tex.SetPixel(x * cs + a, y * cs + b, c);
			tex.Apply(src.mipmapCount > 1);
			tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
			Copies[key] = tex;
		}
		var block = new MaterialPropertyBlock();
		r.GetPropertyBlock(block);
		block.SetTexture(_ground, tex);
		r.SetPropertyBlock(block);
	}
}
