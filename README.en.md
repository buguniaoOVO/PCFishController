<p align="center">
  <img src="docs/afdian-support.jpg" alt="Support Awan on Afdian" width="280">
  <a href="https://github.com/buguniaoOVO/PCFishController"><img src="docs/star-support.png" alt="Give PCFish Assistant a Star" width="280"></a>
</p>

# PCFish Controller

A background assistant for the idle fish-breeding game PCFish. It runs the breeding loop and the tank upgrades for you, while the game window sits minimized or behind other windows.

## How it is built

| Component | Role |
|---|---|
| `pcfish-autohelper/` | BepInEx plugin. Opens a loopback channel on `127.0.0.1:27777` inside the game process. |
| `PCFishController/` | Standalone desktop app. Shows status and sends actions. |

Breeding goes through the game's own data and network entry points. The controller is a plain WinForms program; closing it leaves the game running.

## What it does

- Runs with no simulated input, so you keep using your mouse and keyboard while it works
- Every 15-30 minutes (configurable), reads the breeding counter and breeds until it reaches zero, then waits for the next check
- Picks fish by your goal route, or by rarity and stars; skips fish on cooldown, out of breed uses, locked, or displayed
- Skips direct parent-child pairs, and avoids combinations the server rejected
- Keeps at least 60 seconds between actions, enforced by the plugin and the controller
- Verifies breeding results against parent counts and the returned new fish ID
- Reads your remaining breed counts from the server before each round, to filter out a stale local cache
- Tracks delayed replies and checks fish changes before resuming a timed-out request

## Interface

Overview, Auto Breeding, Warehouse, Synthesis Route, Logs and Settings. The Warehouse lists species, rarity, stars, remaining breeds and cooldown, with search, filters and sortable columns. Language switches between Chinese and English. The close button can ask each time, exit the assistant, or minimize it to the system tray.

## One-click setup

The Settings button locates the game folder, installs the BepInEx 6 runtime when missing, copies the plugin into `BepInEx\plugins`, starts the game once to write the config file, then turns on the in-game action switch. Progress prints as it runs.

## Safety

The channel binds to `127.0.0.1` only. The in-game switch that allows external actions is off by default, and the controller has its own ARM toggle. On an error the assistant stops the current action, and the logs hold no authentication tokens.

## Requirements

Windows 10/11 64-bit and PCFish (Steam). The release download carries its own runtime, so it needs no separate .NET install. Building the controller needs the .NET 8 SDK; building the plugin needs the .NET 6 SDK or newer.
