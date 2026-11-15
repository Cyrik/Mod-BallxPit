# BALL x PIT evolution helper (mod)

A personal BepInEx mod that shows, on the level-up screen, what each offered ball can evolve into with the balls already owned.

## Game

- Install: `D:\Games\BALLxPIT` (`Balls.exe`), Unity 6000.0.62f1, IL2CPP, metadata v31 (not encrypted).
- Saves: `%USERPROFILE%\AppData\LocalLow\Kenny Sun\BALL x PIT\`
- Save backup taken before modding: `save-backup\2026-11-15-before-bepinex\`

## Loader

BepInEx 6 bleeding edge, IL2CPP x64, build `6.0.0-be.793+dbc82f0`, from builds.bepinex.dev. Installed 2026-11-15.

Files it added to the game folder (nothing was overwritten):
`BepInEx\`, `dotnet\`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`

- **Disable the mod loader:** rename `winhttp.dll` to `winhttp.dll.off`.
- **Remove completely:** delete the six items above.
- Log: `D:\Games\BALLxPIT\BepInEx\LogOutput.log`

## Build tools

.NET 8 SDK 8.0.428, user-only install (no PATH change, no admin) at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`, installed 2026-11-15 with Microsoft's signed `dotnet-install.ps1`. Call it by full path. Remove by deleting that folder.

## Building the mod

Source: `src\EvoHelper\`. Build (also copies `EvoHelper.dll` into `D:\Games\BALLxPIT\BepInEx\plugins\EvoHelper\`):

```
& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" build src\EvoHelper\EvoHelper.csproj -c Release
```

Add `-p:SkipDeploy=true` to compile without copying (e.g. while the game is running). Close the game first when replacing an installed build (the DLL is locked while it runs). Plugins load only at game start.

## What the game exposes (from a Cpp2IL dump)

- `InfoDB.I.Heroes` / `InfoDB.I.Passives`: every ball ("Hero") and passive.
- `UpgradeInfo.MergeComponents` (`UpgradeInfo[][]`): ingredient sets that make an evolution. `UpgradeInfo.Evolutions`: what it can become. `HasMergeCombo(a, b)`.
- `UpgradeMgr.I.GetHero(HeroType)`: owned ball instances.
- `LevelUpUI.I`: the level-up screen. `Btns[]` (`LevelUpBtn.TgtInfo` = offered ball), `_choices`, `CurHeroItems`. `Activate(LevelUpType)` runs when it opens.

### Recipe dump (v0.1, 2026-11-15)

The game's own data has 118 ball recipes and 39 passive recipes. The `dlc=0` set matches the two community charts (Naturalist update, Sep 2026). The game also has newer content the charts lack: balls Water, Wood, Force and Beast, 22 ball recipes and 22 passive recipes marked `dlc=1`, plus extra paths to existing results (e.g. Water + Poison -> Swamp, Beast + Egg Sac -> Spider Queen). So the mod should always read recipes from the game, never from the charts.

The mod reads only. It never writes game state or saves.
