using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace DiceModders.ModCore.Tools;

/// <summary>A Harmony patch that is applied while <see cref="On"/> is true and removed while it is false. Made by <see cref="Patcher.Switch(Type, string, Type, string, string)"/>.</summary>
public sealed class PatchSwitch {
	private readonly Harmony _harmony;
	private readonly MethodBase _target;
	private readonly MethodInfo _prefix, _postfix;
	private readonly string _label;
	private readonly ManualLogSource _log;
	private bool _on;

	internal PatchSwitch(Harmony harmony, MethodBase target, MethodInfo prefix, MethodInfo postfix, string label, ManualLogSource log) {
		_harmony = harmony; _target = target; _prefix = prefix; _postfix = postfix; _label = label; _log = log;
	}

	/// <summary>False when the method or the patch methods were not found (then <see cref="On"/> does nothing).</summary>
	public bool Valid => _harmony != null;

	/// <summary>Applies (true) or removes (false) the patch. Setting the same value again does nothing.</summary>
	public bool On {
		get => _on;
		set {
			if (value == _on || !Valid) return;
			try {
				if (value) _harmony.Patch(_target, _prefix == null ? null : new HarmonyMethod(_prefix), _postfix == null ? null : new HarmonyMethod(_postfix));
				else {
					if (_prefix != null) _harmony.Unpatch(_target, _prefix);
					if (_postfix != null) _harmony.Unpatch(_target, _postfix);
				}
				_on = value;
				_log?.LogInfo("[patch] " + (value ? "on   " : "off  ") + _label);
			}
			catch (Exception e) { _log?.LogWarning("[patch] switch " + _label + " failed: " + e.Message); }
		}
	}
}
