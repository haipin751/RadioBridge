---
name: radio-commander-bridge
description: Command the game Radio Commander in a strict turn-based loop through RadioBridge v3.0 (127.0.0.1:8765) and rc_cli.py. Reads only Mission Objectives, Radio Communication Logs, and Dynamic Command Options; blocks locally with sleep until radio exchanges complete and auto-pauses the game.
---

# Radio Commander Turn-Based Bridge (v3.0)

- Bridge URL: `http://127.0.0.1:8765` (override via `RC_BRIDGE_URL`)
- CLI Script: `scripts/rc_cli.py`
- Map Script: `scripts/rc_map.py`

## Turn-Based Rules
1. **Auto-Paused Thinking**: Every `rc_cli.py` command automatically pauses the game (`speed=0`) before returning JSON.
2. **Three Core Outputs**:
   - `objectives`: Active localized mission objectives.
   - `new_logs` / `recent_logs` (last 5): Timestamped radio communication logs.
   - `options`: Available keywords (`units`, `groups`, `orders`, `params`, `dialog_options`). Keywords are localized to the active game language—always match values from `options` (or 1-based indices).
3. **Zero-Spin Blocking Sleep**: Never poll in a loop across turns. `send` and `fast` sleep locally until the radio exchange finishes or an incoming call completes, then pause (`speed=0`) and prompt for the next decision.
4. **Two Playback Speeds**: The game has only two running speeds (`1` = normal play, `2` = fast-forward), plus `0` = pause.

## Commands

### 1. Read Map Coordinates (`rc_map.py`)
At the start of a mission (and whenever checking coordinates), run the standalone map program to read map bounds, waypoint coordinates, objective ping coordinates, and friendly unit coordinates. Always issue `Move` orders using grid coordinates (`X,Y`):
```bash
python scripts/rc_map.py
```

### 2. Read Current Turn Status (`status`)
Pauses the game (waiting first if a radio call is mid-sentence) and returns objectives, the last 5 logs, and available options:
```bash
python scripts/rc_cli.py status
```

### 3. Send Radio Order or Dialog Reply (`send`)
Issues a unit command or dialog reply at normal speed (`1`), sleeps locally until both Commander and the unit finish speaking (`comm_busy == false`) or a dialog opens, then pauses (`0`):
```bash
# Shorthand positional: <unit> <group> <order> [param]
python scripts/rc_cli.py send alpha Report Position
python scripts/rc_cli.py send bravo Report Contacts
python scripts/rc_cli.py send alpha Order Move Village

# Move to grid coordinates (comma-separated X,Y)
python scripts/rc_cli.py send alpha Order Move 245,927

# Explicit flags (use quotes if an option contains spaces, e.g. "Fight status")
python scripts/rc_cli.py send --unit alpha --group Order --order Move --param "245,927"

# Reply to an open dialog prompt (when dialog_open is true; 1-based index)
python scripts/rc_cli.py send --dialog 1
```

### 4. Fast-Forward Until Next Communication (`fast`)
Switches to fast-forward (`speed=2`), sleeps locally until a unit initiates a call or objectives change, drops to normal play (`speed=1`) until the incoming exchange completes, then pauses (`0`):
```bash
python scripts/rc_cli.py fast
```
