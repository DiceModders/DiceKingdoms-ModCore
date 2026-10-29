namespace DiceModders.ModCore;

/// <summary>One-time setup shared by every part of the library.</summary>
internal static class Core {
	private static bool _done;

	public static void Ensure() {
		if (_done) return;
		_done = true;
		CoreRunner.Spawn();
		LobbyHooks.Install();
		MatchState.Install();
		CivilizationHooks.Install();
		CoreLog.Info("ModCore ready");
	}
}
