using System;
using System.Globalization;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// A gentle motion for a decoration that floats: it turns about its up axis and bobs up and down (a crystal over a cauldron, a floating book).
/// The settings live in the object's name, so copies of a prefab (the game clones buildings) keep them without any extra state:
/// <c>Motion.Add(go, spin: 40f, bob: 0.03f, period: 3f)</c> renames <paramref name="go"/> to "...|motion 40 0.03 3" and adds the component.
/// Visual only; uses the game's time (game speed applies, pause stops it).
/// </summary>
public static class Motion {
	private const string Tag = "|motion ";

	/// <summary>Adds the motion to <paramref name="go"/>. spin: degrees per second about up (negative turns the other way); bob: height of the
	/// up-and-down move in model units; period: seconds for one bob.</summary>
	public static void Add(GameObject go, float spin, float bob, float period) {
		if (go == null) return;
		if (!ClassInjector.IsTypeRegisteredInIl2Cpp<MotionDriver>()) ClassInjector.RegisterTypeInIl2Cpp<MotionDriver>();
		var name = go.name;
		int at = name.IndexOf(Tag, StringComparison.Ordinal);
		if (at >= 0) name = name.Substring(0, at);
		go.name = name + Tag + FormattableString.Invariant($"{spin} {bob} {Math.Max(0.1f, period)}");
		if (go.GetComponent<MotionDriver>() == null) go.AddComponent<MotionDriver>();
	}

	internal static bool Read(string name, out float spin, out float bob, out float period) {
		spin = bob = 0f; period = 1f;
		int at = name?.IndexOf(Tag, StringComparison.Ordinal) ?? -1;
		if (at < 0) return false;
		var p = name.Substring(at + Tag.Length).Split(' ');
		return p.Length >= 3 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out spin)
			&& float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bob) && float.TryParse(Leading(p[2]), NumberStyles.Float, CultureInfo.InvariantCulture, out period);
	}

	/// <summary>The number at the start: Unity adds "(Clone)" to the name of a copy ("...|motion 40 0.03 3(Clone)").</summary>
	private static string Leading(string s) {
		int n = 0;
		while (n < s.Length && (char.IsDigit(s[n]) || s[n] == '.' || s[n] == '-' || s[n] == 'E' || s[n] == 'e' || s[n] == '+')) n++;
		return s.Substring(0, n);
	}
}

/// <summary>Moves its object as its name says (see <see cref="Motion"/>). Each copy starts from where it stands and at a phase of its own.</summary>
internal sealed class MotionDriver : MonoBehaviour {
	public MotionDriver(IntPtr ptr) : base(ptr) { }
	private bool _ready, _on;
	private float _spin, _bob, _period, _phase, _angle;
	private Vector3 _home;
	private Quaternion _start;

	public void Update() {
		if (!_ready) {
			_ready = true;
			_on = Motion.Read(gameObject.name, out _spin, out _bob, out _period);
			_home = transform.localPosition; _start = transform.localRotation;
			_phase = (GetInstanceID() & 1023) / 1023f * Mathf.PI * 2f;
			_angle = _phase * Mathf.Rad2Deg;
			if (!_on) { enabled = false; return; }
		}
		float dt = Time.deltaTime;
		_angle = (_angle + _spin * dt) % 360f;
		transform.localRotation = _start * Quaternion.Euler(0f, _angle, 0f);
		transform.localPosition = _home + new Vector3(0f, _bob * Mathf.Sin(Time.time * Mathf.PI * 2f / _period + _phase), 0f);
	}
}
