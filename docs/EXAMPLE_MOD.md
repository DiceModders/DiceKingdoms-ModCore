# Example mod using ModCore

A complete minimal plugin: one lobby rule that the host chooses, one reaction to the match starting, and one civilization.
For a version that also has a window and settings screen see ModBridge's `docs/FIRST_MOD.md`.

## `MyMod.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>latest</LangVersion>
    <RestoreAdditionalProjectSources>
      https://api.nuget.org/v3/index.json;
      https://nuget.bepinex.dev/v3/index.json;
      https://nuget.samboy.dev/v3/index.json
    </RestoreAdditionalProjectSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BepInEx.Unity.IL2CPP" Version="6.0.0-be.*" IncludeAssets="compile" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="interop/*.dll"><Private>false</Private></Reference>
    <Reference Include="DiceModders.ModCore"><HintPath>path/to/DiceModders.ModCore.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
```

## `Plugin.cs`

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using DiceModders.ModCore;
using Core = DiceModders.ModCore.ModCore;       // only needed inside a DiceModders.* namespace; harmless elsewhere

[BepInPlugin("com.example.mymod", "My Mod", "1.0.0")]
[BepInDependency("DiceModders.ModCore")]
public class MyModPlugin : BasePlugin {
    private LobbyRule<bool> _meteors;

    public override void Load() {
        // The host picks this in the lobby; everybody follows; it is frozen when the match starts.
        _meteors = Core.Lobby.Rules.Toggle("mymod.meteors", "Meteors", true, "Disasters hit the islands.");
        _meteors.Changed += v => Log.LogInfo($"meteors: {v}");

        Core.Match.Started += () => Log.LogInfo($"match started, meteors = {_meteors.Value}");
        Core.Match.Ended   += () => Log.LogInfo("match over");

        // A civilization the game accepts. Provide the tech tree yourself.
        Core.Civilizations.Add("example.mycivilization", "My civilization", () => null, skinsLike: 2);
    }

    // somewhere in your gameplay patch:
    //   if (_meteors.Value) SpawnMeteor();
}
```

## What happens at runtime

1. Install **ModCore** (and, to see the rule on screen, **UiKit** + **ModBridge**) in `BepInEx\plugins`, then your mod.
2. In the lobby the host sees "Meteors" under Settings > Lobby > Rules (ModBridge). Guests see the host's value, read-only.
3. If the host turns Meteors off and a guest has no `MyMod`, everybody sees the warning "<guest> does not have My Mod, this rule may desync."
4. When the match starts the value freezes; `Value` stays the same until the match ends.

## Thunderstore `manifest.json` (excerpt)

```json
{
  "name": "MyMod",
  "version_number": "1.0.0",
  "dependencies": ["BepInEx-BepInExPack_IL2CPP-6.0.x", "DiceModders-ModCore-1.0.0"]
}
```

Use the real package names and versions shown on Thunderstore. List ModBridge (and UiKit) as *optional* if your mod works without screens, or as required if it does not.
