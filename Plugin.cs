using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace DiceModders.ModCore;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin {
	internal static new ManualLogSource Log;
	internal static BepInEx.Configuration.ConfigEntry<bool> LogLobbyEvents;

	public override void Load() {
		Log = base.Log;
		LogLobbyEvents = Config.Bind("Debug", "LogLobbyEvents", false,
			"Writes every lobby event (entered, members, host, match start/end, rule changes) to the log. For testing; off by default.");
		ModCore.Init();
	}
}
