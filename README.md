# Room Loader Thread Fix

**English** | [한국어](README.ko.md)

A BepInEx mod for Rain World that fixes a vanilla bug where **CPU usage keeps growing every time the game saves** (hibernating, meeting an echo, warping, etc.). The extra load stays until you restart the game.
The bug is especially bad in the Watcher campaign. In long sessions, CPU usage can approach 100% and the game starts to stutter.

- Game version: v1.11.8 (including the Watcher DLC), BepInEx 5 (bundled with Remix)
- No dependencies, and it works alongside other mods
- Does not change game logic or save data (see "Gameplay impact" below)

## Installation
1. Download `roompreparerfix-<version>.zip` from [Releases](../../releases).
2. Extract it into `Rain World/RainWorld_Data/StreamingAssets/mods/`, so that you end up with `mods/roompreparerfix/modinfo.json`.
3. Start the game, enable **Room Loader Thread Fix** in the Remix menu, and restart.

## Root cause (v1.11.8, confirmed by measurement)
- `RoomPreparer` loads rooms on a background thread (`UpdateThread`). Between stages, the thread waits for the main thread to answer through `RoomPreparer.Update()`. It waits in a **`while(true)` busy loop with no `Sleep`**:
  - `status == 0`: waiting for `requestShortcutsReady` to be handled
  - `status == 4`: waiting for `requestReadyForAI` to be handled, which moves it to `status = 5`
- The main thread only answers when `RainWorldGame.Update` processes the current world's `loadingRooms`.
- If the game shuts down while a room is still loading (save → sleep or echo screen), nothing ever calls `Update()` again. The thread then **spins at 100% of one CPU core forever**. These threads pile up with every save and keep running even in the menus.
- Measured result (Watcher campaign, with a diagnostic mod): 4 saves left 3 orphaned threads, each using about 99% of a core (process CPU 314%, i.e. more than 3 cores). The same kind of play session with this mod enabled ran for 7 saves. 3 orphaned threads appeared, and all of them were stopped right away (room loader threads started: 167, finished: 167).
- Watcher is hit hardest because region changes and saves often happen at the same moment (echoes, warps).

## How the fix works
- It hooks `RoomPreparer.UpdateThread` to track which preparers have a running thread.
- When it finds an abandoned preparer, it sets `status = 5`. On its next loop iteration the thread enters `case 5` and returns normally.
  The thread can overwrite `status` outside its lock, so the mod sets it again every frame until the thread has actually exited.
- A preparer counts as abandoned when:
  1. its game has just finished `RainWorldGame.ShutDownProcess`. The mod stops it immediately.
  2. a safety check (run once per second) finds that it belongs to a game that has shut down, or that it belongs to an old world and is not in the current world's `loadingRooms`, and this has lasted for more than 5 seconds.
- The mod never touches preparers with `room.game == null` (used synchronously for map/world generation) or preparers that are still loading normally.
- Its actions are logged to `BepInEx/LogOutput.log` as `Stopping abandoned room loader thread` / `stopped cleanly`.

## Gameplay impact
The mod only stops threads that **would never finish in vanilla either**. The game sees exactly the same state with or without the mod.
- In vanilla, the only code that moves room loading forward is `RainWorldGame.Update`, which processes the active world's `loadingRooms`. Preparers that belong to a game that has shut down, or to a world that is no longer active, never make progress again. A new World object is created on every region change, and old ones are never reused.
- The thread's work (`ShortcutMapper`, `AImapper`, `AIdataPreprocessor`) only writes into its own Room object. It does not touch static state or the RNG.
- The mod never calls game code (it does not call `ShortCutsReady` or `ReadyForAI`). It only acts after `ShutDownProcess`, when saving has already finished, so save files are identical.
- When you return to a region, its World is rebuilt from the region files and the saved region state. Data from the abandoned Room is never used.

The only effect is that abandoned threads no longer eat CPU, which means less lag.
Whether the mod is allowed in speedruns is up to each leaderboard's rules. Please check with the moderators before submitting runs.

## Building
You need the .NET SDK (the project targets net48) and an installed copy of Rain World. If the game is installed somewhere else, pass `-p:RainWorldDir=...`.
```
dotnet build src/RoomPreparerFix.csproj -c Release
```
The output goes to `mod/plugins/` and is also copied to `mods/roompreparerfix` (turn this off with `-p:CopyToGame=false`).

## License
MIT
