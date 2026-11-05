using System;
using System.Collections.Generic;
using System.IO;

namespace DiceModders.ModCore.Tools;

/// <summary>
/// One folder for every mod's cache files: <c>BepInEx\cache\ModCore\&lt;owner&gt;\</c>. A file asked for with <see cref="PathFor"/> is marked as used in this
/// launch. Once per launch, after the game data has loaded (when mods build their caches), every file under the folder that no mod has asked for in
/// the last <see cref="KeepLaunches"/> launches is deleted, so a removed or disabled mod leaves nothing behind for long. A cache is only ever a copy of
/// something that can be rebuilt: deleting the folder (or a file) is always safe, the owner rebuilds it on the next launch.
/// </summary>
public static class DiskCache {
	/// <summary>Files not asked for in this many launches in a row are deleted.</summary>
	public const int KeepLaunches = 3;

	public static string Root => Path.Combine(BepInEx.Paths.CachePath, "ModCore");
	private static string IndexPath => Path.Combine(Root, "index.txt");

	private static readonly HashSet<string> Used = new(StringComparer.OrdinalIgnoreCase);
	private static Dictionary<string, int> _index;   // relative path -> last launch it was used
	private static int _launch = -1;                 // set by Prune

	/// <summary>The path of a cache file (the folder is created). <paramref name="owner"/> is your mod's short id, <paramref name="file"/> a file name.</summary>
	public static string PathFor(string owner, string file) {
		var dir = Path.Combine(Root, Clean(owner));
		Directory.CreateDirectory(dir);
		var path = Path.Combine(dir, Clean(file));
		if (Used.Add(path) && _launch >= 0) {   // asked for after this launch's clean-up: record it now
			try { LoadIndex(); _index[Relative(path)] = _launch; SaveIndex(); } catch (Exception e) { CoreLog.Once("diskcache:late", "cache index: " + e.Message); }
		}
		return path;
	}

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.Task<byte[]>> Reads = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Starts reading cache files on a background thread (call it early, e.g. in your plugin's Load: the game takes a few seconds to load its data,
	/// so on a hard disk the files are in memory by the time you need them). <see cref="ReadAllBytes"/> then returns the bytes without waiting.
	/// </summary>
	public static void Prefetch(params string[] paths) {
		foreach (var p in paths) {
			if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
			Reads.GetOrAdd(p, path => System.Threading.Tasks.Task.Run(() => {
				try { return File.ReadAllBytes(path); } catch { return null; }
			}));
		}
	}

	/// <summary>All files of one owner's cache folder (to prefetch them all).</summary>
	public static string[] FilesOf(string owner) {
		var dir = Path.Combine(Root, Clean(owner));
		return Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>();
	}

	/// <summary>The file's bytes: from a <see cref="Prefetch"/> if one was started (waits for it), otherwise read now.</summary>
	public static byte[] ReadAllBytes(string path) {
		if (Reads.TryRemove(path, out var task)) {
			var bytes = task.GetAwaiter().GetResult();
			if (bytes != null) return bytes;
		}
		return File.ReadAllBytes(path);
	}

	/// <summary>Once per launch (after GameDataLoaded): records the files used and deletes the ones unused for <see cref="KeepLaunches"/> launches.</summary>
	internal static void Prune() {
		if (_launch >= 0) return;
		try {
			if (!Directory.Exists(Root)) return;
			LoadIndex();
			int launch = 1;
			if (_index.TryGetValue("#launch", out int previous)) launch = previous + 1;
			_index["#launch"] = launch;
			_launch = launch;
			foreach (var p in Used) _index[Relative(p)] = launch;
			int deleted = 0;
			foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) {
				if (string.Equals(file, IndexPath, StringComparison.OrdinalIgnoreCase)) continue;
				var rel = Relative(file);
				if (!_index.TryGetValue(rel, out int last)) { _index[rel] = launch; continue; }   // unknown file: give it the full grace period
				if (launch - last < KeepLaunches) continue;
				try { File.Delete(file); _index.Remove(rel); deleted++; } catch { }
			}
			foreach (var dir in Directory.GetDirectories(Root)) { try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { } }
			var gone = new List<string>();
			foreach (var k in _index.Keys) if (k != "#launch" && !File.Exists(Path.Combine(Root, k))) gone.Add(k);
			foreach (var k in gone) _index.Remove(k);
			SaveIndex();
			if (deleted > 0) CoreLog.Info("cache: removed " + deleted + " file(s) no mod has used in " + KeepLaunches + " launches");
		}
		catch (Exception e) { CoreLog.Warn("cache clean-up skipped: " + e.Message); }
	}

	private static void LoadIndex() {
		if (_index != null) return;
		_index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(IndexPath)) return;
		foreach (var line in File.ReadAllLines(IndexPath)) {
			int tab = line.IndexOf('\t');
			if (tab > 0 && int.TryParse(line.Substring(0, tab), out int n)) _index[line.Substring(tab + 1)] = n;
		}
	}

	private static void SaveIndex() {
		var lines = new List<string>();
		foreach (var kv in _index) lines.Add(kv.Value + "\t" + kv.Key);
		Directory.CreateDirectory(Root);
		File.WriteAllLines(IndexPath, lines);
	}

	private static string Relative(string path) => Path.GetRelativePath(Root, path);

	private static string Clean(string name) {
		if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("empty cache name");
		foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
		return name;
	}
}
