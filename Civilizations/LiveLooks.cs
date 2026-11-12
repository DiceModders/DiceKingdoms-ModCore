using System;
using Utility.Pooling;

namespace DiceModders.ModCore;

using GameBuildingVisual = DiceKingdoms.Game.BuildingVisual;
using GameIslandGrid = DiceKingdoms.Game.IslandGrid;

/// <summary>
/// Rebuilds the building models of an island from its owner's CURRENT skins, the way <c>IslandGrid.AddBuilding</c> (180611500) makes them:
/// prefab = <c>PlayerSkins.GetBuildingVisualPrefab(type)</c>, a copy from the game's pool (<c>Pooling.Allocate</c>) under <c>buildingsParent</c>, stored
/// in <c>buildingVisuals[id]</c>, then <c>UpdateBuildingVisual</c> sets health, rotation, wall joins. The old copy goes back to the pool (<c>Pooling.Free</c>).
/// The building animation (<c>BuildingVisual.OnPlace</c>) is not played. Purely visual: the buildings themselves are not touched.
/// Units, ships and nature pick their models when they are made, so they follow the new skins from then on.
/// </summary>
internal static class LiveLooks {
	public static int Refresh(GameIslandGrid grid) {
		if (grid == null) return 0;
		var skins = grid.island?.Skins;
		var visuals = grid.buildingVisuals;
		var parent = grid.buildingsParent;
		if (skins == null || visuals == null || parent == null) return 0;
		int done = 0;
		foreach (var building in grid.Buildings) {
			try {
				var id = building.id;
				if (!visuals.ContainsKey(id)) continue;
				var prefab = skins.GetBuildingVisualPrefab(building.type);
				if (prefab == null) continue;
				var old = visuals[id];
				if (old != null) Pooling.Free(old.gameObject);
				visuals[id] = Pooling.Allocate<GameBuildingVisual>(prefab, parent);
				grid.UpdateBuildingVisual(building);
				done++;
			}
			catch (Exception e) { CoreLog.Once("looks:refresh", "live looks: a building could not be rebuilt: " + e.Message); }
		}
		return done;
	}
}

public static partial class ModCore {
	public static partial class Civilizations {
		/// <summary>
		/// Rebuilds the building models of this player's island from the player's current looks (after a mod changed a civilization's skins, e.g.
		/// with <see cref="Reskin"/> or a per-player skins choice). Visual only, on this computer. Returns how many buildings were rebuilt.
		/// </summary>
		public static int RefreshLooks(MatchPlayer player) {
			Core.Ensure();
			try { return LiveLooks.Refresh(player?.Island?.grid); }
			catch (Exception e) { CoreLog.Warn("live looks: " + e.Message); return 0; }
		}

		/// <summary>Rebuilds the building models of every island of the match (see <see cref="RefreshLooks(MatchPlayer)"/>).</summary>
		public static int RefreshLooks() {
			int n = 0;
			foreach (var p in Players.All) n += RefreshLooks(p);
			CoreLog.Info("live looks: " + n + " building model(s) rebuilt");
			return n;
		}
	}
}
