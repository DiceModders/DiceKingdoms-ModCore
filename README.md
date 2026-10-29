# ModCore

Shared **game and networking logic** for [Dice Kingdoms](https://store.steampowered.com/) mods (BepInEx 6 IL2CPP).

ModCore has no interface at all. It answers the questions every multiplayer mod ends up asking, once, and lets all mods share the answers:

- Is the player in a Steam lobby? Who is in it? Who is the host? Did the lobby just turn into a match?
- How do I add a setting that the **host** chooses in the lobby and **everybody** follows, frozen when the match starts?
- Does everybody in the lobby have my mod? If not, warn before the game desyncs.
- How do I add a new civilization without the game throwing?

Windows, buttons and settings screens are the job of [UiKit](https://github.com/DiceModders/UiKit). ModCore and UiKit do not know each other.
[ModBridge](https://github.com/DiceModders/DiceKingdoms-ModBridge) is the small plugin that shows ModCore's data on UiKit's screens (lobby rules in the lobby
settings box, civilizations in the Rules drop-down).

> **Desync protection, not anticheat.** The game has no anticheat or ban system: it uses Steam to connect the players and its own synchronisation for the match.
> A mod that changes the simulation on one machine only makes that machine disagree with the others. ModCore's compatibility check tells the players about this
> *before* the match; it cannot and does not stop anybody from playing.

| You want | Use |
|---|---|
| Know whether the player is in a Steam lobby, who is in it and who hosts it | `ModCore.Lobby.InLobby / IsHost / Members / Changed` |
| React when the lobby turns into a match (and when it is over) | `ModCore.Lobby.MatchStarted / MatchEnded` |
| A setting the host chooses in the lobby and everybody follows | `ModCore.Lobby.Rules.Toggle / Number` |
| Find out who in the lobby lacks a mod (desync warning) | `ModCore.Lobby.Compatibility.Warnings` |
| Add a civilization the game accepts (tech tree, skins) | `ModCore.Civilizations.Add(...)` |

---

## Install (players)

Copy `DiceModders.ModCore.dll` into `Dice-Kingdoms\BepInEx\plugins`. Nothing else is needed. Mods that use it list it as a dependency, so a mod manager installs it for you.
Install ModBridge as well if you want the screens (otherwise rules still work, they just have no rows to edit them).

## Use it in your mod (developers)

### 1. Project setup

Add to your `.csproj` (a full file is in [docs/EXAMPLE_MOD.md](docs/EXAMPLE_MOD.md)):

```xml
<ItemGroup>
  <Reference Include="interop/*.dll"><Private>false</Private></Reference>                       <!-- the game's interop assemblies -->
  <Reference Include="DiceModders.ModCore"><HintPath>path\to\DiceModders.ModCore.dll</HintPath><Private>false</Private></Reference>
</ItemGroup>
```

`Private=false` matters: never copy ModCore into your own package. Two copies would both patch the game.

### 2. Depend on it

```csharp
[BepInPlugin("com.example.mymod", "My Mod", "1.0.0")]
[BepInDependency("DiceModders.ModCore")]      // ModCore loads first
public class MyModPlugin : BasePlugin { ... }
```

### 3. The name clash

The namespace `DiceModders.ModCore` contains a class `ModCore`. Inside any namespace that starts with `DiceModders.` the compiler reads `ModCore` as the namespace, so add an alias:

```csharp
using Core = DiceModders.ModCore.ModCore;     // then: Core.Lobby..., Core.Civilizations...
using DiceModders.ModCore;                    // for LobbyMember, ModCivilization, ILobbyRule
```

Outside `DiceModders.*` (for example `namespace MyName.MyMod`) a plain `using DiceModders.ModCore;` is enough.

### 4. Call it

Everything is safe to call from `Load()`. ModCore installs its patches the first time you touch it (or call `ModCore.Init()`).

---

## Lobby

```csharp
if (Core.Lobby.InLobby && Core.Lobby.IsHost) { /* ... */ }
foreach (LobbyMember m in Core.Lobby.Members)
    Log($"{m.Name} id={m.SteamId} host={m.IsHost} me={m.IsLocal}");

Core.Lobby.Changed      += () => Refresh();   // joined/left, member joined/left/kicked, owner changed (rule values: use rule.Changed)
Core.Lobby.MatchStarted += () => Freeze();    // the lobby turned into a match (runs on every machine)
Core.Lobby.MatchEnded   += () => Unfreeze();  // back in the lobby or the menu
```

| Member | Meaning |
|---|---|
| `InLobby` | The local player is in a Steam lobby. |
| `IsHost` | The local player owns the lobby. |
| `MatchRunning` | A match is in progress (between `MatchStarted` and `MatchEnded`). |
| `Members` | `IReadOnlyList<LobbyMember>`: `SteamId`, `Name`, `IsHost`, `IsLocal`, `IsConnected`. |
| `LocalSteamId` | The local Steam id (0 without Steam). |
| `Current` | The raw `Steamworks.Data.Lobby?` for anything ModCore does not wrap. |
| `Changed`, `MatchStarted`, `MatchEnded` | Events, raised on the main thread. |

Limits worth knowing: a lobby entered **before** ModCore loaded is not seen (ModCore loads at game start, so this only matters for hot-reloading). Steam sends no event when the local
player leaves a lobby, so ModCore polls four times a second; `Changed` therefore arrives up to 250 ms late in that one case.

### Lobby rules

A *rule* is a setting the host edits before the match. The value lives in the Steam lobby data, so every client sees the same value, and it is **frozen** when the match starts so nobody can
change it mid-game.

```csharp
var meteors = Core.Lobby.Rules.Toggle("mymod.meteors", "Meteors", defaultValue: true, description: "Disasters hit the islands.");
var speed   = Core.Lobby.Rules.Number("mymod.speed", "Speed", defaultValue: 1, min: 1, max: 5, step: 1, visible: () => cheatsUnlocked);

meteors.Changed += v => Log($"meteors is now {v}");
if (meteors.Value) SpawnMeteor();                  // use .Value everywhere in gameplay code
```

| Member of `LobbyRule<T>` | Meaning |
|---|---|
| `Value` | What the game should use: the host's value in a lobby, `Default` outside one, a frozen copy during a match. |
| `Live` | The current lobby value, ignoring the freeze (what a settings screen shows). |
| `Default`, `Key`, `Title`, `Description` | As registered. `Min`, `Max`, `Step` for numbers. |
| `Editable` | The local player may change it now (host, before the match). |
| `Visible` | Your `visible` callback; screens hide invisible rules. |
| `IsNonDefault` | The host changed it from the default (this is what triggers compatibility warnings). |
| `TrySet(value)` | Host-only; returns false when not allowed. Screens call this. |
| `Changed` | Fires on every client when `Value` changes. |
| `AffectsSimulation` | Set `affectsSimulation: false` for purely visual rules so they never cause a warning. |

Rules to follow:

1. **Prefix the key with your mod** (`mymod.meteors`). Registering a key twice returns the same rule.
2. **Read `Value`, never cache it.** It changes when the lobby changes.
3. **Only rules that change what the simulation does need to be rules.** A local graphics option is a normal config entry.
4. `ModCore.Lobby.Rules.All` lists every rule (`ILobbyRule`, with `ModId`, `ModName`, `ModVersion` taken from the registering plugin's `[BepInPlugin]`); `Registered` fires when a new one appears.

### Compatibility check

Every client publishes its rule keys and mod versions in its Steam member data (`modcore.mods`). When the host has set a rule away from its default and some member does not have the mod,
`Core.Lobby.Compatibility.Warnings` contains a line such as `"Alice does not have Meteors, this rule may desync."`. `Core.Lobby.Compatibility.IsCompatible` is `Warnings.Count == 0`.
Screens (ModBridge) show these lines; your mod can also block a start button or log them.

## Civilizations

```csharp
var civ = Core.Civilizations.Add("authorname.mycivilization", "My civilization", () => myTechTree, skinsLike: 2);
// civ.Id is the civilization number: the same key gives the same id on every machine, 5..127
// (the game sends the civilization as one signed byte; the built-in ones are 0..4)
```

| Parameter | Meaning |
|---|---|
| `key` | Unique, prefixed with your name. It is hashed into the id, so multiplayer and saves agree. |
| `name` | Display name. |
| `techTree` | Function returning the game's `DiceKingdoms.Game.TechTree` for it. May be `null` (or return `null`): the tree of the `skinsLike` civilization is used then, so nothing throws. |
| `skinsLike` | Built-in civilization whose biome and visuals are used: 0 Europe, 1 Middle East, 2 Viking, 3 Ancient, 4 Maya. |

The game uses the civilization number in several places and throws (or freezes) for an unknown one. ModCore patches each of them:

| Game code | ModCore |
|---|---|
| `Technologies.TechTree`, and the copies the compiler inlined into `Statistics.TechTree` / `IsTechnologyLearnable` | Your tree (or the `skinsLike` tree); the learnable rule is reproduced for your civilization |
| `PlayerSkins` constructor / setter (biome switch over 0..4) | Built for `skinsLike`, the real number stored afterwards |
| `PlayerSkins.Clone()` (the Rules screen clones the lobby's skins) | Cloned through `skinsLike`, the real number stored afterwards |
| The network readers of `PlayerSkinsSerialization` (the number travels as one byte) | Build the skins from the number; an unknown number becomes the first civilization instead of throwing |
| `PlayerSkins.Get*Prefab` (skin registry lookups by civilization) | Your `civ.Skins` model if you set one, otherwise the look of `skinsLike` |
| `AIPlay.SampleBuildOrder` | Uses the build orders of `skinsLike` |
| The castle click (`Player.NextCivilization`) | Your civilization is added after the unlocked ones, only when every lobby member has it (published as `modcore.civs`); the host moves a player off one somebody lacks |
| The text key `civilization.<id>.name` | `name` is added to the text table of the current language |

Your mod provides what is its own: `civ.Skins` (building, unit, nature and ship prefabs; leave out what you do not replace), the tech tree (`ModCore.Civilizations.CloneTree(from)` copies a built-in one to
edit), and the name. See the wiki page *Making a civilization* for the step by step guide, every asset a civilization can have and what cannot be changed (biome, AI build orders, 5..127).
`Civilizations.All`, `Added`, `Removed` and `civ.Remove()` exist for tools. To make the civilization selectable on the Rules screen, players install ModBridge.

`[Debug] DumpGameData = true` in the ModCore config makes ModCore write `BepInEx/ModCore-game-data.txt` (technologies, tech trees, building types, the skin registry, prefabs) and
`BepInEx/ModCore-sprites.txt` (every sprite) once the game has loaded: the reference for finding the game's own prefabs and sprites to copy.

---

## Build from source

Requirements: .NET SDK 6 or newer, the game installed.

```powershell
$game = "<your Dice-Kingdoms folder>"   # the folder that contains BepInEx, e.g. ...\steamapps\common\Dice-Kingdoms (Steam: Properties > Installed Files > Browse)
git clone https://github.com/DiceModders/DiceKingdoms-ModCore
cd DiceKingdoms-ModCore
.\scripts\link-interop.ps1 $game   # makes .\interop point at BepInEx\interop (git-ignored)
dotnet build -c Release --no-incremental
Copy-Item bin\Release\net6.0\DiceModders.ModCore.dll "$game\BepInEx\plugins" -Force
```

The interop folder exists after the first game start with BepInEx 6 installed (it is generated into `BepInEx\interop`).

## Project layout

| Path | What is in it |
|---|---|
| `Plugin.cs` | The BepInEx entry point; calls `Core.Ensure()`. |
| `ModCore.cs`, `Core.cs` | Public `ModCore.Init()`; one-time installation of hooks and the helper component. |
| `CoreRunner.cs` | A `MonoBehaviour` that ticks the lobby state four times a second. |
| `Lobby/LobbyApi.cs` | The public lobby, rules and compatibility API (`ModCore.Lobby`). |
| `Lobby/LobbyState.cs` | Steam subscription, members, owner, match start/end, freezing. |
| `Lobby/LobbyHooks.cs` | Harmony patches on `GameState.StartGame` / `OnDisable`. |
| `Lobby/LobbyRuleRegistry.cs`, `LobbyMods.cs` | Rule registry, the `modcore.mods` format, which plugin registered a rule. |
| `Civilizations/CivilizationsApi.cs` | Public API (`Add`, `CloneTree`, registry). |
| `Civilizations/CivilizationHooks.cs` | The patches that make unknown civilization numbers safe (tree, skins, network readers, castle click, AI, `Statistics`, guard). |
| `Civilizations/CivilizationSkins.cs` | `civ.Skins` (own models) and the skin lookup patches. |
| `Civilizations/CivilizationText.cs` | Adds the civilization names to the game's text table. |
| `Civilizations/GameDataDump.cs` | The `DumpGameData` developer tool. |
| `docs/NOTES.md` | How it works inside and why (read before changing the hooks). |
| `docs/EXAMPLE_MOD.md` | A complete minimal mod. |

## Contributing

- Keep ModCore **UI free** and **UiKit free**. If a feature needs a screen, it belongs in ModBridge; if it is about drawing, it belongs in UiKit.
- Public overloads instead of optional parameters on existing methods (optional parameters change the signature and break compiled mods).
- Patch narrowly, wrap Harmony patch bodies in `try/catch` and log through `CoreLog.Once(key, message)` so a failure logs once and never every frame.
- Test with two game instances (or a friend): lobby code only shows its bugs with a second member.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `CS0234` / name `ModCore` is a namespace | Use the alias from "The name clash". |
| Rules never change in the lobby | You are not the host, or the match already started (`Editable` is false). |
| `Value` is the default in a lobby | The host has not changed it, or Steam is not initialised yet (`ModCore.Lobby.InLobby` false). |
| No rows in the lobby settings | Install ModBridge (and UiKit); ModCore itself has no screens. |
| Game throws with a mod civilization | Send `Player.log` (the game's own log, `...\LocalLow\Fivemid\Dice-Kingdoms`) with the log: it has the full stack trace, which names the game code that still reads the number. See `docs/NOTES.md`. |
| A friend's game freezes when you pick the civilization | The friend lacks the mod; ModCore only offers civilizations everybody has, so check both have the same ModCore version. |

Logs are in `BepInEx\LogOutput.log`; ModCore lines are prefixed `[ModCore]`. Report issues with the log attached.


## License

SPDX-License-Identifier: LGPL-2.1-or-later. See [LICENSE](LICENSE).
