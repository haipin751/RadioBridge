# RadioBridge v3.0

**RadioBridge** is a lightweight **BepInEx 5** plugin and **turn-based Python CLI** for the tactical strategy game *Radio Commander*. It exposes a local HTTP bridge (`127.0.0.1:8765`) inside the game and provides blocking CLI tools so humans or AI agents can command platoons purely through radio communications, mission objectives, and map coordinates in a strict turn-based loop.

> **Disclaimer**: This repository contains **only** original mod source code, Python control scripts, and documentation. **No original game files, assemblies, or assets are included.**

---

## Key Features

- **Strict Turn-Based Control (`0x` / `1x` / `2x`)**:
  - Automatically pauses the game (`speed=0`) whenever returning state to the caller, so an AI model or player never loses game time while thinking.
  - Plays at `1x` (`speed=1`) when issuing radio orders (`send`) and sleeps locally in Python until both the Commander and the receiving unit finish speaking (`comm_busy == false` or `dialog_open == true`).
  - Fast-forwards (`speed=2`) via `fast`, sleeps locally until a new radio transmission or objective change occurs, drops to `1x` to let the exchange finish, then auto-pauses (`speed=0`).
- **Minimal, Reliable State Surface**:
  - **Objectives (`objectives`)**: Active mission objectives read from `Radio.ObjectivesController`.
  - **Radio Logs (`recent_logs` / `new_logs`)**: Timestamped radio history read directly from `Radio.RadioController.GiveLogTexts()`.
  - **Dynamic Options (`options`)**: Localized available `units`, `groups`, `orders`, `params`, and `dialog_options` matching the active game language.
  - **Map Coordinates (`rc_map.py`)**: Standalone `/map` reader returning map bounds, named waypoints, objective ping coordinates, and friendly unit grid positions.
- **Zero External Python Dependencies**: `rc_cli.py` and `rc_map.py` use only the Python standard library.

---

## Repository Structure

```text
RadioBridge/
├── src/
│   └── RadioBridge.cs          # BepInEx 5 single-file C# plugin (HTTP server on 127.0.0.1:8765)
├── scripts/
│   ├── rc_cli.py               # Turn-based blocking CLI (status, send, fast)
│   └── rc_map.py               # Standalone map coordinate reader (/map)
├── skill/
│   └── SKILL.md                # Optional AI Agent skill definition
├── examples/
│   └── mission1_test_logs.md   # Example deduplicated radio transcript from a Mission 1 run
├── build.ps1                   # PowerShell build & deploy script (uses your local game DLLs)
├── LICENSE                     # MIT License
└── README.md
```

---

## Prerequisites

1. **Radio Commander** (Windows PC version)
2. **BepInEx 5 (x64)** installed in your *Radio Commander* game directory
3. **Python 3.8+** (for `scripts/rc_cli.py` and `scripts/rc_map.py`)
4. **.NET Framework 4.x** (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, pre-installed on Windows) to compile `src/RadioBridge.cs`

---

## Build & Installation

1. Install **BepInEx 5 (x64)** into your *Radio Commander* installation folder and launch the game once so `BepInEx/plugins` is generated.
2. Clone this repository and run `build.ps1` pointing to your local game folder:

```powershell
# Compile only (outputs to ./build/RadioBridge.dll)
.\build.ps1 -GameDir "D:\Games\Radio Commander"

# Compile and deploy directly to <GameDir>\BepInEx\plugins\RadioBridge.dll
.\build.ps1 -GameDir "D:\Games\Radio Commander" -Deploy
```

3. Launch *Radio Commander* and enter a mission.
   - **Important**: Make sure the **in-game radio switch is turned ON** in the command tent UI; otherwise the game's `RadioController` blocks outgoing/incoming radio transmissions.

---

## Usage (`rc_cli.py` & `rc_map.py`)

Both scripts default to `http://127.0.0.1:8765` (override via the `RC_BRIDGE_URL` environment variable).

### 1. Read Map Coordinates
```bash
python scripts/rc_map.py
```
Returns JSON with `bounds`, `waypoints`, `objectives` (ping coordinates), and `units` (friendly unit grid coordinates).

### 2. Check Current Status (Auto-Pauses)
```bash
python scripts/rc_cli.py status
```
Pauses the game (waiting briefly if a radio call is mid-sentence) and returns current `objectives`, `recent_logs`, `dialog_open`, `comm_busy`, and localized `options`.

### 3. Send a Radio Order or Reply to a Dialog
Always match keywords from `options` (localized to your game language) or pass 1-based indices:

```bash
# Shorthand positional syntax: <unit> <group> <order> [param]
python scripts/rc_cli.py send alpha Report Position
python scripts/rc_cli.py send alpha Order Move 245,927

# Explicit flags (quote options containing spaces)
python scripts/rc_cli.py send --unit alpha --group Order --order Move --param "245,927"

# Reply to an active scripted dialog prompt (1-based index)
python scripts/rc_cli.py send --dialog 1
```

### 4. Fast-Forward Until Next Communication
```bash
python scripts/rc_cli.py fast
```
Switches the game to `2x` speed, sleeps locally until a unit initiates a call or objectives change, drops back to `1x` until the exchange finishes, and auto-pauses at `0x`.

---

## HTTP Bridge Endpoints (`127.0.0.1:8765`)

| Endpoint | Description |
| :--- | :--- |
| `GET /` or `/help` | Basic bridge help/endpoint info |
| `GET /status` (alias `/state`) | Returns mission objectives, recent radio logs, `comm_busy`, `dialog_open`, and dynamic `options` |
| `GET /send` (alias `/say`) | Executes a radio order (`unit`, `group`, `order`, `param`), `dialog` reply, or playback `speed` (`0`, `1`, `2`) |
| `GET /map` | Returns map grid bounds, waypoints, objective ping coordinates, and friendly unit coordinates |

---

## License

This project is licensed under the [MIT License](LICENSE).
