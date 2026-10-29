using System;
using System.Collections.Generic;
using System.Text;
using GameStructuredText = DiceKingdoms.Interface.StructuredText;

namespace DiceModders.ModCore;

/// <summary>
/// A text in the game's own format (<c>StructuredText</c>): plain parts, player names in their colour, and icon tokens inside the text:
/// <c>{r.food}</c> <c>{r.wood}</c> <c>{r.stone}</c> <c>{r.gold}</c> <c>{r.hammer}</c> <c>{r.culture}</c> <c>{r.military}</c> <c>{r.disaster}</c> (resources),
/// <c>{b.castle}</c> (a building, local player's look), <c>{d.meteorite}</c> (a disaster), <c>{o.population}</c> <c>{o.ship}</c> <c>{o.research}</c>
/// <c>{o.hungry}</c> <c>{o.scoreboard}</c> <c>{o.question-mark}</c> <c>{o.clean-tile}</c>. Unknown tokens are dropped.
/// Every player sees it with the built-in elements only, so players without the mod see it too.
/// </summary>
public sealed class GameText {
	private readonly List<object> _parts = new();   // string or MatchPlayer

	/// <summary>Plain text; icon tokens such as <c>{r.gold}</c> inside it become icons.</summary>
	public GameText Text(string text) { if (!string.IsNullOrEmpty(text)) _parts.Add(text); return this; }

	/// <summary>A player's name in the player's colour.</summary>
	public GameText Player(MatchPlayer player) { if (player != null && player.IsValid) _parts.Add(player); return this; }

	/// <summary>The text as the game would show it, with its markup (for logs, or for drawing it yourself).</summary>
	public string Render() {
		try { var s = ToGame(); return s != null ? s.Render() : ""; }
		catch { return Plain(); }
	}

	/// <summary>The parts without markup (player names as plain names, tokens kept as written).</summary>
	public string Plain() {
		var sb = new StringBuilder();
		foreach (var part in _parts) sb.Append(part is MatchPlayer p ? p.Name : (string)part);
		return sb.ToString();
	}

	public override string ToString() => Plain();

	/// <summary>The game's own object. Each part becomes one hole of a format string, so the game converts it by type (string: text with tokens, Player: coloured name).</summary>
	public GameStructuredText ToGame() {
		var format = new StringBuilder();
		var args = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(_parts.Count);
		for (int i = 0; i < _parts.Count; i++) {
			format.Append('{').Append(i).Append('}');
			args[i] = _parts[i] is MatchPlayer p ? p.Game : (Il2CppSystem.Object)(Il2CppSystem.String)(string)_parts[i];
		}
		var formattable = Il2CppSystem.Runtime.CompilerServices.FormattableStringFactory.Create(format.ToString(), args);
		return new GameStructuredText(formattable);
	}
}

public static partial class ModCore {
	/// <summary>Messages in the game's chat/log panel. Main thread only.</summary>
	public static class Messages {
		/// <summary>
		/// Adds a line to the game's log (the panel above the resources, where disasters and attacks are reported). Only the host can do that: the line is
		/// synced to every player by the game, also to players without the mod. Returns false on a client or outside a match.
		/// </summary>
		public static bool Post(GameText text) {
			Core.Ensure();
			if (text == null) return false;
			try {
				var gs = MatchState.Game;
				var log = gs != null ? gs.log : null;
				if (log == null) return false;
				if (!log.IsServer) { CoreLog.Once("messages:post-client", "Messages.Post: only the host can add log lines (this machine is a client)"); return false; }
				log.AddMessage(text.ToGame());
				return true;
			}
			catch (Exception e) { CoreLog.Once("messages:post", "Messages.Post failed: " + e.Message); return false; }
		}

		/// <summary>Same as <see cref="Post(GameText)"/> for plain text (icon tokens work).</summary>
		public static bool Post(string text) => Post(new GameText().Text(text));

		/// <summary>
		/// Sends a chat message as the local player, exactly as if they had typed it (everybody sees it with the player's name). Returns false when there is no
		/// local player. Do not send chat on the player's behalf without them asking for it.
		/// </summary>
		public static bool Chat(string text) {
			Core.Ensure();
			if (string.IsNullOrWhiteSpace(text)) return false;
			try {
				var player = DiceKingdoms.Game.Player.LocalPlayer;
				if (player == null) return false;
				player.Chat(text);
				return true;
			}
			catch (Exception e) { CoreLog.Once("messages:chat", "Messages.Chat failed: " + e.Message); return false; }
		}
	}
}
