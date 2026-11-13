#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Radio Commander Standalone Map Coordinate Reader (rc_map.py)
Queries /map on RadioBridge (127.0.0.1:8765) and outputs only map coordinates:
- bounds (min_x, max_x, min_y, max_y)
- waypoints (named landmarks and their grid coordinates)
- objectives (active objective ping coordinates)
- units (friendly unit positions in grid coordinates)
"""

import json
import os
import sys
import urllib.error
import urllib.request

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

BASE_URL = os.environ.get("RC_BRIDGE_URL", "http://127.0.0.1:8765")


def fetch_map(timeout: float = 6.0) -> dict:
    with urllib.request.urlopen(f"{BASE_URL}/map", timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main():
    try:
        data = fetch_map()
        print(json.dumps(data, ensure_ascii=False, indent=2))
    except urllib.error.HTTPError as e:
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": f"HTTP {e.code}: {e.reason}",
                    "hint": "If /map returned 404, restart Radio Commander so BepInEx loads the updated RadioBridge.dll.",
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        sys.exit(2)
    except urllib.error.URLError as e:
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": f"Cannot connect to RadioBridge at {BASE_URL}: {e}",
                    "hint": "Ensure Radio Commander is running.",
                },
                ensure_ascii=False,
                indent=2,
            )
        )
        sys.exit(2)


if __name__ == "__main__":
    main()
