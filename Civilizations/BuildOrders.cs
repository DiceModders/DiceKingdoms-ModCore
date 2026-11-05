using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using GameAIPlay = DiceKingdoms.Game.AIPlay;
using GameCivilization = DiceKingdoms.Interface.Civilization;
using GameTechTree = DiceKingdoms.Game.TechTree;
using GameTechnologies = DiceKingdoms.Game.Technologies;
using GameTechnologyName = DiceKingdoms.Game.TechnologyName;
using GameStrategy = DiceKingdoms.Game.Player.AIState.AIStrategy;

namespace DiceModders.ModCore;

/// <summary>
/// AI build orders. The game reads them once per <c>AIPlay</c> (<c>InitBuildOrders</c>): the file <c>custom-build-orders.txt</c> in the game folder if it
/// exists, else its own text, cut into blocks by one regular expression. Every block is parsed with no error handling: one unknown civilization, strategy,
/// technology or building name throws, no order is kept, and every AI's <c>SampleBuildOrder</c> throws again (the AI stops playing). A civilization or a
/// strategy without any block throws too.
/// <para>So before the game parses, ModCore puts together the text it would read plus the orders mods added, checks every block the way the game will
/// parse it, leaves out the broken ones (one log line each), and lets the game parse the result. A registered civilization that has no usable order for a
/// strategy gets the orders of the civilization it looks like, with the research order rearranged to fit its own tech tree (each technology after one of its
/// prerequisites; technologies its tree lacks are left out). A built-in civilization whose tree a mod replaced gets the same rearrangement.</para>
/// </summary>
internal static class BuildOrders {
	// the game's own pattern (AIPlay.InitBuildOrders)
	internal static readonly Regex Block = new(@"\s*civilization=(\w+)\s+weight=(\d+)\s+strategy=(\w+)\s+name=(\w+)\s*\[\s*technologies\s*\[([^]]*)\]\s*buildings\s*\[([^]]*)\]\s*\]", RegexOptions.Compiled);
	private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

	internal sealed class Order {
		public int Civilization, Weight; public string Strategy, Name; public List<string> Technologies, Buildings; public string Source;
		public string ToText(int civ, IEnumerable<string> techs) =>
			$"civilization={civ} weight={Weight} strategy={Strategy} name={Name} [\n technologies [\n {string.Join(" ", techs)}\n ]\n buildings [\n {string.Join(" ", Buildings)}\n ]\n]\n\n";
	}

	private sealed class Added { public int Civilization; public LobbyMod Owner; public List<Order> Orders; }
	private static readonly List<Added> FromMods = new();

	/// <summary>The registered civilizations the last checked text gave orders to.</summary>
	internal static readonly HashSet<int> Loaded = new();

	private static string _builtInText;
	[ThreadStatic] private static string _realPath;

	public static IEnumerable<string> Split(string text) => Space.Split(text.Trim()).Where(s => s.Length > 0);

	/// <summary>Reads the blocks of a text; what does not even parse as names/numbers is reported through <paramref name="bad"/>.</summary>
	internal static List<Order> Parse(string text, string source, Action<string> bad) {
		var result = new List<Order>();
		foreach (Match m in Block.Matches(text ?? "")) {
			string where = source + " '" + m.Groups[4].Value + "'";
			if (!int.TryParse(m.Groups[2].Value, out int weight) || weight <= 0) { bad(where + ": weight must be a whole number above 0"); continue; }
			if (!Enum.TryParse(m.Groups[3].Value, out GameStrategy _) || int.TryParse(m.Groups[3].Value, out _)) { bad(where + ": unknown strategy " + m.Groups[3].Value + " (" + string.Join(", ", Enum.GetNames(typeof(GameStrategy))) + ")"); continue; }
			var techs = Split(m.Groups[5].Value).ToList();
			var unknown = techs.Where(t => !Enum.TryParse(t, out GameTechnologyName _) || int.TryParse(t, out _)).ToList();
			if (unknown.Count > 0) { bad(where + ": unknown technolog" + (unknown.Count == 1 ? "y " : "ies ") + string.Join(", ", unknown)); continue; }
			int civ = -1;
			if (Enum.TryParse(m.Groups[1].Value, out GameCivilization c)) civ = (int)c;
			result.Add(new Order { Civilization = civ, Weight = weight, Strategy = m.Groups[3].Value, Name = m.Groups[4].Value, Technologies = techs, Buildings = Split(m.Groups[6].Value).ToList(), Source = source });
		}
		return result;
	}

	public static int Add(int civilization, string text, LobbyMod owner) {
		string source = (owner?.Name ?? "a mod") + " build order";
		var orders = Parse(text, source, why => CoreLog.Warn("build order left out: " + why));
		if (orders.Count == 0) CoreLog.Warn(source + " for civilization " + civilization + ": no usable block (format: civilization=X weight=N strategy=MILITARY name=x [ technologies [ ... ] buildings [ ... ] ])");
		foreach (var o in orders) o.Civilization = civilization;
		FromMods.RemoveAll(a => a.Civilization == civilization && a.Owner == owner);
		if (orders.Count > 0) FromMods.Add(new Added { Civilization = civilization, Owner = owner, Orders = orders });
		ResetLoaded();
		return orders.Count;
	}

	public static void Remove(int civilization, LobbyMod owner) { if (FromMods.RemoveAll(a => a.Civilization == civilization && a.Owner == owner) > 0) ResetLoaded(); }

	/// <summary>The AI keeps the parsed orders; dropping them makes the next AI decision read them again with what is active now (called at match start).</summary>
	internal static void ResetLoaded() {
		try { foreach (var ai in UnityEngine.Object.FindObjectsOfType<GameAIPlay>()) if (ai != null) ai.buildOrders = null; }
		catch (Exception e) { CoreLog.Once("bo:reset", "build orders reset: " + e.Message); }
	}

	private static bool Needed(out GameTechTree[] builtInTrees) {
		builtInTrees = new GameTechTree[5];
		bool any = CivilizationRegistry.All.Count > 0 || FromMods.Count > 0;
		for (int i = 0; i < 5; i++) if (BuiltInChanges.TryGetTree(i, out var t)) { builtInTrees[i] = t; any = true; }
		return any;
	}

	private static string BuiltInText() {
		if (_builtInText != null) return _builtInText;
		foreach (var p in typeof(GameAIPlay).GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public))
			if (p.PropertyType == typeof(string) && p.GetValue(null) is string s && s.Contains("civilization=")) return _builtInText = s;
		return null;
	}

	/// <summary>Writes the checked text and points the game at it for this one call; when nothing is added and nothing is broken the game reads its own input untouched.</summary>
	internal static void Prepare(GameAIPlay ai) {
		using var slow = CoreLog.Slow("build orders");
		Loaded.Clear();
		string path = ai.customBuildOrdersPath;
		bool custom = !string.IsNullOrEmpty(path) && File.Exists(path);
		string baseText, baseName;
		if (custom) { baseText = File.ReadAllText(path); baseName = "custom-build-orders.txt"; }
		else {
			if (Utility.Steamworks.SteamworksInit.IsSteamAndDemo) return;
			var ui = UnityEngine.Object.FindObjectOfType<DiceKingdoms.Interface.Interface>();
			if (ui == null || ui.IsTraining) return;   // the demo and the tutorial use their own fixed text
			baseText = BuiltInText(); baseName = "the game's build orders";
			if (baseText == null) return;
		}

		var problems = new List<string>();
		var baseOrders = Parse(baseText, baseName, problems.Add);
		bool needed = Needed(out var replacedTrees);
		if (!needed && problems.Count == 0 && baseOrders.All(o => o.Civilization >= 0 && Valid(ai, o, problems))) return;   // nothing to add and nothing broken: the game reads its own input

		var technologies = UnityEngine.Resources.FindObjectsOfTypeAll<GameTechnologies>().FirstOrDefault();
		var sb = new StringBuilder();
		var byCiv = new Dictionary<int, List<Order>>();
		foreach (var o in baseOrders) {
			if (o.Civilization < 0) { problems.Add(o.Source + " '" + o.Name + "': unknown civilization"); continue; }
			if (!Valid(ai, o, problems)) continue;
			if (!byCiv.TryGetValue(o.Civilization, out var list)) byCiv[o.Civilization] = list = new List<Order>();
			list.Add(o);
			var tree = o.Civilization < 5 ? replacedTrees[o.Civilization] : null;
			sb.Append(o.ToText(o.Civilization, tree != null ? Arrange(o.Technologies, tree) : o.Technologies));
		}

		// mod orders for built-in civilizations: next to the game's
		foreach (var a in FromMods.Where(a => a.Civilization >= 0 && a.Civilization < 5)) {
			var tree = replacedTrees[a.Civilization] ?? technologies?.TechTree((GameCivilization)a.Civilization);
			foreach (var o in a.Orders) if (Valid(ai, o, problems)) sb.Append(o.ToText(a.Civilization, tree != null ? Arrange(o.Technologies, tree) : o.Technologies));
		}

		// registered civilizations: their own orders; per strategy without one, the look-alike's rearranged to their tree
		foreach (var civ in CivilizationRegistry.All) {
			CivilizationRegistry.TryGetTree(civ.Id, out var tree);
			tree ??= technologies?.TechTree((GameCivilization)civ.SkinsLike);
			var have = new HashSet<string>();
			foreach (var a in FromMods.Where(a => a.Civilization == civ.Id))
				foreach (var o in a.Orders)
					if (Valid(ai, o, problems)) { sb.Append(o.ToText(civ.Id, tree != null ? Arrange(o.Technologies, tree) : o.Technologies)); have.Add(o.Strategy); }
			if (byCiv.TryGetValue(civ.SkinsLike, out var like))
				foreach (var o in like) if (!have.Contains(o.Strategy)) { sb.Append(o.ToText(civ.Id, tree != null ? Arrange(o.Technologies, tree) : o.Technologies)); have.Add("look:" + o.Strategy); }
			if (have.Count > 0) Loaded.Add(civ.Id);
		}

		foreach (var p in problems.Distinct()) CoreLog.Once("bo:" + p, "build order left out: " + p);   // once per launch, not every match
		string combined = Tools.DiskCache.PathFor("ModCore", "build-orders.txt");
		File.WriteAllText(combined, sb.ToString());
		_realPath = path;
		ai.customBuildOrdersPath = combined;
	}

	internal static void Restore(GameAIPlay ai) {
		if (_realPath == null && ai.customBuildOrdersPath != Tools.DiskCache.PathFor("ModCore", "build-orders.txt")) return;
		ai.customBuildOrdersPath = _realPath;
		_realPath = null;
	}

	private static readonly Dictionary<string, bool> KnownBuildings = new();

	/// <summary>The buildings through the game's own name lookup (it throws for an unknown name).</summary>
	private static bool Valid(GameAIPlay ai, Order o, List<string> problems) {
		foreach (var b in o.Buildings) {
			if (!KnownBuildings.TryGetValue(b, out bool ok)) {
				try { ok = ai.ParseBuildingType(b) != null; } catch { ok = false; }
				KnownBuildings[b] = ok;
			}
			if (!ok) { problems.Add(o.Source + " '" + o.Name + "': unknown building " + b); return false; }
		}
		return true;
	}

	/// <summary>
	/// The order kept as far as the tree allows: technologies the tree lacks are dropped, the tree's technologies the order lacks are added at the end, and
	/// each one is moved after one of its prerequisites (the game's rule: no prerequisites, or at least one learnt).
	/// </summary>
	internal static List<string> Arrange(IEnumerable<string> wanted, GameTechTree tree) {
		var deps = new Dictionary<string, string[]>();
		var inTree = new List<string>();
		var byName = new Dictionary<GameTechnologyName, DiceKingdoms.Game.Technology>();
		foreach (var tech in GameBalance.Technologies() ?? Array.Empty<DiceKingdoms.Game.Technology>()) if (tech != null) byName[tech.name] = tech;
		foreach (var slot in tree.slots) {
			var name = slot?.technology;
			if (name == null || !byName.TryGetValue(name.Value, out var t)) continue;
			string n = name.Value.ToString();
			if (deps.ContainsKey(n)) continue;
			inTree.Add(n);
			deps[n] = (tree.GetDependencies(t) ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DiceKingdoms.Game.Technology>(0)).Where(d => d != null).Select(d => d.name.ToString()).ToArray();
		}
		var queue = wanted.Where(deps.ContainsKey).Distinct().ToList();
		queue.AddRange(inTree.Where(n => !queue.Contains(n)));
		var result = new List<string>();
		var learnt = new HashSet<string>();
		while (queue.Count > 0) {
			int i = queue.FindIndex(n => deps[n].Length == 0 || deps[n].Any(learnt.Contains));
			if (i < 0) break;   // the rest can never be learnt in this tree
			learnt.Add(queue[i]); result.Add(queue[i]); queue.RemoveAt(i);
		}
		return result;
	}
}

[HarmonyPatch(typeof(GameAIPlay), "InitBuildOrders")]
internal static class BuildOrdersHook {
	[HarmonyPrefix]
	public static void Prefix(GameAIPlay __instance) {
		try { BuildOrders.Prepare(__instance); }
		catch (Exception e) { CoreLog.Error("build orders: could not check them, the game reads its own: " + e); }
	}

	[HarmonyFinalizer]
	public static Exception Finalizer(GameAIPlay __instance, Exception __exception) {
		try { BuildOrders.Restore(__instance); } catch (Exception e) { CoreLog.Once("bo:restore", "build orders restore: " + e.Message); }
		if (__exception != null) CoreLog.Error("the game could not read its AI build orders (the AI cannot choose one): " + __exception.Message);
		return __exception;
	}
}

public static partial class ModCore {
	public static partial class Civilizations {
		/// <summary>
		/// AI build orders for a civilization (built-in 0..4 or registered), in the game's own format, any number of blocks:
		/// <code>civilization=ANY weight=2 strategy=MILITARY name=rush [
		///     technologies [ UNLOCK_STORAGE UNLOCK_ARCHERY_RANGE ... ]
		///     buildings [ house cot farm racks ... ]
		/// ]</code>
		/// The <c>civilization=</c> value is replaced by <paramref name="civilization"/>. Blocks are checked (weight above 0, known strategy, technology and
		/// building names) and broken ones are left out with a log line, so a mistake never stops the AI. Technologies are arranged to fit the civilization's
		/// tech tree. A registered civilization without an order for a strategy uses the orders of the civilization it looks like, rearranged the same way.
		/// Orders for a built-in civilization are added next to the game's. Calling again replaces your earlier orders for that civilization.
		/// </summary>
		/// <returns>How many blocks were accepted.</returns>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static int AddBuildOrders(int civilization, string text) {
			var owner = LobbyMods.Identify(System.Reflection.Assembly.GetCallingAssembly());
			Core.Ensure();
			return BuildOrders.Add(civilization, text, owner);
		}

		/// <summary>Removes the build orders your mod added for a civilization.</summary>
		[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
		public static void RemoveBuildOrders(int civilization) {
			var owner = LobbyMods.Identify(System.Reflection.Assembly.GetCallingAssembly());
			Core.Ensure();
			BuildOrders.Remove(civilization, owner);
		}
	}
}
