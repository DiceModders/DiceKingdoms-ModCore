namespace DiceModders.ModCore;

/// <summary>
/// Entry point of the library: game and networking logic shared by Dice Kingdoms mods, with no UI. <c>ModCore.Lobby</c> reports the Steam lobby and carries
/// host-synced lobby rules, <c>ModCore.Civilizations</c> makes the game accept civilizations that mods add.
/// </summary>
public static partial class ModCore {
	/// <summary>Installs the hooks (ModCore has no update loop: everything follows game and Steam events). Optional: every public entry point calls it. Safe to call any number of times.</summary>
	public static void Init() => Core.Ensure();
}
