using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace DiceModders.ModCore;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin {
	internal static new ManualLogSource Log;
	internal static BepInEx.Configuration.ConfigEntry<bool> LogLobbyEvents;
	internal static BepInEx.Configuration.ConfigEntry<bool> DisableConsoleQuickEdit;
	internal static BepInEx.Configuration.ConfigEntry<bool> SaveIconPictures;

	public override void Load() {
		Log = base.Log;
		DisableConsoleQuickEdit = Config.Bind("General", "DisableConsoleQuickEdit", true,
			"Switches off QuickEdit on the BepInEx console window. With QuickEdit on, a click inside the console pauses the game until Enter is pressed.");
		LogLobbyEvents = Config.Bind("Debug", "LogLobbyEvents", false,
			"Writes every lobby event (entered, members, host, match start/end, rule changes) to the log. For testing; off by default.");
		SaveIconPictures = Config.Bind("Debug", "SaveIconPictures", false,
			"Also writes every picture sheet the icon renderer makes as a PNG into BepInEx\\cache\\ModCore\\icon-pictures, to look at. For testing; off by default.");
		if (DisableConsoleQuickEdit.Value) Tools.ConsoleGuard.DisableQuickEdit();
		ModCore.Init();
	}
}
