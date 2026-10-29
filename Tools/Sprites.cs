using System;
using System.IO;
using UnityEngine;

namespace DiceModders.ModCore.Tools;

/// <summary>Sprites from image files, for icons a mod brings along (building pictures, buttons).</summary>
public static class Sprites {
	/// <summary>
	/// Loads a PNG or JPG file as a sprite. The pivot defaults to the centre and 100 pixels per unit (what the game's building icons use).
	/// The texture is bilinear, without mipmaps, and marked <c>DontUnloadUnusedAsset</c> so a scene change does not unload it.
	/// </summary>
	public static Sprite Load(string path, Vector2? pivot = null, float pixelsPerUnit = 100f) {
		if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
		byte[] bytes = File.ReadAllBytes(path);
		var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) {
			name = Path.GetFileNameWithoutExtension(path),
			filterMode = FilterMode.Bilinear,
			wrapMode = TextureWrapMode.Clamp,
			hideFlags = HideFlags.DontUnloadUnusedAsset,
		};
		if (!ImageConversion.LoadImage(texture, bytes, true)) throw new InvalidDataException("not a PNG/JPG image: " + path);
		var sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), pivot ?? new Vector2(0.5f, 0.5f), pixelsPerUnit);
		sprite.name = texture.name;
		sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
		return sprite;
	}

	/// <summary>
	/// Loads one sprite per player colour from <paramref name="folder"/>: <c>00.png</c> ... <c>11.png</c>, in the game's colour order
	/// (0 Red, 1 Pink, 2 Orange, 3 Green, 4 Carribean, 5 Cyan, 6 Yellow, 7 Ivory, 8 Brown, 9 Blue, 10 Grey, 11 Purple).
	/// </summary>
	public static Sprite[] LoadPlayerColorSet(string folder, int count = 12) {
		var result = new Sprite[count];
		for (int i = 0; i < count; i++) result[i] = Load(Path.Combine(folder, i.ToString("00") + ".png"));
		return result;
	}
}
