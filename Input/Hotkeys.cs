using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace DiceModders.ModCore;

/// <summary>
/// A key a mod reacts to, on the game's own input system (Unity Input System, the same one the game's keys and its Settings > Input page use).
/// Unity calls back when the key is pressed, so nothing checks the keyboard every frame. The keys live in the mod's config file
/// (<c>[Hotkeys]</c>), so players can change them there, in an in-game config editor, or on a screen that lists <see cref="ModCore.Hotkeys.All"/>.
/// </summary>
public sealed class Hotkey {
	private readonly ConfigEntry<string> _entry;
	private InputAction _action;

	internal Hotkey(LobbyMod mod, ConfigEntry<string> entry, string id, string title, string defaultKeys, Func<bool> when) {
		Mod = mod; _entry = entry; Id = id; Title = title; Default = defaultKeys; When = when;
		_entry.SettingChanged += (_, _) => HotkeyKeys.Later(() => { Bind(); ModCore.Hotkeys.RaiseChanged(this); });   // never inside an input callback
	}

	public string Id { get; }
	public string Title { get; }
	/// <summary>The keys, e.g. <c>"F9"</c>, <c>"Ctrl+E"</c>, <c>"Shift+Alt+H"</c>; empty = not bound. Key names are Unity's <c>Key</c> names.</summary>
	public string Keys => _entry.Value ?? "";
	public string Default { get; }
	public string ModId => Mod.Id;
	public string ModName => Mod.Name;
	/// <summary>Optional: the key only fires while this returns true (e.g. while the mod's window is open).</summary>
	public Func<bool> When { get; set; }
	public event Action Pressed;

	internal LobbyMod Mod { get; }

	/// <summary>Sets the keys (also written to the config file). Empty unbinds.</summary>
	public void Set(string keys) => _entry.Value = HotkeyKeys.Normalize(keys);
	public void Reset() => _entry.Value = Default;

	/// <summary>Waits for the next key press (with any held Ctrl / Shift / Alt) and binds it; Escape cancels. <paramref name="done"/> gets the result.</summary>
	public void Capture(Action<string> done) => HotkeyKeys.CaptureNext(keys => { if (keys != null) Set(keys); done?.Invoke(Keys); });

	/// <summary>
	/// What else reacts when these keys are pressed: other mod keys with the same keys, and the game's own keys (Settings > Input) on the same key.
	/// A clash is not always a mistake (a key that only works while a window is open may share a game key on purpose), so screens show it as a warning.
	/// </summary>
	public IReadOnlyList<string> Clashes() {
		var list = new List<string>();
		if (!HotkeyKeys.TryParse(Keys, out var key, out _)) return list;
		foreach (var other in ModCore.Hotkeys.All)
			if (other != this && other.Keys == Keys) list.Add(other.ModName + ": " + other.Title);
		string path = "<Keyboard>/" + HotkeyKeys.ControlName(key);
		foreach (var game in GameKeys.All())
			if (string.Equals(game.Path, path, StringComparison.OrdinalIgnoreCase) && !list.Contains("the game: " + game.Action)) list.Add("the game: " + game.Action);
		return list;
	}

	internal void Bind() {
		try {
			if (_action != null) { HotkeyKeys.Forget(_action); _action.Disable(); _action.Dispose(); }
			_action = null;
			if (!HotkeyKeys.TryParse(Keys, out var key, out _)) return;
			// the binding path comes from the key name, so it does not need the keyboard device to exist yet (it does not at game data load)
			string path = "<Keyboard>/" + HotkeyKeys.ControlName(key);
			_action = new InputAction("modcore." + Mod.Id + "." + Id, InputActionType.Button, path);
			HotkeyKeys.Listen(_action, Fire);
			_action.Enable();
			CoreLog.Info("hotkey '" + Title + "' bound to " + Keys + " (" + Mod.Name + ", " + path + ")");
		}
		catch (Exception e) { CoreLog.Once("hotkey:" + Id, "hotkey '" + Title + "' (" + Keys + "): " + e.Message); }
	}

	private void Fire() {
		string blocked = HotkeyKeys.Capturing ? "capturing a key" : !ModSwitches.IsOn(Mod.Id) ? "mod switched off" : HotkeyKeys.Typing() ? "typing in a text field"
			: !HotkeyKeys.ModifiersMatch(Keys) ? "other modifiers held" : null;
		if (blocked == null) { try { if (When != null && !When()) blocked = "its window is not open"; } catch (Exception e) { blocked = "when() failed: " + e.Message; } }
		CoreLog.Info("hotkey '" + Title + "' (" + Keys + ") pressed" + (blocked == null ? "" : ", ignored: " + blocked));
		if (blocked != null) return;
		var handler = Pressed;
		if (handler == null) return;
		foreach (Action d in handler.GetInvocationList()) {
			try { d(); } catch (Exception e) { CoreLog.Error("hotkey '" + Title + "' handler failed: " + e); }
		}
	}
}

/// <summary>Key text ("Ctrl+E") to the input system and back, the typing check, and capturing the next key press.</summary>
internal static class HotkeyKeys {
	private static readonly string[] Modifiers = { "Ctrl", "Shift", "Alt" };

	public static string Normalize(string keys) {
		if (!TryParse(keys, out var key, out var mods)) return "";
		return string.Join("+", Modifiers.Where(mods.Contains).Append(key.ToString()));
	}

	public static bool TryParse(string keys, out Key key, out HashSet<string> mods) {
		key = Key.None; mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(keys)) return false;
		foreach (var raw in keys.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0)) {
			var m = Modifiers.FirstOrDefault(x => x.Equals(raw, StringComparison.OrdinalIgnoreCase) || (x == "Ctrl" && raw.Equals("Control", StringComparison.OrdinalIgnoreCase)));
			if (m != null) { mods.Add(m); continue; }
			if (!Enum.TryParse(raw, true, out key) || key == Key.None) return false;
		}
		return key != Key.None;
	}

	/// <summary>
	/// The keyboard layout's control name of a key: "a" for A, "1" for Digit1, otherwise the key name starting lower-case ("f9", "pageUp",
	/// "leftArrow", "numpadEnter", "space") - the names Unity's Keyboard layout gives its key controls.
	/// </summary>
	public static string ControlName(Key key) {
		string name = key.ToString();
		if (key >= Key.Digit1 && key <= Key.Digit0) return name.Substring(5);
		return char.ToLowerInvariant(name[0]) + name.Substring(1);
	}

	/// <summary>Exactly the modifiers of the binding are held (so "E" does not fire for Ctrl+E).</summary>
	public static bool ModifiersMatch(string keys) {
		var kb = Keyboard.current;
		if (kb == null || !TryParse(keys, out _, out var mods)) return false;
		return kb.ctrlKey.isPressed == mods.Contains("Ctrl") && kb.shiftKey.isPressed == mods.Contains("Shift") && kb.altKey.isPressed == mods.Contains("Alt");
	}

	/// <summary>A text field (chat, lobby name ...) has the focus: keys are text then, not commands.</summary>
	public static bool Typing() {
		var selected = EventSystem.current?.currentSelectedGameObject;
		if (selected == null) return false;
		var tmp = selected.GetComponent<TMPro.TMP_InputField>();
		if (tmp != null && tmp.isFocused) return true;
		var legacy = selected.GetComponent<UnityEngine.UI.InputField>();
		return legacy != null && legacy.isFocused;
	}

	// ---- one listener for every mod key ----------------------------------------------------------------------------------------------
	// InputAction.performed passes a CallbackContext struct, which Il2CppInterop cannot wrap in a delegate. InputSystem.onActionChange passes
	// (object action, InputActionChange), which it can: one global listener, a dictionary lookup per call, no polling.

	private static readonly Dictionary<IntPtr, Action> Actions = new();
	private static Il2CppSystem.Action<Il2CppSystem.Object, InputActionChange> _listener;

	public static void Listen(InputAction action, Action performed) {
		if (_listener == null) {
			_listener = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<Il2CppSystem.Object, InputActionChange>>(
				new Action<Il2CppSystem.Object, InputActionChange>(OnActionChange));
			InputSystem.add_onActionChange(_listener);
		}
		Actions[action.Pointer] = performed;
	}

	public static void Forget(InputAction action) => Actions.Remove(action.Pointer);

	private static void OnActionChange(Il2CppSystem.Object obj, InputActionChange change) {
		if (change != InputActionChange.ActionPerformed || obj == null || !Actions.TryGetValue(obj.Pointer, out var performed)) return;
		try { performed(); } catch (Exception e) { CoreLog.Error("hotkey: " + e); }
	}

	// ---- capturing the next key press -------------------------------------------------------------------------------------------------

	private static InputAction _capture;

	/// <summary>True while a key is being captured: the mod keys do not fire then (Escape cancels the capture, it does not close a window).</summary>
	public static bool Capturing => _capture != null || UnityEngine.Time.frameCount == _captureEnded;   // the Escape that cancelled it too

	/// <summary>
	/// A temporary pass-through action on every key: each key press reports, the first one that is not a modifier, plus the held modifiers,
	/// is the answer. Escape cancels.
	/// </summary>
	public static void CaptureNext(Action<string> done) {
		StopCapture();
		var kb = Keyboard.current;
		if (kb == null) { done(null); return; }
		var action = new InputAction("modcore.capture", InputActionType.PassThrough, "<Keyboard>/<Key>");
		_capture = action;
		_captured = false;
		Listen(action, () => {
			var pressed = action.activeControl?.TryCast<KeyControl>();
			if (pressed == null || !pressed.wasPressedThisFrame) return;   // a key release
			if (pressed.keyCode is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;   // keep waiting
			if (_captured) return;
			_captured = true;
			string result = null;   // Escape cancels
			if (pressed.keyCode != Key.Escape) {
				var mods = new List<string>();
				if (kb.ctrlKey.isPressed) mods.Add("Ctrl");
				if (kb.shiftKey.isPressed) mods.Add("Shift");
				if (kb.altKey.isPressed) mods.Add("Alt");
				mods.Add(pressed.keyCode.ToString());
				result = string.Join("+", mods);
			}
			// Unity must not destroy an input action inside its own callback: finish after this input update
			Later(() => { StopCapture(); done(result); });
		});
		action.Enable();
	}

	private static int _captureEnded = -1;
	private static bool _captured;

	// ---- running something after the current input update ----------------------------------------------------------------------------
	// Input actions must not be disposed or rebound inside an action callback (Unity asserts and can crash). Later() runs the work from
	// InputSystem.onAfterUpdate, subscribed only while something is waiting.

	private static readonly List<Action> Pending = new();
	private static Il2CppSystem.Action _afterUpdate;
	private static bool _hooked;

	public static void Later(Action work) {
		Pending.Add(work);
		if (_hooked) return;
		_afterUpdate ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(RunPending));
		InputSystem.add_onAfterUpdate(_afterUpdate);
		_hooked = true;
	}

	private static void RunPending() {
		InputSystem.remove_onAfterUpdate(_afterUpdate);
		_hooked = false;
		var work = Pending.ToArray();
		Pending.Clear();
		foreach (var w in work) {
			try { w(); } catch (Exception e) { CoreLog.Error("hotkey: " + e); }
		}
	}

	private static void StopCapture() {
		if (_capture == null) return;
		_captureEnded = UnityEngine.Time.frameCount;
		var a = _capture;
		_capture = null;
		Forget(a);
		a.Disable();
		a.Dispose();
	}
}

public static partial class ModCore {
	/// <summary>Keys mods react to, on the game's own input system. See <see cref="Hotkey"/>.</summary>
	public static class Hotkeys {
		private static readonly List<Hotkey> Items = new();
		private static bool _ready;

		/// <summary>Every registered hotkey, in registration order (for a screen that lets players change them).</summary>
		public static IReadOnlyList<Hotkey> All => Items;
		/// <summary>A hotkey was added.</summary>
		public static event Action<Hotkey> Added;
		/// <summary>A hotkey's keys changed.</summary>
		public static event Action<Hotkey> Changed;

		/// <summary>
		/// A key for your mod, stored in your config file as <c>[Hotkeys] id = keys</c>. <paramref name="pressed"/> runs on the main thread when it is
		/// pressed (not while a text field has the focus, not while your mod is switched off, only with exactly the given modifiers held).
		/// </summary>
		/// <param name="defaultKeys">e.g. <c>"F9"</c>, <c>"Ctrl+E"</c>; empty = unbound until the player sets one. Avoid the game's own keys.</param>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static Hotkey Add(ConfigFile config, string id, string title, string defaultKeys, Action pressed, Func<bool> when = null) {
			var mod = LobbyMods.Identify(System.Reflection.Assembly.GetCallingAssembly());
			Core.Ensure();
			if (config == null) throw new ArgumentNullException(nameof(config));
			if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required", nameof(id));
			var entry = config.Bind("Hotkeys", id, HotkeyKeys.Normalize(defaultKeys), title + " (keys like F9, Ctrl+E, Shift+Alt+H; empty = none)");
			var hotkey = new Hotkey(mod, entry, id, title ?? id, HotkeyKeys.Normalize(defaultKeys), when);
			if (pressed != null) hotkey.Pressed += pressed;
			Items.Add(hotkey);
			if (_ready) hotkey.Bind();
			else Civilizations.WhenGameDataLoaded(BindAll);   // the keyboard device exists from the main menu on
			Added?.Invoke(hotkey);
			return hotkey;
		}

		private static void BindAll() {
			if (_ready) return;
			_ready = true;
			foreach (var h in Items) h.Bind();
		}

		internal static void RaiseChanged(Hotkey h) => Changed?.Invoke(h);
	}
}
