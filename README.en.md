<p align="center">
  <img src="docs/afdian-support.jpg" alt="Support Awan on Afdian" width="280">
  <a href="https://github.com/buguniaoOVO/PCFishController"><img src="docs/star-support.png" alt="Give PCFish Assistant a Star" width="280"></a>
</p>

# PCFish Controller

A background assistant for the idle fish-breeding game PCFish. It runs the breeding loop and the tank upgrades for you, while the game window sits minimized or behind other windows.
The BepInEx console hides on startup, and file logging continues.

## How it is built

| Component | Role |
|---|---|
| `pcfish-autohelper/` | BepInEx plugin. Opens a loopback channel on `127.0.0.1:27777` inside the game process. |
| `PCFishController/` | Standalone desktop app. Shows status and sends actions. |

Breeding goes through the game's own data and network entry points. The controller is a plain WinForms program; closing it leaves the game running.

## What it does

- Overview shows run status, game connection, fish count, breeding counter and tank level. Start All and Stop All control automatic breeding and upgrade checks
- Every 15-30 minutes (configurable), reads the breeding counter and breeds until it reaches zero, then waits for the next check
- Picks fish by your goal route, or by rarity and stars; skips fish on cooldown, out of breed uses, locked, or displayed
- Skips direct parent-child pairs, and avoids combinations the server rejected
- Keeps at least 60 seconds between actions, enforced by the plugin and the controller
- Verifies breeding results against parent counts and the returned new fish ID
- Reads your remaining breed counts from the server before each round, to filter out a stale local cache
- Tracks delayed replies and checks fish changes before resuming a timed-out request
- Runs auto synthesis when the warehouse passes a configurable threshold (900 by default). It excludes season fish and season recipe fish, prefers fish with no breed uses left, and confirms each craft by re-reading the fish list
- Synthesis follows real click steps: open the function window, switch to the Merge tab, click "+" once per fish, then click Synthesize. It waits for the animation, closes the result popup, then closes the window, with a pause between each step
- Dismisses the game's own dialog (for example "a network error occurred") when it appears, records it, and retries later
- A configurable safety wait (5 seconds by default) runs after each craft before the next action
- Closes the game's own "invite a friend for a free heart" popup when it appears. That window covers the function window, so the assistant clicks its Close button through the game's own entry point, leaving other dialogs untouched

## Interface

Overview, Auto Breeding, Warehouse, Synthesis Route, Logs and Settings. The Warehouse lists species, rarity, stars, remaining breeds and cooldown, with search, filters and sortable columns. The interface supports Chinese and English. The close action can ask each time, exit the assistant, or minimize it to the system tray.

## One-click setup

The Settings button locates the game folder, installs the BepInEx 6 runtime when missing, copies the plugin into `BepInEx\plugins`, starts the game once to write the config file, then turns on the in-game action switch. Progress prints as it runs.

The Synthesis Route uses the game's rarity colors for fish names. Yellow, blue and green mark missing materials, sufficient quantities and season fish ready to synthesize in both the route and table.

## Safety

The channel binds to `127.0.0.1` only. The in-game switch that allows external actions is off by default, and the controller has its own ARM toggle. On an error the assistant stops the current action, and the logs hold no authentication tokens.

## Requirements

Windows 10/11 64-bit and PCFish (Steam). The release download carries its own runtime, so it needs no separate .NET install. Building the controller needs the .NET 8 SDK; building the plugin needs the .NET 6 SDK or newer.
