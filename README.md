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

Version 0.4.0, test world with 7 simulated players spread out and the nearby items panel open, 60 second windows measured inside the game. Each state was measured twice in mirrored order, and the table shows the range of the two.

Turning one feature on at a time, starting from `Enabled = false`:

| State | Average frame time | Frames over 33 ms per minute | Frames over 100 ms per minute |
|---|---|---|---|
| `Enabled = false` | 30.13 to 33.02 ms | 132 to 185 | 61 to 67 |
| Plugin on, every feature off | 31.35 to 31.39 ms | 134 to 151 | 64 |
| Light list only | 28.73 to 31.32 ms | 75 to 117 | 64 to 70 |
| Offscreen sorting only | 21.04 to 22.72 ms | 168 to 181 | 88 to 96 |
| Nearby items search only | 27.61 to 28.47 ms | 86 to 89 | 1 to 2 |
| All on (default) | 17.39 to 17.47 ms | 4 | 2 |

Turning one feature off at a time from the default settings:

| State | Average frame time | Frames over 33 ms per minute | Frames over 100 ms per minute |
|---|---|---|---|
| All on (default) | 20.43 to 20.54 ms | 5.5 to 6 | 2 to 2.2 |
| Light list off | 21.69 to 21.98 ms | 92 | 2 to 3 |
| Offscreen sorting off | 30.82 to 30.83 ms | 170 to 185 | 3 to 5.5 |
| Nearby items search off | 23.18 to 23.30 ms | 89 | 86 |

The water cache purge only matters while new chunks are loading. In 45 second windows right after the 7 simulated players spread out, with the other features on, turning it off raised the average from 23.32 to 24.86 ms to 31.09 to 33.07 ms, and frames over 33 ms per minute from 181 to 247 to 708 to 803.

In a real session with 7 players connected, version 0.2.2 without the nearby items search, alternating three 60 second windows each, the average frame time went from 40.85 ms to 35.28 ms and frames over 33 ms went from 1151 to 626.

## Game version

Built and tested against ZED ZONE Steam build 26711990 with BepInEx 6.0.0-be.793 IL2CPP. The nearby items search checks the start of the game function it hooks and turns itself off if the game build is different. The other features patch game methods by name.

## Building

Requires the .NET SDK and a ZED ZONE install with BepInEx that has been launched once, so that the interop assemblies exist in `BepInEx\interop`. Game and interop DLLs are not included in this repository.

```
dotnet build -c Release -p:GameRoot="D:\SteamLibrary\steamapps\common\ZED ZONE"
```

`GameRoot` defaults to `C:\Program Files (x86)\Steam\steamapps\common\ZED ZONE\`. The output is `bin\ZZPerf.dll`.
