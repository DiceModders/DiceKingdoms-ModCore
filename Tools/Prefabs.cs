using UnityEngine;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// Copies of the game's prefabs that a mod can change (other meshes, icons, references) and hand to <c>civ.Skins</c>. The copies live under one inactive,
/// persistent object, so they never wake up, render or get unloaded; the game's own <c>Instantiate</c> of a copy makes normal, active instances.
/// The original prefab is never touched.
/// </summary>
public static class Prefabs {
	private static Transform _staging;

	/// <summary>A copy of <paramref name="prefab"/> with the same name (the game's pooling and tools expect the original naming) or <paramref name="name"/>.</summary>
	public static T Copy<T>(T prefab, string name = null) where T : Component {
		if (prefab == null) return null;
		var copy = Object.Instantiate(prefab, Staging());
		copy.gameObject.name = name ?? prefab.gameObject.name;
		return copy;
	}

	/// <summary>The same for a plain GameObject prefab.</summary>
	public static GameObject Copy(GameObject prefab, string name = null) {
		if (prefab == null) return null;
		var copy = Object.Instantiate(prefab, Staging());
		copy.name = name ?? prefab.name;
		return copy;
	}

	internal static Transform Staging() {
		if (_staging != null) return _staging;
		var go = new GameObject("DiceModders ModCore Prefabs");
		go.SetActive(false);
		go.hideFlags = HideFlags.HideAndDontSave;
		Object.DontDestroyOnLoad(go);
		_staging = go.transform;
		return _staging;
	}
}
