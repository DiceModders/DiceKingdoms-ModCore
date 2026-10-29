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
	public static Mesh LoadDkmesh(string path, out Vector3? flagPosition, out float flagYaw) {
		var inv = CultureInfo.InvariantCulture;
		var v = new List<Vector3>();
		var uv = new List<Vector2>();
		var t = new List<int>();
		flagPosition = null;
		flagYaw = 0f;
		foreach (var line in File.ReadLines(path)) {
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
		return Create(Path.GetFileNameWithoutExtension(path), v.ToArray(), uv.ToArray(), t.ToArray());
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
