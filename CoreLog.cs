using System.Collections.Generic;

namespace DiceModders.ModCore;

/// <summary>Null-safe logging helper (the library may be used before <see cref="Plugin"/> has set its logger).</summary>
internal static class CoreLog {
	private static readonly HashSet<string> Seen = new();

	public static void Info(string message)  => Plugin.Log?.LogInfo("[modcore] " + message);
	public static void Warn(string message)  => Plugin.Log?.LogWarning("[modcore] " + message);
	public static void Error(string message) => Plugin.Log?.LogError("[modcore] " + message);

	/// <summary>Verbose trace, written only when <c>[Debug] LogLobbyEvents</c> is on in the ModCore config.</summary>
	public static void Trace(string message) {
		if (Plugin.LogLobbyEvents != null && Plugin.LogLobbyEvents.Value) Info(message);
	}

	/// <summary>
	/// Measures a block: <c>using (CoreLog.Slow("island colours")) { ... }</c>. Anything that takes 15 ms or more (a visible hitch) is logged with its
	/// time, so freezes can be traced to the code that caused them. Costs one timer read when fast.
	/// </summary>
	public static SlowScope Slow(string what) => new(what);

	public readonly struct SlowScope : System.IDisposable {
		private readonly string _what;
		private readonly long _start;
		internal SlowScope(string what) { _what = what; _start = System.Diagnostics.Stopwatch.GetTimestamp(); }
		public void Dispose() {
			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - _start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (ms >= 15) Warn("slow: " + _what + " took " + (int)ms + " ms");
		}
	}

	/// <summary>The name of a delegate's method, for the slow log ("MyPlugin.OnGameDataLoaded").</summary>
	public static string NameOf(System.Delegate d) => d?.Method == null ? "?" : (d.Method.DeclaringType?.Name ?? "") + "." + d.Method.Name;

	/// <summary>Logs a message only the first time a key is seen (for things that would otherwise repeat).</summary>
	public static void Once(string key, string message) {
		if (Seen.Add(key)) Warn(message);
	}
}
