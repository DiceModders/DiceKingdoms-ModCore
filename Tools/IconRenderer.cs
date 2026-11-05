using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using GamePlayerColor = DiceKingdoms.Game.PlayerColor;
using GamePlayerColors = DiceKingdoms.Game.PlayerColors;

namespace DiceModders.ModCore.Tools;

/// <summary>How a model is photographed by <see cref="IconRenderer"/>.</summary>
public sealed class IconView {
	/// <summary>Camera angle down from the horizon and around the model, in degrees.</summary>
	public float Pitch = 22f, Yaw = 45f;
	/// <summary>Empty border around the model, as a part of the picture (0.06 = 6 %).</summary>
	public float Padding = 0.06f;
	/// <summary>
	/// Fixed half height of the view in model units. Null fits the model into the picture; a fixed value keeps every model at the same scale (units of
	/// different heights, hats and weapons sticking out) and stands them on <see cref="Ground"/>.
	/// </summary>
	public float? OrthoSize;
	/// <summary>With a fixed <see cref="OrthoSize"/>: where the model's feet (its lowest point) stand, as a part of the picture height from the bottom.</summary>
	public float Ground = 0.04f;

	/// <summary>The angle of the game's own building pictures (fitted against them: pitch 22, yaw 45).</summary>
	public static IconView Building => new();
	/// <summary>Units seen almost from the front, all at one scale.</summary>
	public static IconView Unit => new() { Pitch = 8f, Yaw = 200f, OrthoSize = 0.62f };

	internal string Key => FormattableString.Invariant($"{Pitch}/{Yaw}/{Padding}/{OrthoSize}/{Ground}");
}

/// <summary>
/// Takes pictures of models inside the game, so a civilization does not need pictures made outside the game: one picture per player colour, with a transparent
/// background and soft edges. The game paints a model in a player's colour with <c>ColoredPaperUtils.SlowSetPlayerColor</c>, which is used here as well, so
/// the pictures match what players see in the match.
/// <para>Rendering is spread over frames (one player colour per frame) after the game data has loaded, and the result is kept in the
/// <see cref="TextureCache"/>: it is rendered once, later launches read one file. The cache is renewed when the model, the view, the size or the game's version changes.</para>
/// </summary>
public static class IconRenderer {
	private const int RendererVersion = 1;
	private const int Layer = 31;                       // a layer the game does not draw: the camera sees only the model
	private static readonly Vector3 Stage = new(0f, -5000f, 0f);

	private sealed class Job {
		public string Name, Key; public GameObject Prefab; public IconView View; public int Width, Height, Colours; public Action<Sprite[]> Done;
		public GameObject Instance; public int Next; public byte[] Page; public int CellW, CellH, PageW, PageH;
	}

	private static readonly Queue<Job> Jobs = new();
	private static Job _current;
	private static Camera _camera;
	private static HDAdditionalCameraData _hd;
	private static Light _light;
	private static RenderTexture _rt;
	private static Texture2D _read;
	private static IconRenderDriver _driver;

	/// <summary>
	/// Pictures of <paramref name="prefab"/> in every player colour (12, the game's colour order), <paramref name="width"/> x <paramref name="height"/> pixels.
	/// <paramref name="done"/> gets them on the main thread: at once from the cache, otherwise when rendered (a few frames after the game data has loaded).
	/// The prefab is not changed; a copy is photographed.
	/// </summary>
	/// <param name="name">Unique, stable name of the pictures (it names the cache file), e.g. "author.civ castle".</param>
	public static void PlayerColours(string name, GameObject prefab, IconView view, int width, int height, Action<Sprite[]> done) {
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
		if (prefab == null) throw new ArgumentNullException(nameof(prefab));
		if (done == null) throw new ArgumentNullException(nameof(done));
		view ??= IconView.Building;
		var job = new Job { Name = name, Prefab = prefab, View = view, Width = Math.Max(8, width), Height = Math.Max(8, height), Done = done };
		job.CellW = (job.Width + 3) / 4 * 4; job.CellH = (job.Height + 3) / 4 * 4;
		job.Colours = 12;
		job.PageW = job.CellW * 4; job.PageH = job.CellH * 3;
		job.Key = KeyOf(job);

		var cached = TextureCache.Load("icons " + name, job.Key);
		if (cached != null && cached.Length == 1) { Finish(job, cached[0]); return; }

		Jobs.Enqueue(job);
		ModCore.Civilizations.WhenGameDataLoaded(Wake);
	}

	/// <summary>Starts reading cached pictures in the background (call early, e.g. in your plugin's Load).</summary>
	public static void Prefetch(params string[] names) { foreach (var n in names) TextureCache.Prefetch("icons " + n); }

	private static string KeyOf(Job job) {
		var sb = new StringBuilder();
		sb.Append(RendererVersion).Append('|').Append(Application.version).Append('|').Append(job.View.Key).Append('|').Append(job.Width).Append('x').Append(job.Height);
		foreach (var mf in job.Prefab.GetComponentsInChildren<MeshFilter>(true))
			sb.Append('|').Append(mf.gameObject.name).Append(':').Append(mf.sharedMesh != null ? mf.sharedMesh.name + "/" + mf.sharedMesh.vertexCount : "-");
		foreach (var sm in job.Prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
			sb.Append('|').Append(sm.gameObject.name).Append(':').Append(sm.sharedMesh != null ? sm.sharedMesh.name + "/" + sm.sharedMesh.vertexCount : "-");
		return sb.ToString();
	}

	private static void Wake() {
		if (Jobs.Count == 0 && _current == null) return;
		if (_driver == null) {
			if (!ClassInjector.IsTypeRegisteredInIl2Cpp<IconRenderDriver>()) ClassInjector.RegisterTypeInIl2Cpp<IconRenderDriver>();
			var go = new GameObject("ModCore icon renderer") { hideFlags = HideFlags.HideAndDontSave };
			UnityEngine.Object.DontDestroyOnLoad(go);
			_driver = go.AddComponent<IconRenderDriver>();
		}
		_driver.enabled = true;
	}

	/// <summary>One step per frame: set up the next job or render one player colour. The driver switches itself off when the queue is empty.</summary>
	internal static void Step() {
		try {
			if (_current == null) {
				if (Jobs.Count == 0) { _driver.enabled = false; Release(); return; }
				_current = Jobs.Dequeue();
				if (!Begin(_current)) { _current = null; return; }
			}
			using (CoreLog.Slow("icon render " + _current.Name)) RenderColour(_current, _current.Next++);
			if (_current.Next >= _current.Colours) { End(_current); _current = null; }
		}
		catch (Exception e) {
			CoreLog.Error("icon renderer: " + (_current?.Name ?? "") + " failed: " + e);
			if (_current?.Instance != null) UnityEngine.Object.Destroy(_current.Instance);
			_current = null;
		}
	}

	private static bool Begin(Job job) {
		if (job.Prefab == null) { CoreLog.Warn("icon renderer: the model of '" + job.Name + "' is gone"); return false; }
		EnsureCamera(job);
		var inst = UnityEngine.Object.Instantiate(job.Prefab);
		inst.hideFlags = HideFlags.HideAndDontSave;
		// a picture needs only what is drawn: the game's scripts would try to join a match
		// (the colour component stays: it knows which materials take the player colour, set in the prefab)
		foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
			if (mb.TryCast<DiceKingdoms.Interface.ColoredPaperOverrides>() == null) UnityEngine.Object.DestroyImmediate(mb);
		foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
		foreach (var pr in inst.GetComponentsInChildren<ParticleSystemRenderer>(true)) pr.enabled = false;   // smoke and fire are not part of a picture
		inst.transform.position = Stage;
		inst.transform.rotation = Quaternion.identity;
		inst.SetActive(true);
		foreach (var a in inst.GetComponentsInChildren<Animator>(true)) { try { a.Update(0f); } catch { } }   // the first frame of the default animation instead of the bind pose
		job.Instance = inst;
		job.Page = new byte[job.PageW * job.PageH * 4];
		Frame(job);
		if (job.View.OrthoSize == null) Refit(job);
		return true;
	}

	private static void Frame(Job job) {
		var rot = Quaternion.Euler(job.View.Pitch, job.View.Yaw, 0f);
		_camera.transform.rotation = rot;
		var bounds = BoundsOf(job.Instance);
		Vector3 right = rot * Vector3.right, up = rot * Vector3.up, fwd = rot * Vector3.forward;
		// the model's extent on the picture plane
		float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
		var c = bounds.center; var e = bounds.extents;
		for (int i = 0; i < 8; i++) {
			var p = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
			float x = Vector3.Dot(p, right), y = Vector3.Dot(p, up);
			minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
		}
		float aspect = (float)job.Width / job.Height;
		float half, cx = (minX + maxX) / 2f, cy;
		if (job.View.OrthoSize is float fixedHalf) {
			half = fixedHalf;
			cy = minY + half * (1f - 2f * job.View.Ground);   // the lowest point at Ground from the bottom
		}
		else {
			half = Math.Max((maxY - minY) / 2f, (maxX - minX) / 2f / aspect) / Math.Max(0.05f, 1f - 2f * job.View.Padding);
			cy = (minY + maxY) / 2f;
		}
		_camera.orthographic = true;
		_camera.orthographicSize = half;
		_camera.aspect = aspect;
		float depth = e.magnitude * 4f + 1f;
		_camera.transform.position = right * cx + up * cy + fwd * (Vector3.Dot(c, fwd) - depth / 2f);
		_camera.nearClipPlane = 0.01f; _camera.farClipPlane = depth;
	}

	/// <summary>
	/// The bounding box of a rotated model is wider than the model, so the first framing leaves empty space. One test picture shows where the model really is;
	/// the camera is then moved and zoomed so it fills the picture (minus the padding).
	/// </summary>
	private static void Refit(Job job) {
		var black = Shot(Color.black); var white = Shot(Color.white);
		int w = _rt.width, h = _rt.height, x0 = w, y0 = h, x1 = -1, y1 = -1;
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++) {
				int i = (y * w + x) * 4;
				if ((white[i] - black[i]) + (white[i + 1] - black[i + 1]) + (white[i + 2] - black[i + 2]) > 3 * 250) continue;   // background
				if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
			}
		if (x1 < 0) { CoreLog.Warn("icon renderer: '" + job.Name + "' is not visible from this view"); return; }
		float half = _camera.orthographicSize, unit = 2f * half / h;   // world size of one pixel
		float cx = ((x0 + x1 + 1) / 2f - w / 2f) * unit, cy = ((y0 + y1 + 1) / 2f - h / 2f) * unit;
		float needH = (y1 - y0 + 1) * unit / 2f, needW = (x1 - x0 + 1) * unit / 2f / _camera.aspect;
		var t = _camera.transform;
		t.position += t.right * cx + t.up * cy;
		_camera.orthographicSize = Math.Max(needH, needW) / Math.Max(0.05f, 1f - 2f * job.View.Padding);
	}

	private static Bounds BoundsOf(GameObject go) {
		bool any = false; var b = new Bounds(go.transform.position, Vector3.zero);
		foreach (var r in go.GetComponentsInChildren<Renderer>(false)) {   // drawn parts only: hidden ones report a box at the origin
			if (!r.enabled || r is ParticleSystemRenderer) continue;
			if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
		}
		return b;
	}

	private static void EnsureCamera(Job job) {
		int w = job.Width * 2, h = job.Height * 2;   // twice the size, averaged down: smooth edges
		if (_rt == null || _rt.width != w || _rt.height != h) {
			if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); }
			_rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "ModCore icons", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1 };
			_rt.Create();
			if (_read != null) UnityEngine.Object.Destroy(_read);
			_read = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
		}
		if (_camera != null) return;
		var go = new GameObject("ModCore icon camera") { hideFlags = HideFlags.HideAndDontSave };
		UnityEngine.Object.DontDestroyOnLoad(go);
		_camera = go.AddComponent<Camera>();
		_camera.enabled = false;   // renders only when asked
		_camera.cullingMask = 1 << Layer;
		_camera.clearFlags = CameraClearFlags.SolidColor;
		_camera.allowHDR = false; _camera.allowMSAA = false;
		_hd = go.AddComponent<HDAdditionalCameraData>();
		_hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
		_hd.volumeLayerMask = 0;   // the scene's volumes (fog, colour grading) stay out
		_hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;

		var lightGo = new GameObject("ModCore icon light") { hideFlags = HideFlags.HideAndDontSave };
		UnityEngine.Object.DontDestroyOnLoad(lightGo);
		lightGo.transform.rotation = Quaternion.Euler(50f, 150f, 0f);
		_light = lightGo.AddComponent<Light>();
		_light.type = LightType.Directional;
		_light.enabled = false;   // on only while a picture is taken
		try { var hl = lightGo.AddComponent<HDAdditionalLightData>(); hl.intensity = 3f; } catch (Exception e) { CoreLog.Once("icons:light", "icon renderer light: " + e.Message); }
	}

	private static unsafe void RenderColour(Job job, int colour) {
		var colours = PlayerColourList();
		// the game's own colouring, per part: each player colour is a copy of the palette with that player's cell
		if (colours != null && colour < colours.Length && colours[colour]?.CustomColorPalette != null)
			foreach (var r in job.Instance.GetComponentsInChildren<Renderer>(true))
				if (r is not ParticleSystemRenderer) DiceKingdoms.Interface.ColoredPaperOverrides.GetOrCreateComponent(r)?.SetColorPalette(colours[colour].CustomColorPalette);

		var black = Shot(Color.black);
		var white = Shot(Color.white);

		// alpha from the difference of the two backgrounds; colour from the black one, un-premultiplied; 2x2 average down
		int w = _rt.width, h = _rt.height;
		int cellX = (colour % 4) * job.CellW, cellY = (2 - colour / 4) * job.CellH;   // first row at the top of the page
		int ox = (job.CellW - job.Width) / 2, oy = (job.CellH - job.Height) / 2;
		for (int y = 0; y < job.Height; y++)
			for (int x = 0; x < job.Width; x++) {
				float r = 0, g = 0, b = 0, a = 0;
				for (int dy = 0; dy < 2; dy++)
					for (int dx = 0; dx < 2; dx++) {
						int i = ((y * 2 + dy) * w + x * 2 + dx) * 4;
						float pa = 1f - ((white[i] - black[i]) + (white[i + 1] - black[i + 1]) + (white[i + 2] - black[i + 2])) / (3f * 255f);
						pa = Mathf.Clamp01(pa);
						r += black[i]; g += black[i + 1]; b += black[i + 2]; a += pa;   // premultiplied sums
					}
				int o = ((cellY + oy + y) * job.PageW + cellX + ox + x) * 4;
				if (a <= 0.001f) { job.Page[o] = job.Page[o + 1] = job.Page[o + 2] = job.Page[o + 3] = 0; continue; }
				job.Page[o] = (byte)Math.Min(255f, r / a);
				job.Page[o + 1] = (byte)Math.Min(255f, g / a);
				job.Page[o + 2] = (byte)Math.Min(255f, b / a);
				job.Page[o + 3] = (byte)Math.Min(255f, a / 4f * 255f + 0.5f);
			}
	}

	private static byte[] Shot(Color background) {
		_hd.backgroundColorHDR = background;
		_camera.backgroundColor = background;
		_camera.targetTexture = _rt;
		_light.enabled = true;
		try { _camera.Render(); }
		finally { _light.enabled = false; }
		var previous = RenderTexture.active;
		RenderTexture.active = _rt;
		_read.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0, false);
		RenderTexture.active = previous;
		_camera.targetTexture = null;
		var raw = _read.GetRawTextureData();
		var bytes = new byte[raw.Length];
		unsafe { fixed (byte* d = bytes) Buffer.MemoryCopy((byte*)raw.Pointer + 4 * IntPtr.Size, d, bytes.Length, bytes.Length); }
		GC.KeepAlive(raw);
		return bytes;
	}

	private static GamePlayerColor[] _colours;
	private static GamePlayerColor[] PlayerColourList() {
		if (_colours != null) return _colours;
		foreach (var pc in Resources.FindObjectsOfTypeAll<GamePlayerColors>()) {
			if (pc == null) continue;
			if (pc.Colors == null) pc.Init();
			if (pc.Colors != null) return _colours = pc.Colors;
		}
		CoreLog.Once("icons:colours", "icon renderer: the game's player colours are not loaded; pictures keep the model's own colours");
		return null;
	}

	private static unsafe void End(Job job) {
		UnityEngine.Object.Destroy(job.Instance);
		job.Instance = null;
		var page = new Texture2D(job.PageW, job.PageH, TextureFormat.RGBA32, true) { name = "icons " + job.Name, hideFlags = HideFlags.DontUnloadUnusedAsset };
		fixed (byte* p = job.Page) TextureCache.BleedColour(p, job.PageW, job.PageH, 8);
		var pixels = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(job.PageW * job.PageH);
		for (int i = 0, n = job.PageW * job.PageH; i < n; i++)
			pixels[i] = new Color32(job.Page[i * 4], job.Page[i * 4 + 1], job.Page[i * 4 + 2], job.Page[i * 4 + 3]);
		page.SetPixels32(pixels);   // level 0; Apply builds the smaller levels
		job.Page = null;
		page.Apply(true, false);
		page.filterMode = FilterMode.Trilinear; page.wrapMode = TextureWrapMode.Clamp;
		if (Plugin.SaveIconPictures?.Value == true) {
			try {
				var dir = System.IO.Path.Combine(DiskCache.Root, "icon-pictures");
				System.IO.Directory.CreateDirectory(dir);
				System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, string.Join("_", job.Name.Split(System.IO.Path.GetInvalidFileNameChars())) + ".png"), (byte[])ImageConversion.EncodeToPNG(page));
			}
			catch (Exception e) { CoreLog.Once("icons:png", "icon renderer: could not save the picture sheet: " + e.Message); }
		}
		TextureCache.Save("icons " + job.Name, job.Key, new[] { page });
		CoreLog.Info("icon renderer: '" + job.Name + "' rendered (" + job.Colours + " colours, " + job.Width + "x" + job.Height + ")");
		Finish(job, page);
	}

	private static void Finish(Job job, Texture2D page) {
		var sprites = new Sprite[job.Colours];
		for (int i = 0; i < job.Colours; i++) {
			var rect = new Rect((i % 4) * job.CellW + (job.CellW - job.Width) / 2, (2 - i / 4) * job.CellH + (job.CellH - job.Height) / 2, job.Width, job.Height);
			var s = Sprite.Create(page, rect, new Vector2(0.5f, 0.5f), 100f);
			s.name = job.Name + " " + i;
			s.hideFlags = HideFlags.DontUnloadUnusedAsset;
			sprites[i] = s;
		}
		try { job.Done(sprites); }
		catch (Exception e) { CoreLog.Error("icon renderer: handler of '" + job.Name + "' failed: " + e); }
	}

	private static void Release() {
		if (_rt != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt = null; }
		if (_read != null) { UnityEngine.Object.Destroy(_read); _read = null; }
	}
}

/// <summary>Calls <see cref="IconRenderer.Step"/> once per frame, only while pictures are waiting (it is disabled otherwise).</summary>
internal sealed class IconRenderDriver : MonoBehaviour {
	public IconRenderDriver(IntPtr ptr) : base(ptr) { }
	public void Update() => IconRenderer.Step();
}
