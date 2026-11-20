using System;
using System.Collections.Generic;

namespace DiceModders.ModCore;

public static partial class ModCore {
	public static partial class Lobby {
		/// <summary>
		/// Small text values that each player publishes for everybody in the Steam lobby to read: a player's own choices that the other computers
		/// must know, for example which upkeep they pay. The Steam lobby stays open during a match, so the values work in the lobby and in the match.
		/// Every player writes only their own values; a computer player has none (<see cref="Get"/> returns null).
		/// </summary>
		/// <remarks>
		/// Steam delivers a change to the other computers after a short delay (usually well under a second). A value that changes the simulation must
		/// therefore name the turn it is meant for (for example "from turn 7"), so that every computer applies it at the same moment.
		/// </remarks>
		public static class PlayerValues {
			/// <summary>Publishes this player's value for <paramref name="key"/> (use your mod's prefix, e.g. "upkeep.choices"). False when not in a lobby.</summary>
			public static bool Set(string key, string value) { Core.Ensure(); return LobbyState.SetOwnValue(key, value); }

			/// <summary>The value player <paramref name="steamId"/> published for <paramref name="key"/>, or null (no lobby, no such member, never set).</summary>
			public static string Get(ulong steamId, string key) { Core.Ensure(); return LobbyState.GetMemberValue(steamId, key); }

			/// <summary>A player's published values changed (Steam does not say which key): the argument is that player's Steam id.</summary>
			public static event Action<ulong> Changed;

			internal static void RaiseChanged(ulong steamId) {
				var handler = Changed;
				if (handler == null) return;
				foreach (Action<ulong> d in handler.GetInvocationList()) {
					try { d(steamId); } catch (Exception e) { CoreLog.Error("Lobby.PlayerValues.Changed handler failed: " + e); }
				}
			}
		}
	}
}
