#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Radio Commander Turn-Based CLI (rc_cli.py v3.0)
Core design:
- Turn-based loop: Game is ALWAYS paused (0x) while the model thinks.
- Only 3 core datasets: Mission Objectives (任务目标), Radio Logs (通讯记录), Dynamic Options (发起通讯选项).
- Local blocking sleep: When issuing an order or fast-forwarding, sleeps locally until the radio
  exchange completes or a new communication arrives, then automatically pauses (0x) and prompts
  the model for the next turn (preventing empty LLM polling spins).
"""

import argparse
import ctypes
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8")

BASE_URL = os.environ.get("RC_BRIDGE_URL", "http://127.0.0.1:8765")


def http_get_json(path: str, timeout: float = 6.0) -> dict:
    url = f"{BASE_URL}{path}"
    req = urllib.request.Request(url, method="GET")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read().decode("utf-8", errors="replace").strip()
        return json.loads(raw) if raw else {}


def http_send(params: dict, timeout: float = 6.0) -> dict:
    query = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None and str(v) != ""})
    url = f"{BASE_URL}/send?{query}"
    req = urllib.request.Request(url, method="GET")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read().decode("utf-8", errors="replace").strip()
        return json.loads(raw) if raw else {}


def get_status(limit: int = 5) -> dict:
    return http_get_json(f"/status?limit={limit}")


def pause_game() -> None:
    try:
        http_send({"speed": "0"})
    except Exception:
        pass


def wait_until_comm_finishes(start_logs: int, max_wait: float = 35.0) -> str:
    """
    Sleep locally while a radio transmission / unit reply / multi-line dialog is in progress.
    Returns the reason why waiting finished.
    """
    t0 = time.time()
    saw_busy_or_new_log = False

    while (time.time() - t0) < max_wait:
        time.sleep(0.35)
        st = get_status(limit=5)
        if st.get("dialog_open"):
            return "dialog_open"

        busy = bool(st.get("comm_busy"))
        total_logs = int(st.get("total_logs", 0))
        elapsed = time.time() - t0

        if busy or total_logs > start_logs:
            saw_busy_or_new_log = True

        # Wait at least 1.8s after command sent so unit's queuedMessage delayTime is registered,
        # then finish as soon as comm_busy drops to False and unit reply has arrived (total_logs >= start_logs + 2 or !busy)
        if elapsed >= 1.8 and not busy:
            # Double check after a short 0.5s settle interval in case a follow-up dialog line triggers
            time.sleep(0.5)
            st2 = get_status(limit=5)
            if st2.get("dialog_open"):
                return "dialog_open"
            if not st2.get("comm_busy"):
                return "comm_completed" if saw_busy_or_new_log else "idle"

    return "timeout"


def wait_for_next_comm_in_fast_forward(start_logs: int, start_objectives: list, timeout: float = 45.0) -> str:
    """
    In fast-forward mode (speed=2), sleep locally until any new communication starts,
    then wait at normal play speed (speed=1) for that communication to finish speaking, and return.
    """
    t0 = time.time()
    comm_triggered = False

    while (time.time() - t0) < timeout:
        time.sleep(0.35)
        st = get_status(limit=5)

        if st.get("dialog_open"):
            return "dialog_open"

        busy = bool(st.get("comm_busy"))
        total_logs = int(st.get("total_logs", 0))
        objs = st.get("objectives") or []

        if busy or total_logs > start_logs or objs != start_objectives:
            comm_triggered = True
            # Ensure 1x normal speed while listening to incoming radio call
            try:
                http_send({"speed": "1"})
            except Exception:
                pass
            break

    if comm_triggered:
        # Wait for the incoming speaker(s) to finish their lines
        finish_reason = wait_until_comm_finishes(start_logs, max_wait=35.0)
        return f"incoming_{finish_reason}"

    return "fast_forward_timeout"


def format_turn_output(action_desc: str, trigger: str, prev_total_logs: int | None = None, extra_error: dict | None = None) -> dict:
    """
    Ensure game is paused (0x), fetch current status, and format the turn-based output
    with only the 3 core sections + next step deployment prompt.
    """
    pause_game()
    st = get_status(limit=5)

    logs = st.get("logs") or []
    new_logs = []
    if prev_total_logs is not None:
        new_logs = [item for item in logs if int(item.get("index", 0)) > prev_total_logs]

    raw_time = st.get("time") or {}
    clean_time = {
        "datetime": raw_time.get("datetime"),
        "speed": raw_time.get("speed"),
    } if raw_time else None

    clean_objectives = [
        obj.get("text", "") if isinstance(obj, dict) else str(obj)
        for obj in (st.get("objectives") or [])
        if (obj.get("text") if isinstance(obj, dict) else obj)
    ]

    raw_options = dict(st.get("options") or {})
    dialog_open = bool(st.get("dialog_open"))
    if not dialog_open and not raw_options.get("dialog_options"):
        raw_options.pop("dialog_options", None)

    out = {
        "ok": extra_error is None,
        "time": clean_time,
        # 1. Mission Objectives
        "objectives": clean_objectives,
        # 2. Communication Logs
        "new_logs": [f"[{x.get('time', '')}] {x.get('text', '')}".strip() for x in new_logs],
        "recent_logs": [f"[{x.get('time', '')}] {x.get('text', '')}".strip() for x in logs[-5:]],
        # 3. Available Communication Options
        "options": raw_options,
        "next_step_prompt": (
            "【回合待命 | 游戏已自动暂停(0x)】请根据最新通讯记录与任务目标决定下一步部署：\n"
            "1. 继续指挥（发起通讯）：使用 send 命令下达部队指令（unit/group/order/param）或选择对话项（--dialog N），脚本将自动等待回电完成后再次暂停。\n"
            "2. 快进时间（等待通讯）：使用 fast 命令开启加速档，脚本将本地阻塞 sleep 直至收到下一条新通讯或目标变更后自动暂停。"
        ),
    }
    if dialog_open:
        out["dialog_open"] = True
    if extra_error:
        out.update(extra_error)
    return out


def parse_tokens_to_params(tokens: list[str], args) -> dict:
    params = {
        "unit": getattr(args, "unit", None),
        "group": getattr(args, "group", None),
        "order": getattr(args, "order", None),
        "param": getattr(args, "param", None),
        "dialog": getattr(args, "dialog", None),
        "speed": getattr(args, "speed", None),
    }

    positional = []
    for raw in tokens or []:
        t = raw.strip()
        if not t:
            continue
        if t.startswith("@"):
            t = t[1:]
        low = t.lower()
        if low in ("send", "on", "reset"):
            continue
        if low in ("fast", "faster"):
            params["speed"] = "2"
            continue
        if low == "pause":
            params["speed"] = "0"
            continue
        if low in ("play", "normal"):
            params["speed"] = "1"
            continue

        if ":" in t or "=" in t:
            sep = ":" if ":" in t else "="
            k, v = t.split(sep, 1)
            kl = k.strip().lower()
            vs = v.strip()
            if kl in ("unit", "group", "order", "dialog", "speed"):
                params[kl] = vs
            elif kl in ("param", "int"):
                params["param"] = f"{params['param']},{vs}" if params["param"] else vs
        else:
            positional.append(t)

    # Support shorthand positional order: <unit> <group> <order> [param]
    if positional and not params["unit"] and not params["dialog"]:
        if len(positional) >= 1:
            params["unit"] = positional[0]
        if len(positional) >= 2:
            params["group"] = positional[1]
        if len(positional) >= 3:
            params["order"] = positional[2]
        if len(positional) >= 4:
            params["param"] = ",".join(positional[3:])

    return params


def cmd_status(_args):
    st = get_status(limit=5)
    prev_logs = int(st.get("total_logs", 0))
    if st.get("comm_busy") and not st.get("dialog_open"):
        print("[等待中] 当前电台正在通话中，等待本轮语音结束 (sleep)...", file=sys.stderr, flush=True)
        wait_until_comm_finishes(prev_logs, max_wait=25.0)
    out = format_turn_output("status", "status_check", prev_total_logs=prev_logs)
    print(json.dumps(out, ensure_ascii=False, indent=2))


def press_space_briefly(duration: float = 0.2):
    """Hold Space briefly (0.2s) before fast-forwarding to prevent the game from getting stuck."""
    try:
        user32 = ctypes.windll.user32
        VK_SPACE = 0x20
        SCAN_SPACE = 0x39
        KEYEVENTF_KEYUP = 0x0002
        user32.keybd_event(VK_SPACE, SCAN_SPACE, 0, 0)
        time.sleep(duration)
        user32.keybd_event(VK_SPACE, SCAN_SPACE, KEYEVENTF_KEYUP, 0)
        time.sleep(0.05)
    except Exception:
        pass


def cmd_fast(args):
    st = get_status(limit=5)
    prev_logs = int(st.get("total_logs", 0))
    prev_objs = st.get("objectives") or []

    if st.get("dialog_open"):
        out = format_turn_output(
            "fast_aborted_dialog_open",
            "dialog_open",
            prev_total_logs=prev_logs,
            extra_error={"note": "当前存在待处理的对话选项 (dialog_open=true)，请先回复对话再快进。"},
        )
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return

    press_space_briefly(0.2)
    http_send({"speed": "2"})
    print(f"[等待中] 已开启加速档，本地 sleep 等待新通讯到来 (最长 {args.timeout}s)...", file=sys.stderr, flush=True)
    trigger = wait_for_next_comm_in_fast_forward(prev_logs, prev_objs, timeout=args.timeout)
    out = format_turn_output("fast_forward", trigger, prev_total_logs=prev_logs)
    print(json.dumps(out, ensure_ascii=False, indent=2))


def cmd_send(args):
    st_before = get_status(limit=5)
    prev_logs = int(st_before.get("total_logs", 0))
    prev_objs = st_before.get("objectives") or []

    # Wait if radio is currently mid-sentence before issuing a new command
    if st_before.get("comm_busy") and not st_before.get("dialog_open"):
        print("[等待中] 电台正忙，先等待当前语音结束...", file=sys.stderr, flush=True)
        wait_until_comm_finishes(prev_logs, max_wait=20.0)
        st_before = get_status(limit=5)
        prev_logs = int(st_before.get("total_logs", 0))

    params = parse_tokens_to_params(getattr(args, "tokens", []), args)

    # If user passed only speed=2 / fast via send/act, delegate to fast-forward wait
    if not params["unit"] and not params["dialog"] and str(params.get("speed")) in ("2", "fast", "faster"):
        args.timeout = getattr(args, "timeout", None) or 45.0
        cmd_fast(args)
        return

    res = http_send(params)
    if not res.get("ok"):
        out = format_turn_output(
            "send_failed",
            "validation_error",
            prev_total_logs=prev_logs,
            extra_error={"error": res.get("error"), "details": res},
        )
        print(json.dumps(out, ensure_ascii=False, indent=2))
        sys.exit(1)

    sent_desc = res.get("selected") or res.get("sent") or f"speed={res.get('speed')}"
    print(f"[等待中] 已发起通讯 ({sent_desc})，正在本地 sleep 等待通话与回电完成...", file=sys.stderr, flush=True)

    trigger = wait_until_comm_finishes(prev_logs, max_wait=args.timeout or 35.0)
    out = format_turn_output(f"send: {sent_desc}", trigger, prev_total_logs=prev_logs)
    print(json.dumps(out, ensure_ascii=False, indent=2))


def main():
    parser = argparse.ArgumentParser(description="Radio Commander Turn-Based CLI (v3.0)")
    sub = parser.add_subparsers(dest="cmd", required=False)

    # 1. status
    p_status = sub.add_parser("status", help="Pause game and read Objectives, Radio Logs, and Available Options")
    p_status.set_defaults(func=cmd_status)

    # 2. send / act (initiate communication & sleep until reply finishes)
    for name in ("send", "act"):
        p_send = sub.add_parser(name, help="Send radio order or dialog reply, sleep until reply completes, then pause")
        p_send.add_argument("tokens", nargs="*", help="Order tokens (e.g. alpha 报告 位置, or unit:alpha group:报告 order:位置)")
        p_send.add_argument("--unit", "-u", type=str, default=None, help="Unit name or 1-based index")
        p_send.add_argument("--group", "-g", type=str, default=None, help="Group name or 1-based index")
        p_send.add_argument("--order", "-o", type=str, default=None, help="Order name or 1-based index")
        p_send.add_argument("--param", "-p", type=str, default=None, help="Parameter(s), comma-separated")
        p_send.add_argument("--dialog", "-d", type=str, default=None, help="Dialog option 1-based index or text")
        p_send.add_argument("--speed", "-s", type=str, default=None, help="Optional speed override")
        p_send.add_argument("--wait", action="store_true", default=True, help="Wait for communication completion (enabled by default)")
        p_send.add_argument("--timeout", type=float, default=35.0, help="Max wait seconds (default: 35s)")
        p_send.set_defaults(func=cmd_send)

    # 3. fast (fast-forward speed=2 & sleep until next incoming communication)
    p_fast = sub.add_parser("fast", help="Fast-forward (speed=2) and sleep until next radio message arrives, then pause")
    p_fast.add_argument("--timeout", type=float, default=45.0, help="Max real-time seconds to fast-forward (default: 45s)")
    p_fast.set_defaults(func=cmd_fast)

    args = parser.parse_args()
    if not args.cmd:
        args.func = cmd_status

    try:
        args.func(args)
    except urllib.error.HTTPError as e:
        print(
            json.dumps(
                {
                    "ok": False,
                    "error": f"HTTP {e.code}: {e.reason}",
                    "hint": "If /status returned 404, please restart Radio Commander once so BepInEx loads the newly compiled RadioBridge v3.0 DLL.",
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
    except Exception as e:
        print(json.dumps({"ok": False, "error": str(e)}, ensure_ascii=False, indent=2))
        sys.exit(1)


if __name__ == "__main__":
    main()
