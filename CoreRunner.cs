using System;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace DiceModders.ModCore;

/// <summary>One tiny persistent MonoBehaviour that wakes the library four times a second (Steam lobby bookkeeping). Everything else is event driven.</summary>
internal sealed class CoreRunner : MonoBehaviour {
	public CoreRunner(IntPtr ptr) : base(ptr) { }

	private static CoreRunner _instance;
	private static bool _registered;
	private float _next;
	private float _nextGuard;

	internal static void Spawn() {
		if (_instance != null) return;
		if (!_registered) {
			_registered = true;
			try { ClassInjector.RegisterTypeInIl2Cpp<CoreRunner>(); }
			catch (Exception e) { CoreLog.Warn("runner registration: " + e.Message); }
		}
		var go = new GameObject("DiceModders ModCore");
		go.hideFlags = HideFlags.HideAndDontSave;
		DontDestroyOnLoad(go);
		_instance = go.AddComponent<CoreRunner>();
	}

	public void Update() {
		float now = Time.unscaledTime;
		if (now < _next) return;
		_next = now + 0.25f;
		try { LobbyState.Tick(); }
		catch (Exception e) { CoreLog.Once("runner:lobby", "lobby tick failed: " + e); }
		if (now >= _nextGuard) {
			_nextGuard = now + 1f;
			CivilizationGuard.Run();
			CivilizationText.Tick();
			GameDataDump.Tick();
		}
	}
}
