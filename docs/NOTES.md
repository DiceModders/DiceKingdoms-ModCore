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
- The ids come from a hash of the key (1000 and up), so every machine computes the same number.
