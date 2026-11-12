using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DiceModders.ModCore;

/// <summary>
/// The game's own key bindings (its InputActionAsset: Camera, Building, Interface ...), with the player's changes from Settings > Input applied.
/// Read on demand from the asset's JSON and its binding overrides, so it is always the current state and costs nothing until asked.
/// </summary>
internal static class GameKeys {
	internal readonly record struct Binding(string Action, string Path);

	public static List<Binding> All() {
		var result = new List<Binding>();
		try {
			foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>()) {
				if (asset == null) continue;
				var overrides = new Dictionary<string, string>();
				try {
					using var o = JsonDocument.Parse(InputActionRebindingExtensions.SaveBindingOverridesAsJson(asset.Cast<IInputActionCollection2>()));
					if (o.RootElement.TryGetProperty("bindings", out var list))
						foreach (var b in list.EnumerateArray())
							if (b.TryGetProperty("id", out var id) && b.TryGetProperty("path", out var p)) overrides[id.GetString() ?? ""] = p.GetString();
				}
				catch (Exception) { }   // no overrides
				using var doc = JsonDocument.Parse(asset.ToJson());
				if (!doc.RootElement.TryGetProperty("maps", out var maps)) continue;
				foreach (var map in maps.EnumerateArray()) {
					string mapName = map.TryGetProperty("name", out var mn) ? mn.GetString() : "";
					if (!map.TryGetProperty("bindings", out var bindings)) continue;
					foreach (var b in bindings.EnumerateArray()) {
						if (b.TryGetProperty("isComposite", out var c) && c.ValueKind == JsonValueKind.True) continue;
						string path = b.TryGetProperty("path", out var p) ? p.GetString() : null;
						if (b.TryGetProperty("id", out var id) && overrides.TryGetValue(id.GetString() ?? "", out var changed) && !string.IsNullOrEmpty(changed)) path = changed;
						string action = b.TryGetProperty("action", out var a) ? a.GetString() : "";
						if (!string.IsNullOrEmpty(path) && path.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase))
							result.Add(new Binding(mapName + " / " + action, path));
					}
				}
			}
		}
		catch (Exception e) { CoreLog.Once("gamekeys", "could not read the game's key bindings: " + e.Message); }
		return result;
	}
}
