# ZZPerf

ZZPerf is a BepInEx IL2CPP plugin for ZED ZONE that reduces frame time on the host of a multiplayer room. When players spread out and many chunks are loaded, the host's main thread slows down even without network load. ZZPerf trims the parts of that work that only affect the host's own screen and the host's internal caches.

Only the host needs it. It does nothing in single player or when you join someone else's room, and guests do not need to install anything.

## Features

| Feature | Setting | Default | What it does | Guest impact |
|---|---|---|---|---|
| Light list | `UseRegisteredLights` | On | Builds the shadow and outline light cache from the render pipeline's registered lights instead of searching the whole scene. | None |
| Water cache purge | `PurgeMinIntervalMs` | 250 | Runs the water tile cache cleanup at most once every 250 ms. Lookups still check expiry per entry, so results are the same. | None |
| Offscreen sorting | `CullOffscreenChunks` | On | Disables `SortingGroup` on terrain objects in chunks outside the host's view. Position, state and update order are untouched, and chunks are restored the frame they come into view. | None |
| Nearby items search | `CacheSearch` | On | While the nearby items panel is open, the game searches the whole scene for water wells and containers every frame. ZZPerf answers those two searches from a list recorded when the objects are created, and resyncs with the original search every 30 seconds. | None |
| Far chunk interval | `UpdateInterval` | 1 | Sets the game's own far chunk update interval. Chunks farther than `Distance` from every player update only every N frames. 1 keeps the original behavior. | Possible |

Guest impact was judged by reading the game code. The four features that are on by default only change rendering state or caches inside the host's game, and none of their values are sent to guests or written to saves. The far chunk interval changes how often game logic runs in distant chunks, so it is off by default. If you raise it, tell your guests first.

## Installation

1. Close the game.
2. Subscribe to ZED ZONE code mod loader on the Steam Workshop, item 3834614258, and run `install.cmd` in its Workshop folder once. This installs BepInEx 6 IL2CPP.
3. Put `ZZPerf.dll` in `ZED ZONE\Mods\ZZPerf\`.
4. Start the game and host a room. The config file is created on first launch.

## Configuration

The config file is `ZED ZONE\BepInEx\config\local.zzperf.cfg`. Changes are picked up within 2 seconds while the game is running.

| Section | Key | Default | Description |
|---|---|---|---|
| General | `Enabled` | true | Turns the whole plugin on or off. |
| General | `OnlyWhenHosting` | true | Runs only while you host a room. |
| General | `LogStats` | false | Writes activity counts to the BepInEx log every 30 seconds. |
| Light | `UseRegisteredLights` | true | Light list feature. |
| Water | `PurgeMinIntervalMs` | 250 | Minimum interval between water cache purges in ms. 0 restores the original behavior. |
| Sorting | `CullOffscreenChunks` | true | Offscreen sorting feature. |
| Sorting | `MarginUnits` | 48 | Chunks within this distance of the screen edge stay enabled. |
| Sorting | `DelaySeconds` | 1 | A chunk is disabled after staying offscreen this long. |
| Sorting | `BudgetMs` | 1 | Time budget in ms for disabling work, run every 0.1 seconds. |
| NearbyItems | `CacheSearch` | true | Nearby items search feature. |
| NearbyItems | `ResyncSeconds` | 30 | How often to resync with the original search while the panel is open. 0 resyncs only when the panel opens. |
| FarChunks | `UpdateInterval` | 1 | Update far chunks every N frames. Values above 1 can change what guests see. |
| FarChunks | `Distance` | 150 | Chunks farther than this from every player count as far. Values below 64 are not applied. |

## Turning it off

- To turn everything off, set `Enabled = false`. This takes effect within 2 seconds, even mid-game.
- To turn off a single feature, set `UseRegisteredLights = false`, `CullOffscreenChunks = false`, `PurgeMinIntervalMs = 0` or `CacheSearch = false`.
- To uninstall, close the game and delete the `Mods\ZZPerf` folder and `BepInEx\config\local.zzperf.cfg`.

## Measured results

Both measurements below used version 0.2.2, which has the light list, water cache and offscreen sorting features with default settings. The nearby items search was added in 0.4.0 and has not been measured yet.

In a test world with 7 simulated players spread out and 153 chunks loaded, measured in 5 second windows:

| | Average frame time | 99th percentile |
|---|---|---|
| Off | 34.12 to 35.21 ms | 72.70 to 78.24 ms |
| On | 21.90 to 22.18 ms | 26.79 to 28.22 ms |

In a real session with 7 players connected, alternating three 60 second windows each, the average frame time went from 40.85 ms to 35.28 ms and frames over 33 ms went from 1151 to 626.

## Game version

Built and tested against ZED ZONE Steam build 26711990 with BepInEx 6.0.0-be.793 IL2CPP. The nearby items search checks the start of the game function it hooks and turns itself off if the game build is different. The other features patch game methods by name.

## Building

Requires the .NET SDK and a ZED ZONE install with BepInEx that has been launched once, so that the interop assemblies exist in `BepInEx\interop`. Game and interop DLLs are not included in this repository.

```
dotnet build -c Release -p:GameRoot="D:\SteamLibrary\steamapps\common\ZED ZONE"
```

`GameRoot` defaults to `C:\Program Files (x86)\Steam\steamapps\common\ZED ZONE\`. The output is `bin\ZZPerf.dll`.
