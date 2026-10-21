# Implementation notes

## Lobby and lobby rules (how it works)

- The game uses Facepunch.Steamworks (interop `Fivemid.FiveSteam.dll`). The interop has no `event` members, so the static events of `SteamMatchmaking` are subscribed with the `add_OnLobby...` methods
  (`OnLobbyEntered`, `OnLobbyDataChanged`, `OnLobbyMember{DataChanged,Joined,Leave,Disconnected,Kicked,Banned}`), next to the game's own subscriptions. Steam starts after the plugins load, so the runner tick subscribes once `SteamClient.IsValid`.
- Steam sends nothing when the local player leaves a lobby, so the 4 Hz tick also checks `Lobby.MemberCount` and the owner. `Lobby.Members` is an IL2CPP `IEnumerable<Friend>`, enumerated with `GetEnumerator/MoveNext`.
- The match starts in `GameState.StartGame` (host directly, clients through `StartGameRPC`), which is where rules freeze (`MatchStarted`); `GameState.OnDisable` unfreezes them (`MatchEnded`).
- Rule data: lobby data `modcore.rule.<key>` ("1"/"0" for toggles, invariant integers for numbers). Member data `modcore.mods` is `v1;<id>|<name>|<version>|<key>,<key>;...`.

## Civilizations

- `Technologies.TechTree(civilization)` uses `First(...)` and throws for an unknown civilization: a prefix returns the registered tree provider's tree.
- `PlayerSkins.Civilization`'s setter switches over 0..4 and throws otherwise: a prefix sets the borrowed built-in civilization first, then stores the real number.
- A registered civilization without a tree provider (or with one that returns null) gets the tree of its `skinsLike` civilization: the original `First(...)` would throw.
- Picking a civilization for play: clicking the castle on the match-start screen runs `Player.NextCivilization` (host side, clients send `NextCivilizationRPC`), which steps through the player's unlocked civilizations (`Player.Inventory.UnlockedCivilizations`, from the Steam inventory) and wraps around. `NextCivilizationHook` (a prefix) adds the registered civilizations to the end of that cycle: after the last unlocked civilization the first registered one is set, then the next one, and after the last registered one the game's own wrap-around runs. The `PlayerSkins` constructor throws for unknown numbers, so the skins are built for the `skinsLike` civilization and the real number is stored afterwards. **Do not replace or patch the player's inventory.** An earlier version did (a postfix on `PlayerInventory.FromSteamworks`, then writing `PlayerInventory.FALLBACK`) and the lobby stopped building: `Player.NetworkStart` also picks the random starting civilization out of that same array (`NextSample<Civilization>`), and it threw `IndexOutOfRangeException`, so no player was spawned. Patching the tiny `UnlockedCivilizations` getter is no better: IL2CPP shares identical getters and hooking it crashed the game at startup.
- The ids come from a hash of the key (1000 and up), so every machine computes the same number.

## Event frequency (measured)

The game writes its own lobby and member data several times a second (a solo match with AIs produced over 100 `OnLobbyDataChanged` / `OnLobbyMemberDataChanged` events in a few minutes).
So `OnDataChanged` only re-evaluates the rules, and `Lobby.Changed` is raised for membership and owner changes only. Set `[Debug] LogLobbyEvents = true` in `DiceModders.ModCore.cfg`
to trace the events; the data events are summarised as one counter line per lobby/match phase.
