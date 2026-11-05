using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Steamworks;
using SteamLobby = Steamworks.Data.Lobby;
using SteamFriend = Steamworks.Friend;

namespace DiceModders.ModCore;

/// <summary>
/// The Steam lobby, as the game's own Facepunch.Steamworks sees it. State comes from <c>SteamMatchmaking</c>'s static events (the game's own subscriptions
/// stay untouched; ours are added next to them), subscribed once when the game starts Steam (<c>SteamClient.Init</c>, see <see cref="LobbyHooks"/>).
/// Steam raises no event when the local player leaves, so the game's only way out, <c>Lobby.Leave</c>, is hooked; an owner change arrives as a lobby
/// data update (Steam sends one when the owner changes) or with the old owner's leave event. Nothing is polled.
/// </summary>
internal static class LobbyState {
	private const string ModsKey = "modcore.mods";
	private const string CivsKey = "modcore.civs";

	private static SteamLobby? _lobby;
	private static ulong _owner;
	private static LobbyMember[] _members = Array.Empty<LobbyMember>();
	private static readonly Dictionary<ulong, HashSet<string>> MemberKeys = new();
	private static readonly Dictionary<ulong, HashSet<string>> MemberCivs = new();
	private static readonly List<object> Keep = new();   // the IL2CPP delegates must outlive the subscription (the whole session)
	private static bool _subscribed;

	public static bool MatchRunning { get; private set; }
	public static bool InLobby => _lobby.HasValue;
	public static SteamLobby? Current => _lobby;
	public static IReadOnlyList<LobbyMember> Members => _members;
	public static bool IsHost { get { ulong me = LocalSteamId; return _lobby.HasValue && me != 0 && _owner == me; } }

	public static ulong LocalSteamId {
		get {
			try { return SteamClient.IsValid ? SteamClient.SteamId.Value : 0UL; }
			catch { return 0UL; }   // Steam is not running / the interface is not up
		}
	}

	public static string GetLobbyData(string key) {
		try { return _lobby.HasValue ? _lobby.Value.GetData(key) : null; }
		catch (Exception e) { CoreLog.Once("lobby:getdata", "lobby: reading lobby data failed: " + e.Message); return null; }
	}

	public static bool SetLobbyData(string key, string value) {
		try { return _lobby.HasValue && _lobby.Value.SetData(key, value); }
		catch (Exception e) { CoreLog.Once("lobby:setdata", "lobby: writing lobby data failed: " + e.Message); return false; }
	}

	public static bool MemberHasKey(ulong steamId, string tag) => MemberKeys.TryGetValue(steamId, out var keys) && keys.Contains(tag);

	/// <summary>The version of the mod a member published this rule key with, or null when they do not have it.</summary>
	public static string MemberVersionOf(ulong steamId, string key) {
		if (!MemberKeys.TryGetValue(steamId, out var keys)) return null;
		foreach (var k in keys) if (k.StartsWith(key + "@", StringComparison.Ordinal)) return k.Substring(key.Length + 1);
		return null;
	}

	/// <summary>True when every other member published the same civilization with the same number (a client without it would not know what the number means).</summary>
	public static bool EveryoneHasCivilization(ModCivilization civ) {
		if (!_lobby.HasValue) return true;
		string clean = CivTag(civ);
		ulong me = LocalSteamId;
		foreach (var m in _members) {
			if (m.SteamId == me) continue;
			if (!(MemberCivs.TryGetValue(m.SteamId, out var set) && set.Contains(clean))) return false;
		}
		return true;
	}

	private static string CleanCivKey(string key) => (key ?? "").Replace(',', '_').Replace(';', '_').Replace('@', '_');
	/// <summary>"key=id@version": the same civilization, the same number and the same mod version (another version may play differently).</summary>
	private static string CivTag(ModCivilization c) => CleanCivKey(c.Key) + "=" + c.Id + "@" + CleanCivKey(c.ModVersion);

	// ------------------------------------------------------------------ subscription

	/// <summary>The game started Steam (or it was already running when ModCore set up): subscribe to the lobby events once.</summary>
	public static void SteamReady() {
		if (_subscribed) return;
		bool up;
		try { up = SteamClient.IsValid; }
		catch { up = false; }
		if (up) TrySubscribe();
	}

	/// <summary>The game called <c>Lobby.Leave</c> (its only way out of a lobby; Steam sends no event for it).</summary>
	public static void LeftByGame(ulong lobbyId) {
		if (_lobby.HasValue && (lobbyId == 0 || _lobby.Value.Id.Value == lobbyId)) Leave();
	}

	/// <summary>Steam announces an owner change with a lobby data update; the members and rules follow the new host.</summary>
	private static void CheckOwner(SteamLobby lobby) {
		ulong owner = lobby.Owner.Id.Value;
		if (owner == _owner) return;
		CoreLog.Trace("lobby owner changed");
		Reload();
		LobbyRuleRegistry.ReevaluateAll();
		ModCore.Lobby.RaiseChanged();
	}

	private static int _nextStep;

	private static void TrySubscribe() {
		// each subscription is done exactly once; a failed step is retried on the next SteamReady without repeating the ones before it
		Action[] steps = {
			() => SteamMatchmaking.add_OnLobbyEntered(Wrap<Il2CppSystem.Action<SteamLobby>>(new Action<SteamLobby>(OnEntered))),
			() => SteamMatchmaking.add_OnLobbyDataChanged(Wrap<Il2CppSystem.Action<SteamLobby>>(new Action<SteamLobby>(OnDataChanged))),
			() => SteamMatchmaking.add_OnLobbyMemberDataChanged(Wrap<Il2CppSystem.Action<SteamLobby, SteamFriend>>(new Action<SteamLobby, SteamFriend>(OnMemberDataChanged))),
			() => SteamMatchmaking.add_OnLobbyMemberJoined(Wrap<Il2CppSystem.Action<SteamLobby, SteamFriend>>(new Action<SteamLobby, SteamFriend>((l, f) => OnMemberChange(l, f, false)))),
			() => SteamMatchmaking.add_OnLobbyMemberLeave(Wrap<Il2CppSystem.Action<SteamLobby, SteamFriend>>(new Action<SteamLobby, SteamFriend>((l, f) => OnMemberChange(l, f, true)))),
			() => SteamMatchmaking.add_OnLobbyMemberDisconnected(Wrap<Il2CppSystem.Action<SteamLobby, SteamFriend>>(new Action<SteamLobby, SteamFriend>((l, f) => OnMemberChange(l, f, true)))),
			() => SteamMatchmaking.add_OnLobbyMemberKicked(Wrap<Il2CppSystem.Action<SteamLobby, SteamFriend, SteamFriend>>(new Action<SteamLobby, SteamFriend, SteamFriend>((l, f, admin) => OnMemberChange(l, f, true)))),
			() => SteamMatchmaking.add_OnLobbyMemberBanned(Wrap<Il2CppSystem.Action<SteamLobby, SteamFriend, SteamFriend>>(new Action<SteamLobby, SteamFriend, SteamFriend>((l, f, admin) => OnMemberChange(l, f, true)))),
		};
		try {
			for (; _nextStep < steps.Length; _nextStep++) steps[_nextStep]();
			_subscribed = true;
			CoreLog.Info("lobby: listening to the game's Steam lobby events");
		}
		catch (Exception e) { CoreLog.Once("lobby:subscribe", $"lobby: could not subscribe to Steam lobby event {_nextStep + 1}/{steps.Length}: {e}"); }
	}

	private static T Wrap<T>(Delegate managed) where T : Il2CppSystem.Delegate {
		var native = DelegateSupport.ConvertDelegate<T>(managed);
		Keep.Add(managed);
		Keep.Add(native);
		return native;
	}

	// ------------------------------------------------------------------ events

	private static bool IsCurrent(SteamLobby lobby) => _lobby.HasValue && _lobby.Value.Id.Value == lobby.Id.Value;

	private static int _dataEvents;   // lobby/member data events since the last summary line (they are far too frequent to log one by one)

	private static void TraceDataEvents(string when) {
		if (_dataEvents > 0) CoreLog.Trace($"{_dataEvents} lobby/member data event(s) until {when}");
		_dataEvents = 0;
	}

	private static void OnEntered(SteamLobby lobby) {
		try {
			_lobby = lobby;
			MemberKeys.Clear();
			MemberCivs.Clear();
			Reload();
			CoreLog.Trace($"lobby entered: id={lobby.Id.Value} members={_members.Length} host={(IsHost ? "me" : _owner.ToString())}");
			PublishMods();
			LobbyRuleRegistry.ReevaluateAll();
			ModCore.Lobby.RaiseChanged();
		}
		catch (Exception e) { CoreLog.Once("lobby:entered", "lobby: entered handler: " + e); }
	}

	private static void OnDataChanged(SteamLobby lobby) {
		using var slow = CoreLog.Slow("lobby data event");
		try {
			// The game rewrites its own lobby data several times a second, so this must stay cheap: no member reload, no Changed event.
			// Members are tracked by the member events; the owner check is one id compare. Rule values are re-read here and raise their own Changed.
			if (!IsCurrent(lobby)) return;
			_dataEvents++;
			CheckOwner(lobby);
			LobbyRuleRegistry.ReevaluateAll();
		}
		catch (Exception e) { CoreLog.Once("lobby:data", "lobby: data handler: " + e); }
	}

	private static void OnMemberDataChanged(SteamLobby lobby, SteamFriend member) {
		try {
			if (!IsCurrent(lobby)) return;
			_dataEvents++;
			// someone published their rule keys: the "may desync" warning may change; the guard only matters when their civilizations changed
			if (ReadMemberMods(lobby, member)) CivilizationGuard.Run();
			GameBalance.Recompute("member mods");   // who has which mod decides which balance mods apply (cheap when nothing changed)
		}
		catch (Exception e) { CoreLog.Once("lobby:memberdata", "lobby: member data handler: " + e); }
	}

	private static void OnMemberChange(SteamLobby lobby, SteamFriend member, bool gone) {
		try {
			if (!IsCurrent(lobby)) return;
			CoreLog.Trace($"member {(gone ? "left" : "joined")}: {member.Id.Value}");
			if (gone && member.Id.Value == LocalSteamId) { Leave(); return; }
			Reload();
			CivilizationGuard.Run();   // a member without a civilization the host picked must not be sent its number
			LobbyRuleRegistry.ReevaluateAll();
			GameBalance.Recompute("members");
			ModCore.Lobby.RaiseChanged();
		}
		catch (Exception e) { CoreLog.Once("lobby:member", "lobby: member handler: " + e); }
	}

	/// <summary>The local player is no longer in the lobby.</summary>
	private static void Leave() {
		TraceDataEvents("leaving"); CoreLog.Trace("lobby left");
		_lobby = null;
		_owner = 0;
		_members = Array.Empty<LobbyMember>();
		MemberKeys.Clear();
		MemberCivs.Clear();
		LobbyRuleRegistry.ReevaluateAll();
		ModCore.Lobby.RaiseChanged();
	}

	/// <summary>
	/// The lobby turned into a match: rules freeze first, then mods hear about it. Called from the game's <c>onStartGame</c> event and, as a fallback,
	/// after <c>GameState.StartGame</c>; only the first call per match counts.
	/// </summary>
	public static void MatchStarted() {
		if (MatchRunning) return;
		TraceDataEvents("match start"); CoreLog.Info($"match started (inLobby={InLobby}, host={IsHost}, rules={LobbyRuleRegistry.All.Count})");
		MatchRunning = true;
		LobbyRuleRegistry.FreezeAll();
		GameBalance.Recompute("match started");   // building/technology values are fixed for the whole match from here
		BuildOrders.ResetLoaded();   // the AI reads its build orders again, with the trees active for this match
		ModCore.Match.RaiseStarted();
	}

	/// <summary>Back in the lobby or the menu: rules follow the lobby again (or fall back to their defaults outside one).</summary>
	public static void MatchEnded() {
		MatchState.Clear();
		if (!MatchRunning) return;
		TraceDataEvents("match end"); CoreLog.Info("match ended");
		MatchRunning = false;
		LobbyRuleRegistry.UnfreezeAll();
		ModSwitches.MatchEnded();   // mods switched on/off during the match take effect now
		UnitStatsRegistry.MatchEnded();   // unit-stat changes made during the match too
		GameBalance.MatchEnded();
		ModCore.Match.RaiseEnded();
	}

	// ------------------------------------------------------------------ members

	/// <summary>Re-reads owner and members from Steam.</summary>
	private static void Reload() {
		if (!_lobby.HasValue) return;
		var lobby = _lobby.Value;
		_owner = lobby.Owner.Id.Value;
		ulong me = LocalSteamId;
		var list = new List<LobbyMember>();
		try {
			// The generic enumerator only exposes Current; MoveNext lives on the non-generic interface.
			var members = lobby.Members.GetEnumerator();
			var cursor = members.Cast<Il2CppSystem.Collections.IEnumerator>();
			while (cursor.MoveNext()) {
				SteamFriend friend = members.Current;
				ulong id = friend.Id.Value;
				string name = null;
				try { name = friend.Name; } catch { /* name not cached yet */ }
				list.Add(new LobbyMember(id, string.IsNullOrEmpty(name) ? "Player" : name, id == _owner, id == me, true));
				ReadMemberMods(lobby, friend);
			}
		}
		catch (Exception e) { CoreLog.Once("lobby:members", "lobby: could not list the members: " + e.Message); }
		_members = list.ToArray();
	}

	/// <summary>Reads a member's published rule keys and civilizations; true when the civilizations changed.</summary>
	private static bool ReadMemberMods(SteamLobby lobby, SteamFriend member) {
		ulong id = member.Id.Value;
		if (id == LocalSteamId) return false;   // our own keys are known without asking Steam
		var keys = LobbyRuleRegistry.ParseKeys(lobby.GetMemberData(member, ModsKey));
		if (keys != null) MemberKeys[id] = keys; else MemberKeys.Remove(id);

		var civs = new HashSet<string>();
		string raw = lobby.GetMemberData(member, CivsKey);
		if (!string.IsNullOrEmpty(raw) && raw.StartsWith("v2"))   // older ModCore (v1, no versions) counts as not having the civilization
			foreach (var k in raw.Substring(2).Split(',')) if (k.Length > 0) civs.Add(k);
		MemberCivs.TryGetValue(id, out var before);
		bool changed = before == null ? civs.Count > 0 : !before.SetEquals(civs);
		if (civs.Count > 0) MemberCivs[id] = civs; else MemberCivs.Remove(id);
		return changed;
	}

	/// <summary>Tells the other members which rules this client has (Steam member data "modcore.mods").</summary>
	public static void PublishMods() {
		if (!_lobby.HasValue) return;
		try {
			_lobby.Value.SetMemberData(ModsKey, LobbyRuleRegistry.DescribeMods());
			MemberKeys[LocalSteamId] = LobbyRuleRegistry.ParseKeys(LobbyRuleRegistry.DescribeMods()) ?? new HashSet<string>();
			var civKeys = new List<string>();
			foreach (var c in CivilizationRegistry.All) civKeys.Add(CivTag(c));
			_lobby.Value.SetMemberData(CivsKey, "v2" + string.Join(",", civKeys));
		}
		catch (Exception e) { CoreLog.Once("lobby:publish", "lobby: could not publish the rule keys: " + e.Message); }
	}
}
