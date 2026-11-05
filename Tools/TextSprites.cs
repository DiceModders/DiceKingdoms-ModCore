using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// Pictures inside the game's texts (research tooltips, event log, rules). The game writes a picture as <c>&lt;sprite name="..."&gt;</c> with the name of
/// the sprite it shows (for a building: the name of its icon sprite), and TextMeshPro finds it in the default sprite asset ("Icons Text") or one of its
/// fallbacks (one sheet per civilization: "Buildings Viking Text", "Units Maya Text", ...). <see cref="AddLike"/> packs a mod's pictures into a new
/// sprite asset and adds it to those fallbacks, so a sprite with that name shows the mod's picture. Each picture copies the size, metrics and scale of
/// an existing game picture and is fitted into that picture's visible bounds, so it sits in the text exactly like the game's own.
/// Call it once, after <see cref="ModCore.Civilizations.GameDataLoaded"/>.
/// </summary>
public static class TextSprites {
	/// <summary>One picture: the sprite name texts will use, the game sprite it copies size from, and the picture (a readable RGBA32 or ARGB32 texture).</summary>
	public sealed class Entry {
		private Texture2D _picture;
		private readonly Func<Texture2D> _load;
		public Entry(string name, string likeName, Texture2D picture) { Name = name; LikeName = likeName; _picture = picture; }
		/// <summary>The picture is loaded only when the sheet has to be drawn (not when <see cref="AddLike"/> finds it in the cache).</summary>
		public Entry(string name, string likeName, Func<Texture2D> loadPicture) { Name = name; LikeName = likeName; _load = loadPicture; }
		public string Name { get; }
		public string LikeName { get; }
		public Texture2D Picture { get { if (_picture == null && _load != null) { try { _picture = _load(); } catch (Exception e) { CoreLog.Once("textsprite:load:" + Name, "text sprites: picture of '" + Name + "' failed: " + e.Message); } } return _picture; } }
	}

	/// <summary>
	/// Adds the pictures as one new sprite asset named <paramref name="assetName"/>. Returns how many were added. With a <paramref name="cacheKey"/>
	/// (for example <see cref="IconAtlas.Key"/> or <see cref="TextureCache.KeyOfFiles"/> of the picture files) the drawn sheet is kept in
	/// <see cref="TextureCache"/> and later launches skip loading and drawing the pictures.
	/// </summary>
	public static int AddLike(string assetName, IEnumerable<Entry> entries, string cacheKey = null) {
		var root = TMP_Settings.defaultSpriteAsset;
		if (root == null) { CoreLog.Warn("text sprites: the game has no default sprite asset yet (call after GameDataLoaded)"); return 0; }

		// every game picture by name: (asset, character)
		var known = new Dictionary<string, (TMP_SpriteAsset asset, TMP_SpriteCharacter ch)>();
		foreach (var sa in Resources.FindObjectsOfTypeAll<TMP_SpriteAsset>()) {
			var table = sa.spriteCharacterTable;
			if (table == null) continue;
			for (int i = 0; i < table.Count; i++) { var ch = table[i]; if (ch?.name != null && !known.ContainsKey(ch.name)) known[ch.name] = (sa, ch); }
		}

		// shelf layout, 4096 wide
		var items = new List<(Entry e, TMP_SpriteAsset like, TMP_SpriteCharacter ch, int x, int y, int w, int h)>();
		const int W = 4096;
		int cx = 0, cy = 0, rowH = 0;
		var names = new HashSet<string>();
		foreach (var e in entries) {
			if (e?.Name != null && !names.Add(e.Name)) continue;   // one picture per name (many buildings use one name for every player colour)
			if (e == null || string.IsNullOrEmpty(e.Name) || !known.TryGetValue(e.LikeName ?? "", out var like) || like.ch.glyph == null) {
				CoreLog.Once("textsprite:skip:" + e?.Name, "text sprites: skipped '" + e?.Name + "' (no game sprite '" + e?.LikeName + "')");
				continue;
			}
			var r = like.ch.glyph.glyphRect;
			if (cx + r.width > W) { cx = 0; cy += rowH; rowH = 0; }
			items.Add((e, like.asset, like.ch, cx, cy, r.width, r.height));
			cx += r.width; rowH = Math.Max(rowH, r.height);
		}
		if (items.Count == 0) return 0;
		int H = 1; while (H < cy + rowH) H <<= 1;

		Texture2D atlas = null;
		string fullKey = null;
		if (cacheKey != null) {
			var layout = new System.Text.StringBuilder(cacheKey).Append("|game ").Append(Application.version).Append('|').Append(W).Append('x').Append(H);
			foreach (var it in items) layout.Append('|').Append(it.e.Name).Append('<').Append(it.e.LikeName).Append('@').Append(it.x).Append(',').Append(it.y);
			fullKey = TextureCache.KeyOfFiles(Array.Empty<string>(), layout.ToString());
			var cached = TextureCache.Load(assetName, fullKey);
			if (cached != null && cached.Length == 1) atlas = cached[0];
		}
		if (atlas == null) atlas = Draw(assetName, items, W, H, fullKey);
		return Register(assetName, atlas, items, root, W, H);
	}

	private static Texture2D Draw(string assetName, List<(Entry e, TMP_SpriteAsset like, TMP_SpriteCharacter ch, int x, int y, int w, int h)> items, int W, int H, string cacheKey) {
		var atlasBytes = new Il2CppStructArray<byte>(W * H * 4);   // zeroed: transparent
		var sheets = new Dictionary<IntPtr, (Il2CppStructArray<byte> raw, int w, bool argb)>();
		unsafe {
			byte* dst = (byte*)atlasBytes.Pointer + 4 * IntPtr.Size;
			foreach (var it in items) {
				var sheet = it.like.spriteSheet?.TryCast<Texture2D>();
				if (sheet == null || !sheet.isReadable) continue;
				if (!sheets.TryGetValue(sheet.Pointer, out var s)) { s = (sheet.GetRawTextureData(), sheet.width, sheet.format == TextureFormat.ARGB32); sheets[sheet.Pointer] = s; }
				var gr = it.ch.glyph.glyphRect;
				var pic = it.e.Picture;
				if (pic == null) { CoreLog.Once("textsprite:nopic:" + it.e.Name, "text sprites: no picture for '" + it.e.Name + "'"); continue; }
				if (!pic.isReadable || (pic.format != TextureFormat.RGBA32 && pic.format != TextureFormat.ARGB32)) {
					CoreLog.Once("textsprite:fmt:" + it.e.Name, "text sprites: '" + it.e.Name + "' needs a readable RGBA32/ARGB32 texture");
					continue;
				}
				var picRaw = pic.GetRawTextureData();
				Fit((byte*)s.raw.Pointer + 4 * IntPtr.Size, s.w, s.argb, gr.x, gr.y, gr.width, gr.height,
				    (byte*)picRaw.Pointer + 4 * IntPtr.Size, pic.width, pic.height, pic.format == TextureFormat.ARGB32,
				    dst, W, it.x, it.y);
				GC.KeepAlive(picRaw);
			}
		}
		var atlas = new Texture2D(W, H, TextureFormat.RGBA32, false) { name = assetName, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
		atlas.LoadRawTextureData(atlasBytes);
		atlas.hideFlags = HideFlags.DontUnloadUnusedAsset;
		if (cacheKey != null) TextureCache.Save(assetName, cacheKey, new[] { atlas });   // compressed, written, made GPU only
		else atlas.Apply(false, true);   // no longer readable: GPU copy only
		return atlas;
	}

	private static int Register(string assetName, Texture2D atlas, List<(Entry e, TMP_SpriteAsset like, TMP_SpriteCharacter ch, int x, int y, int w, int h)> items,
	                            TMP_SpriteAsset root, int W, int H) {
		var asset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
		asset.name = assetName;
		asset.hideFlags = HideFlags.DontUnloadUnusedAsset;
		asset.spriteSheet = atlas;
		var material = new Material(items[0].like.material) { name = assetName, hideFlags = HideFlags.DontUnloadUnusedAsset };
		material.SetTexture("_MainTex", atlas);
		asset.material = material;
		var glyphs = new Il2CppSystem.Collections.Generic.List<TMP_SpriteGlyph>();
		var chars = new Il2CppSystem.Collections.Generic.List<TMP_SpriteCharacter>();
		for (int i = 0; i < items.Count; i++) {
			var it = items[i];
			var lg = it.ch.glyph;
			var g = new TMP_SpriteGlyph { index = (uint)i, metrics = lg.metrics, glyphRect = new GlyphRect(it.x, it.y, it.w, it.h), scale = lg.scale, atlasIndex = 0 };
			var c = new TMP_SpriteCharacter(0xFFFE, g) { name = it.e.Name, scale = it.ch.scale };
			glyphs.Add(g);
			chars.Add(c);
		}
		asset.spriteGlyphTable = glyphs;
		asset.spriteCharacterTable = chars;
		// a fresh asset has no version: UpdateLookupTables would "upgrade" it from the old spriteInfoList format (null) and throw
		asset.m_Version = items[0].like.m_Version ?? "1.1.0";
		asset.UpdateLookupTables();
		root.fallbackSpriteAssets ??= new Il2CppSystem.Collections.Generic.List<TMP_SpriteAsset>();
		root.fallbackSpriteAssets.Add(asset);
		CoreLog.Info("text sprites: '" + assetName + "' with " + items.Count + " pictures (" + W + "x" + H + ") added to '" + root.name + "'");
		return items.Count;
	}

	/// <summary>Draws the picture's visible part (bilinear) into a cell of the atlas, fitted into the visible bounds of the game's picture in its sheet.</summary>
	private static unsafe void Fit(byte* sheet, int sheetW, bool sheetArgb, int gx, int gy, int gw, int gh,
	                               byte* pic, int pw, int ph, bool picArgb, byte* dst, int dstW, int dx, int dy) {
		int sa = sheetArgb ? 0 : 3, pa = picArgb ? 0 : 3, ps = picArgb ? 1 : 0;
		int x0 = gw, y0 = gh, x1 = -1, y1 = -1;
		for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++)
			if (sheet[((gy + y) * sheetW + gx + x) * 4 + sa] > 8) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
		if (x1 < 0) { x0 = 0; y0 = 0; x1 = gw - 1; y1 = gh - 1; }
		int sx0 = pw, sy0 = ph, sx1 = -1, sy1 = -1;
		for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++)
			if (pic[(y * pw + x) * 4 + pa] > 8) { if (x < sx0) sx0 = x; if (x > sx1) sx1 = x; if (y < sy0) sy0 = y; if (y > sy1) sy1 = y; }
		if (sx1 < 0) return;
		float bw = x1 - x0 + 1, bh = y1 - y0 + 1, cw = sx1 - sx0 + 1, ch = sy1 - sy0 + 1;
		float scale = Math.Min(bw / cw, bh / ch);
		float ow = cw * scale, oh = ch * scale, ox = x0 + (bw - ow) / 2f, oy = y0 + (bh - oh) / 2f;
		for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++) {
			float px = x + 0.5f, py = y + 0.5f;
			if (px < ox || px > ox + ow || py < oy || py > oy + oh) continue;
			float u = (px - ox) / scale + sx0 - 0.5f, v = (py - oy) / scale + sy0 - 0.5f;
			int xa = Math.Clamp((int)Math.Floor(u), 0, pw - 1), ya = Math.Clamp((int)Math.Floor(v), 0, ph - 1);
			int xb = Math.Min(xa + 1, pw - 1), yb = Math.Min(ya + 1, ph - 1);
			float fx = Math.Clamp(u - xa, 0f, 1f), fy = Math.Clamp(v - ya, 0f, 1f);
			byte* a = pic + (ya * pw + xa) * 4, b = pic + (ya * pw + xb) * 4, c = pic + (yb * pw + xa) * 4, e = pic + (yb * pw + xb) * 4;
			byte* d = dst + ((dy + y) * dstW + dx + x) * 4;   // atlas is RGBA32
			for (int k = 0; k < 4; k++) {
				int o = k == 3 ? pa : k + ps;
				d[k] = (byte)Math.Clamp((int)Math.Round((a[o] * (1 - fx) + b[o] * fx) * (1 - fy) + (c[o] * (1 - fx) + e[o] * fx) * fy), 0, 255);
			}
		}
	}
}
