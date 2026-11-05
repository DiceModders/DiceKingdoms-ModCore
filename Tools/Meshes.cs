using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// Builds meshes at runtime. The game's IL2CPP build has <c>Mesh.RecalculateNormals</c> stripped (it throws), so normals are computed here with plain
/// float math. Meshes are marked <c>DontUnloadUnusedAsset</c> so a scene change does not unload them.
/// </summary>
public static class Meshes {
	/// <summary>
	/// A mesh from vertices (prefab space of the model you replace), UVs and triangles (3 indices each). Normals are computed per vertex from the faces
	/// that use it. Uses 32-bit indices when there are more than 65535 vertices.
	/// </summary>
	public static Mesh Create(string name, Vector3[] vertices, Vector2[] uvs, int[] triangles) {
		if (vertices == null || triangles == null) throw new ArgumentNullException(vertices == null ? nameof(vertices) : nameof(triangles));
		var mesh = new Mesh { name = name ?? "mod-mesh" };
		mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
		mesh.vertices = vertices;   // also updates the bounds
		if (uvs != null && uvs.Length == vertices.Length) mesh.uv = uvs;
		mesh.triangles = triangles;
		mesh.normals = Normals(vertices, triangles);
		mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
		return mesh;
	}

	/// <summary>
	/// Reads a <c>.dkmesh</c> file: plain text, one item per line: <c>v x y z u v</c> (a vertex with its UV on the game's 8x8 colour palette),
	/// <c>t a b c</c> (a triangle), <c>flag x y z yaw</c> (optional: where the building's flag stands, prefab space, yaw in degrees).
	/// Numbers use a dot as decimal separator.
	/// </summary>
	/// <remarks>
	/// The text is parsed once: the parsed arrays are kept in ModCore's disk cache (keyed by the file's size and change time), so later launches read
	/// one binary file instead of parsing thousands of numbers. Use <see cref="Prefetch"/> early to read them in the background.
	/// </remarks>
	public static Mesh LoadDkmesh(string path, out Vector3? flagPosition, out float flagYaw) {
		var fi = new FileInfo(path);
		string key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
		string cache = CachePath(path);
		var data = ReadCache(cache, key);
		if (data == null) {
			using (var reader = new StreamReader(path)) data = Parse(reader);
			WriteCache(cache, key, data);
		}
		flagPosition = data.Flag; flagYaw = data.FlagYaw;
		return Create(Path.GetFileNameWithoutExtension(path), data.V, data.UV, data.T, data.N);
	}

	/// <summary>
	/// The same format from any stream, e.g. a model embedded in your mod's DLL (<c>Assembly.GetManifestResourceStream</c>). With <paramref name="version"/>
	/// (your mod's version) the parsed result is cached like a file's; without it the text is parsed every time.
	/// </summary>
	public static Mesh LoadDkmesh(string name, Func<Stream> open, string version, out Vector3? flagPosition, out float flagYaw) {
		if (open == null) throw new ArgumentNullException(nameof(open));
		string cache = version != null ? CachePath("embedded:" + name) : null;
		string key = "embedded:" + name + "|" + version;
		var data = cache != null ? ReadCache(cache, key) : null;
		if (data == null) {
			using (var reader = new StreamReader(open())) data = Parse(reader);
			if (cache != null) WriteCache(cache, key, data);
		}
		flagPosition = data.Flag; flagYaw = data.FlagYaw;
		return Create(name, data.V, data.UV, data.T, data.N);
	}

	/// <summary>Starts reading the cached results of these .dkmesh files in the background (call early, e.g. in your plugin's Load).</summary>
	public static void Prefetch(params string[] dkmeshPaths) { foreach (var p in dkmeshPaths) DiskCache.Prefetch(CachePath(p)); }

	/// <summary>A mesh with normals you already have (skips the normal calculation).</summary>
	public static Mesh Create(string name, Vector3[] vertices, Vector2[] uvs, int[] triangles, Vector3[] normals) {
		if (normals == null || normals.Length != vertices.Length) return Create(name, vertices, uvs, triangles);
		var mesh = new Mesh { name = name ?? "mod-mesh" };
		mesh.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
		mesh.vertices = vertices;
		if (uvs != null && uvs.Length == vertices.Length) mesh.uv = uvs;
		mesh.triangles = triangles;
		mesh.normals = normals;
		mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
		return mesh;
	}

	private sealed class Parsed { public Vector3[] V, N; public Vector2[] UV; public int[] T; public Vector3? Flag; public float FlagYaw; }

	private static string CachePath(string source) {
		using var sha = System.Security.Cryptography.SHA1.Create();
		var id = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(source.StartsWith("embedded:") ? "x" : source) + source))).Substring(0, 16);
		return DiskCache.PathFor("meshes", Path.GetFileNameWithoutExtension(source.Replace(':', '_')) + "-" + id + ".bin");
	}

	private const int CacheVersion = 1;

	private static Parsed ReadCache(string path, string key) {
		if (!File.Exists(path)) return null;
		try {
			using var r = new BinaryReader(new MemoryStream(DiskCache.ReadAllBytes(path)));
			if (r.ReadInt32() != CacheVersion || r.ReadString() != key) return null;
			var d = new Parsed();
			int n = r.ReadInt32();
			d.V = new Vector3[n]; d.N = new Vector3[n]; d.UV = new Vector2[n];
			for (int i = 0; i < n; i++) { d.V[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); d.N[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); d.UV[i] = new Vector2(r.ReadSingle(), r.ReadSingle()); }
			d.T = new int[r.ReadInt32()];
			for (int i = 0; i < d.T.Length; i++) d.T[i] = r.ReadInt32();
			if (r.ReadBoolean()) d.Flag = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
			d.FlagYaw = r.ReadSingle();
			return d;
		}
		catch (Exception e) { CoreLog.Warn("mesh cache " + path + " unreadable, parsing again: " + e.Message); return null; }
	}

	private static void WriteCache(string path, string key, Parsed d) {
		try {
			using var ms = new MemoryStream();
			using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) {
				w.Write(CacheVersion); w.Write(key); w.Write(d.V.Length);
				for (int i = 0; i < d.V.Length; i++) { w.Write(d.V[i].x); w.Write(d.V[i].y); w.Write(d.V[i].z); w.Write(d.N[i].x); w.Write(d.N[i].y); w.Write(d.N[i].z); w.Write(d.UV[i].x); w.Write(d.UV[i].y); }
				w.Write(d.T.Length); foreach (var x in d.T) w.Write(x);
				w.Write(d.Flag.HasValue); if (d.Flag is Vector3 f) { w.Write(f.x); w.Write(f.y); w.Write(f.z); }
				w.Write(d.FlagYaw);
			}
			File.WriteAllBytes(path, ms.ToArray());
		}
		catch (Exception e) { CoreLog.Warn("mesh cache " + path + " could not be written: " + e.Message); }
	}

	private static Parsed Parse(TextReader reader) {
		var inv = CultureInfo.InvariantCulture;
		var v = new List<Vector3>();
		var uv = new List<Vector2>();
		var t = new List<int>();
		Vector3? flagPosition = null;
		float flagYaw = 0f;
		string line;
		while ((line = reader.ReadLine()) != null) {
			var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (p.Length == 0) continue;
			switch (p[0]) {
				case "v" when p.Length >= 6:
					v.Add(new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
					uv.Add(new Vector2(float.Parse(p[4], inv), float.Parse(p[5], inv)));
					break;
				case "t" when p.Length >= 4:
					t.Add(int.Parse(p[1], inv)); t.Add(int.Parse(p[2], inv)); t.Add(int.Parse(p[3], inv));
					break;
				case "flag" when p.Length >= 5:
					flagPosition = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv));
					flagYaw = float.Parse(p[4], inv);
					break;
			}
		}
		var va = v.ToArray(); var ta = t.ToArray();
		return new Parsed { V = va, UV = uv.ToArray(), T = ta, N = Normals(va, ta), Flag = flagPosition, FlagYaw = flagYaw };
	}

	/// <summary>An empty mesh (used to blank out parts of a model).</summary>
	public static Mesh Empty {
		get {
			if (_empty == null) _empty = new Mesh { name = "mod-empty", hideFlags = HideFlags.DontUnloadUnusedAsset };
			return _empty;
		}
	}
	private static Mesh _empty;

	// Unity's struct helpers can be stripped too: plain float math only.
	private static Vector3[] Normals(Vector3[] v, int[] t) {
		var nx = new float[v.Length]; var ny = new float[v.Length]; var nz = new float[v.Length];
		for (int i = 0; i + 2 < t.Length; i += 3) {
			Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
			float ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, wx = c.x - a.x, wy = c.y - a.y, wz = c.z - a.z;
			float cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
			for (int k = 0; k < 3; k++) { int j = t[i + k]; nx[j] += cx; ny[j] += cy; nz[j] += cz; }
		}
		var normals = new Vector3[v.Length];
		for (int i = 0; i < normals.Length; i++) {
			float l = (float)Math.Sqrt(nx[i] * nx[i] + ny[i] * ny[i] + nz[i] * nz[i]);
			normals[i] = l > 0f ? new Vector3(nx[i] / l, ny[i] / l, nz[i] / l) : new Vector3(0f, 1f, 0f);
		}
		return normals;
	}
}
