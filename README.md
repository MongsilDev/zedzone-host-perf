# ZED ZONE Host Lag Reduction

Cuts lag and stutter for the host of a ZED ZONE multiplayer room, especially when players spread out across the map. The plugin's DLL and config file are named ZZPerf.

Only the host needs it. It does nothing in single player or when you join someone else's room, and guests do not need to install anything.

## Measured results

Test world, 7 simulated players spread out, nearby items panel open, version 0.4.0.

| | Without ZZPerf | With ZZPerf |
|---|---|---|
| Average frame time | 30 to 33 ms | 17.4 ms |
| Frames over 33 ms per minute | 132 to 185 | 4 |
| Frames over 100 ms per minute | 61 to 67 | 2 |

A second run of the same test averaged 20.5 ms with every feature on. Turning off one feature at a time from there:

- Offscreen sorting off: average frame time rises from 20.5 ms to 30.8 ms.
- Nearby items search off: frames over 100 ms per minute go from 2 to 86.
- Light list off: frames over 33 ms per minute go from 6 to 92.
- Water cache purge off: while chunks are loading, average frame time rises from about 24 ms to about 32 ms.

In a real session with 7 players connected, the earlier version 0.2.2 lowered the average frame time from 40.9 ms to 35.3 ms and cut frames over 33 ms in three minutes from 1151 to 626.

Each state was measured twice for 60 seconds inside the game, in mirrored order.

## Installation

1. Close the game.
2. Subscribe to [ZED ZONE code mod loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3834614258) and run `install.cmd` in its Workshop folder once. This installs BepInEx 6 IL2CPP.
3. Subscribe to Host Lag Reduction on the Steam Workshop. The loader picks up code mods from subscribed Workshop items.
4. Start the game through Steam and host a room.

To install a DLL you built yourself instead, put `ZZPerf.dll` in `ZED ZONE\Mods\ZZPerf\`.

## Features

| Feature | Setting | Default | What it does | Guest impact |
|---|---|---|---|---|
| Offscreen sorting | `CullOffscreenChunks` | On | Cuts the biggest share of host lag when players are spread across the map. | None |
| Nearby items search | `CacheSearch` | On | Removes the long freezes while the nearby items panel is open. | None |
| Light list | `UseRegisteredLights` | On | Fewer stutter spikes from light and shadow updates. | None |
| Water cache purge | `PurgeMinIntervalMs` | On | Smoother loading when players move into new areas. | None |
| Far chunk interval | `UpdateInterval` | Off | Updates distant parts of the map less often. | Possible |

Each feature is turned on or off with its key in the config file. `PurgeMinIntervalMs = 0` turns the water cache purge off, and `UpdateInterval = 2` or more turns the far chunk interval on.

Guest impact was judged by reading the game code. The four features that are on by default only change rendering state or caches inside the host's game, and none of their values are sent to guests or written to saves. The far chunk interval changes how often game logic runs in distant chunks. If you turn it on, tell your guests first.

### How it works

- Offscreen sorting disables `SortingGroup` on terrain objects in chunks outside the host's view. Position, state and update order are untouched, and chunks are restored the frame they come into view.
- Nearby items search: while the panel is open, the game searches the whole scene for water wells and containers every frame. ZZPerf answers those two searches from a list recorded when the objects are created, and resyncs with the original search every 30 seconds.
- Light list builds the shadow and outline light cache from the render pipeline's registered lights instead of searching the whole scene.
- Water cache purge runs the water tile cache cleanup at most once every 250 ms. Lookups still check expiry per entry, so results are the same.
- Far chunk interval sets the game's own far chunk update interval, so chunks farther than `Distance` from every player update only every N frames.

## Configuration

The config file is `ZED ZONE\BepInEx\config\local.zzperf.cfg` and is created on first launch. Changes are picked up within 2 seconds while the game is running.

| Section | Key | Default | Description |
|---|---|---|---|
| General | `Enabled` | true | Turns the whole plugin on or off. |
| General | `OnlyWhenHosting` | true | Runs only while you host a room. |
| General | `LogStats` | false | Writes activity counts to the BepInEx log every 30 seconds. |
| Sorting | `MarginUnits` | 48 | Chunks within this distance of the screen edge stay enabled. |
| Sorting | `DelaySeconds` | 1 | A chunk is disabled after staying offscreen this long. |
| Sorting | `BudgetMs` | 1 | Time budget in ms for disabling work, run every 0.1 seconds. |
| NearbyItems | `ResyncSeconds` | 30 | How often to resync with the original search while the panel is open. 0 resyncs only when the panel opens. |
| FarChunks | `Distance` | 150 | Chunks farther than this from every player count as far. Values below 64 are not applied. |

To check that it is running, look for `ZZPerf loaded` in the BepInEx log, or set `LogStats = true` to get a `stats` line every 30 seconds.

## Turning it off

- To turn everything off, set `Enabled = false`.
- To uninstall a Workshop install, close the game and unsubscribe from Host Lag Reduction. You can also delete `BepInEx\config\local.zzperf.cfg`.
- To uninstall a manual install, close the game and delete the `Mods\ZZPerf` folder and `BepInEx\config\local.zzperf.cfg`.

## Game version

Built and tested against ZED ZONE Steam build 26711990 with BepInEx 6.0.0-be.793 IL2CPP.

- The nearby items search checks the start of the game function it hooks and turns itself off if the game build is different.
- The other features patch game methods by name. If an update removes or renames one of those methods, that patch is skipped with a `patch failed` warning in the BepInEx log and the game's original code runs.
- If the light list, offscreen sorting, nearby items search or far chunk interval throws an error while running, that feature turns itself off until the game is restarted.

## Bug reports

Report bugs in GitHub Issues.

## Building

Requires the .NET SDK and a ZED ZONE install with BepInEx that has been launched once, so that the interop assemblies exist in `BepInEx\interop`. Game and interop DLLs are not included in this repository.

```
dotnet build -c Release -p:GameRoot="D:\SteamLibrary\steamapps\common\ZED ZONE"
```

`GameRoot` defaults to `C:\Program Files (x86)\Steam\steamapps\common\ZED ZONE\`. The output is `bin\ZZPerf.dll`.
