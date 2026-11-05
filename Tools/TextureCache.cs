using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// Textures a mod builds from many files (icon atlases, text picture sheets) are built ONCE and kept on disk, already GPU-compressed (DXT5), in
/// <c>BepInEx\cache\ModCore</c>. Later launches only read one file and hand the bytes to the GPU: no PNG decoding, no pixel work, about 4x less video
/// memory. The cache is keyed by the inputs (<see cref="KeyOfFiles"/>): change, add or remove a source file and it is rebuilt on the next launch.
/// Deleting the folder is always safe.
/// </summary>
public static class TextureCache {
	private const int Magic = 0x5854434D, Version = 2;   // "MCTX"; 2: mip levels stored

	public static string Folder => Path.Combine(DiskCache.Root, "textures");

	/// <summary>A key for a set of source files: their paths, sizes and change times (a missing file counts too), plus anything else that changes the result.</summary>
	public static string KeyOfFiles(IEnumerable<string> paths, string extra = null) {
		var sb = new StringBuilder(extra ?? "");
		foreach (var p in paths) {
			var fi = new FileInfo(p);
			sb.Append('|').Append(p).Append(':');
			if (fi.Exists) sb.Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks); else sb.Append('-');
		}
		using var sha = SHA1.Create();
		return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
	}

	private static string FileOf(string name) => DiskCache.PathFor("textures", name + ".tex");   // unused files are removed after a few launches

	/// <summary>Starts reading these cached textures in the background (call early, e.g. in your plugin's Load).</summary>
	public static void Prefetch(params string[] names) { foreach (var n in names) DiskCache.Prefetch(FileOf(n)); }

	/// <summary>The cached textures (GPU only, not readable) when the cache holds <paramref name="key"/>; otherwise null.</summary>
	public static Texture2D[] Load(string name, string key) {
		var path = FileOf(name);
		if (!File.Exists(path)) return null;
		try {
			using var r = new BinaryReader(new MemoryStream(DiskCache.ReadAllBytes(path)));
			if (r.ReadInt32() != Magic || r.ReadInt32() != Version || r.ReadString() != key) return null;
			int pages = r.ReadInt32();
			var result = new Texture2D[pages];
			for (int i = 0; i < pages; i++) {
				int w = r.ReadInt32(), h = r.ReadInt32(), format = r.ReadInt32(), mips = r.ReadInt32(), length = r.ReadInt32();
				var bytes = r.ReadBytes(length);
				var tex = new Texture2D(w, h, (TextureFormat)format, mips > 1) {
					name = name + (pages > 1 ? " " + i : ""), filterMode = mips > 1 ? FilterMode.Trilinear : FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.DontUnloadUnusedAsset,
				};
				unsafe { fixed (byte* p = bytes) tex.LoadRawTextureData((IntPtr)p, length); }
				tex.Apply(false, true);   // GPU copy only
				result[i] = tex;
			}
			return result;
		}
		catch (Exception e) { CoreLog.Warn("texture cache '" + name + "' could not be read, rebuilding: " + e.Message); return null; }
	}

	/// <summary>
	/// Compresses the readable textures to DXT5 (when <paramref name="compress"/>), writes them under <paramref name="key"/> and makes them GPU only.
	/// Call with textures you just built; they are not readable afterwards.
	/// </summary>
	public static void Save(string name, string key, IReadOnlyList<Texture2D> pages, bool compress = true) {
		try {
			using var ms = new MemoryStream();
			using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) {
				w.Write(Magic); w.Write(Version); w.Write(key); w.Write(pages.Count);
				foreach (var tex in pages) {
					if (compress && tex.width % 4 == 0 && tex.height % 4 == 0) tex.Compress(true);
					var raw = tex.GetRawTextureData();
					var bytes = new byte[raw.Length];
					unsafe { fixed (byte* d = bytes) Buffer.MemoryCopy((byte*)raw.Pointer + 4 * IntPtr.Size, d, bytes.Length, bytes.Length); }
					GC.KeepAlive(raw);
					w.Write(tex.width); w.Write(tex.height); w.Write((int)tex.format); w.Write(tex.mipmapCount); w.Write(bytes.Length); w.Write(bytes);
					tex.Apply(false, true);
				}
			}
			File.WriteAllBytes(FileOf(name), ms.ToArray());
		}
		catch (Exception e) {
			CoreLog.Warn("texture cache '" + name + "' could not be written: " + e.Message);
			foreach (var tex in pages) { try { if (tex.isReadable) tex.Apply(false, true); } catch { } }
		}
	}

	/// <summary>
	/// Gives fully transparent pixels next to a picture the colour of their visible neighbours (alpha stays 0), <paramref name="passes"/> pixels deep.
	/// Smaller mip levels and bilinear filtering average neighbouring pixels; without this the black of the empty pixels would darken every edge.
	/// </summary>
	internal static unsafe void BleedColour(byte* px, int w, int h, int passes) {
		var done = new bool[w * h];
		for (int i = 0; i < w * h; i++) done[i] = px[i * 4 + 3] > 0;
		var next = new List<int>();
		for (int pass = 0; pass < passes; pass++) {
			next.Clear();
			for (int y = 0; y < h; y++)
				for (int x = 0; x < w; x++) {
					int i = y * w + x;
					if (done[i]) continue;
					int r = 0, g = 0, b = 0, n = 0;
					for (int dy = -1; dy <= 1; dy++) {
						int yy = y + dy; if (yy < 0 || yy >= h) continue;
						for (int dx = -1; dx <= 1; dx++) {
							int xx = x + dx; if (xx < 0 || xx >= w) continue;
							int j = yy * w + xx;
							if (!done[j]) continue;
							r += px[j * 4]; g += px[j * 4 + 1]; b += px[j * 4 + 2]; n++;
						}
					}
					if (n == 0) continue;
					px[i * 4] = (byte)(r / n); px[i * 4 + 1] = (byte)(g / n); px[i * 4 + 2] = (byte)(b / n);
					next.Add(i);
				}
			if (next.Count == 0) break;
			foreach (var i in next) done[i] = true;
		}
	}

	/// <summary>Copies a picture (readable RGBA32 or ARGB32) into an RGBA32 byte buffer, centred in the cell at (x, y) of size cw x ch (cropped if larger).</summary>
	internal static unsafe void Blit(Texture2D pic, byte* dst, int dstW, int x, int y, int cw, int ch) {
		bool argb = pic.format == TextureFormat.ARGB32;
		if (!argb && pic.format != TextureFormat.RGBA32) throw new InvalidDataException("picture '" + pic.name + "' is " + pic.format + ", not RGBA32/ARGB32");
		var raw = pic.GetRawTextureData();
		byte* src = (byte*)raw.Pointer + 4 * IntPtr.Size;
		int pw = pic.width, ph = pic.height;
		int ox = (cw - pw) / 2, oy = (ch - ph) / 2;
		for (int py = 0; py < ph; py++) {
			int ty = oy + py;
			if (ty < 0 || ty >= ch) continue;
			byte* s = src + py * pw * 4;
			byte* d = dst + ((y + ty) * dstW + x) * 4;
			for (int px = 0; px < pw; px++) {
				int tx = ox + px;
				if (tx < 0 || tx >= cw) continue;
				byte* sp = s + px * 4, dp = d + tx * 4;
				if (argb) { dp[0] = sp[1]; dp[1] = sp[2]; dp[2] = sp[3]; dp[3] = sp[0]; }
				else { dp[0] = sp[0]; dp[1] = sp[1]; dp[2] = sp[2]; dp[3] = sp[3]; }
			}
		}
		GC.KeepAlive(raw);
	}
}

/// <summary>
/// Many same-size pictures (for example a civilization's building and unit icons, 12 player colours each) packed into a few 4096x4096 pages,
/// cached on disk by <see cref="TextureCache"/>. The first launch decodes the PNGs; every later launch reads one file. Missing files leave an empty cell.
/// </summary>
public sealed class IconAtlas {
	public const int PageSize = 4096;
	private readonly Texture2D[] _pages;
	private readonly bool[] _present;

	public int CellWidth { get; }
	public int CellHeight { get; }
	public int Count => _present.Length;
	/// <summary>The cache key (inputs' paths, sizes and change times); another cached texture built from the same pictures can reuse it.</summary>
	public string Key { get; }
	/// <summary>Built from the PNGs during this launch (the cache was missing or stale).</summary>
	public bool Rebuilt { get; }

	private IconAtlas(Texture2D[] pages, bool[] present, int cw, int ch, string key, bool rebuilt) {
		_pages = pages; _present = present; CellWidth = cw; CellHeight = ch; Key = key; Rebuilt = rebuilt;
	}

	private int Columns => PageSize / CellWidth;
	private int PerPage => Columns * (PageSize / CellHeight);

	public bool Has(int index) => index >= 0 && index < _present.Length && _present[index];

	/// <summary>A sprite showing cell <paramref name="index"/> (null when its file was missing). Pivot as a fraction of the cell; 100 pixels per unit like the game's icons.</summary>
	public Sprite Sprite(int index, Vector2 pivot, string name) {
		if (!Has(index)) return null;
		int page = index / PerPage, i = index % PerPage;
		var tex = _pages[page];
		var rect = new Rect(i % Columns * CellWidth, i / Columns * CellHeight, CellWidth, CellHeight);
		var s = UnityEngine.Sprite.Create(tex, rect, pivot, 100f, 0, SpriteMeshType.FullRect);
		s.name = name;
		s.hideFlags = HideFlags.DontUnloadUnusedAsset;
		return s;
	}

	/// <summary>Packs <paramref name="files"/> (PNG/JPG, each centred in a cell of the given size) or loads the cached result.</summary>
	public static IconAtlas Build(string name, IReadOnlyList<string> files, int cellWidth, int cellHeight) {
		if (cellWidth <= 0 || cellHeight <= 0 || cellWidth > PageSize || cellHeight > PageSize) throw new ArgumentOutOfRangeException(nameof(cellWidth));
		var present = new bool[files.Count];
		for (int i = 0; i < files.Count; i++) present[i] = File.Exists(files[i]);
		string key = TextureCache.KeyOfFiles(files, "atlas " + cellWidth + "x" + cellHeight + " mips bleed");
		int cols = PageSize / cellWidth, perPage = cols * (PageSize / cellHeight);
		int pageCount = Math.Max(1, (files.Count + perPage - 1) / perPage);

		var cached = TextureCache.Load(name, key);
		if (cached != null && cached.Length == pageCount) return new IconAtlas(cached, present, cellWidth, cellHeight, key, false);

		var pages = new Texture2D[pageCount];
		for (int p = 0; p < pageCount; p++) {
			int n = Math.Min(perPage, files.Count - p * perPage);
			int w = Math.Min(n, cols) * cellWidth, h = (n + cols - 1) / cols * cellHeight;
			w = (w + 3) / 4 * 4; h = (h + 3) / 4 * 4;
			var buffer = new Il2CppStructArray<Color32>(w * h);   // zeroed: transparent; Color32 is RGBA bytes
			unsafe {
				byte* dst = (byte*)buffer.Pointer + 4 * IntPtr.Size;
				for (int k = 0; k < n; k++) {
					int index = p * perPage + k;
					if (!present[index]) continue;
					var pic = new Texture2D(2, 2, TextureFormat.RGBA32, false);
					try {
						if (!ImageConversion.LoadImage(pic, File.ReadAllBytes(files[index]), false)) { present[index] = false; CoreLog.Warn("icon atlas: not an image " + files[index]); continue; }
						TextureCache.Blit(pic, dst, w, k % cols * cellWidth, k / cols * cellHeight, cellWidth, cellHeight);
					}
					finally { UnityEngine.Object.Destroy(pic); }
				}
				TextureCache.BleedColour(dst, w, h, 8);
			}
			// with mipmaps: the UI shows icons far smaller than 256 px, and without smaller levels the edges look hard and jagged
			var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) {
				name = name + (pageCount > 1 ? " " + p : ""), filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.DontUnloadUnusedAsset,
			};
			tex.SetPixels32(buffer, 0);
			tex.Apply(true, false);   // builds the smaller levels; still readable for compression
			pages[p] = tex;
		}
		TextureCache.Save(name, key, pages);
		CoreLog.Info("icon atlas '" + name + "': " + files.Count + " pictures packed into " + pageCount + " page(s) and cached");
		return new IconAtlas(pages, present, cellWidth, cellHeight, key, true);
	}
}
