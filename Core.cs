namespace DiceModders.ModCore;

/// <summary>One-time setup shared by every part of the library.</summary>
internal static class Core {
	private static bool _done;

	public static void Ensure() {
		if (_done) return;
		_done = true;
		LobbyHooks.Install();
		MatchGuard.Install();
		CivilizationTable.Install();
		try { new HarmonyLib.Harmony("DiceModders.ModCore.combat").PatchAll(typeof(WalkableRegions)); } catch (System.Exception e) { CoreLog.Error("walkable regions patch failed: " + e); }
		SeaLevelFill.Install();
		MatchState.Install();
		CivilizationHooks.Install();
		CoreLog.Info("ModCore ready");
	}
}
