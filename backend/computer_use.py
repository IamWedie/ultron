"""Autonomous desktop agent for ULTRON ("computer use").

Sees the screen, asks a vision-capable model what to do next, executes the
action through the C# input layer, and iterates until the task is done or the
step budget is exhausted. Deliberately self-contained (its own generate_content
calls) so it never depends on Live-session turn semantics.
"""

from __future__ import annotations

import asyncio
import json
import math
import re

ACTION_VOCABULARY = """You control a Windows desktop by outputting ACTION (json only).

Available actions:
  {"type":"click","x":int,"y":int}           left click at screen pixel (x,y)
  {"type":"double_click","x":int,"y":int}
  {"type":"right_click","x":int,"y":int}
  {"type":"move","x":int,"y":int}            move the mouse (no click)
  {"type":"scroll","amount":int}             wheel notches (positive = down)
  {"type":"type","text":"..."}               type text into the focused window
  {"type":"press","keys":"ctrl+a"}           shortcut/media/plain key
  {"type":"done","summary":"...one line..."} FINISH the task

Rules:
- You see ONE screenshot per step. Look before you act, then act, then look again.
- Coordinates are pixel positions in the image you were shown.
- Prefer keyboard shortcuts (Win, Alt+F4, ctrl+s...) over mouse when faster.
- If a window needs typing, sometimes you must click its field first.
- Be careful and minimal: one small action per step; verify after each action.
- When the task is complete (or truly impossible), STOP with {"type":"done","summary":...}.
- Output ONLY one JSON object, no markdown, no extra text.
"""


def validate_action(action: dict) -> dict:
    if not isinstance(action, dict):
        raise ValueError("action must be an object")
    kind = action.get("type")
    if kind not in {"click", "double_click", "right_click", "move", "scroll", "type", "press", "done"}:
        raise ValueError("unsupported action type")
    if kind in {"click", "double_click", "right_click", "move"}:
        for name in ("x", "y"):
            value = action.get(name)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
                raise ValueError(f"{name} must be a finite number")
            if value < 0 or value > 10000:
                raise ValueError(f"{name} is outside the allowed range")
    elif kind == "scroll":
        value = action.get("amount")
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError("scroll amount must be finite")
        if value < -100 or value > 100:
            raise ValueError("scroll amount is outside the allowed range")
    elif kind == "type":
        value = action.get("text")
        if not isinstance(value, str) or len(value) > 4096:
            raise ValueError("type text is too long")
    elif kind == "press":
        value = action.get("keys")
        if not isinstance(value, str) or not value or len(value) > 128:
            raise ValueError("press keys are invalid")
    elif kind == "done":
        value = action.get("summary", "")
        if not isinstance(value, str) or len(value) > 500:
            raise ValueError("done summary is too long")
    return action


def _parse_action(text: str) -> dict | None:
    match = re.search(r"\{", text or "")
    if not match:
        return None
    try:
        value, end = json.JSONDecoder().raw_decode(text[match.start():])
    except json.JSONDecodeError:
        return None
    if text[match.start() + end:].strip():
        return None
    return value if isinstance(value, dict) else None


def _build_prompt(task: str, history: list[tuple[str, str]], step: int, steps: int) -> str:
    lines = [ACTION_VOCABULARY, f"\nTASK: {task}", f"STEP {step} of {steps}. Image attached is the current screen."]
    if history:
        log = "\n".join(f"  step {i}: I sent {json.dumps(a)} -> result: {r[:200]}" for i, (a, r) in enumerate(history, 1))
        lines.append("SO FAR:\n" + log)
    lines.append("Decide the single next action and reply with exactly one JSON object.")
    return "\n".join(lines)


async def run_computer_use(client, model: str, task: str, act_async, steps: int = 12, screenshot=None) -> str:
    """act_async(action: dict) -> awaitable[str] — executes the raw action."""
    task = str(task or "").strip()
    if not task or len(task) > 2000:
        return "Computer task is empty or too long."
    steps = max(3, min(int(steps), 16))
    history: list[tuple[str, str]] = []
    shot_fn = screenshot or (lambda: _capture_screen_default())

    for step in range(1, steps + 1):
        try:
            img, mime = await asyncio.to_thread(shot_fn)
        except Exception as e:
            return f"Could not capture the screen: {e}"
        b64 = base64_b64encode(img)

        prompt = _build_prompt(task, history, step, steps)
        try:
            resp = await asyncio.to_thread(
                client.models.generate_content,
                model=model,
                contents=[
                    {"inline_data": {"mime_type": mime, "data": b64}},
                    prompt,
                ],
            )
            text = resp.text or ""
        except Exception as e:
            return f"Vision model call failed on step {step}: {e}"

        action = _parse_action(text)
        if action is None:
            return f"Model output was not a usable action (step {step}). Last text: {text[:200]}"

        try:
            action = validate_action(action)
        except ValueError as e:
            return f"Invalid computer action: {e}"

        if action.get("type") == "done":
            return str(action.get("summary", "Task complete."))

        try:
            result = await act_async(action)
        except Exception as e:
            result = f"EXECUTION ERROR: {e}"
        history.append((action, result))

    return ("Stopped because the step budget ran out without a 'done' answer. "
            + "Complete the task conceptually: last action result was: "
            + (history[-1][1] if history else "n/a"))


def base64_b64encode(b: bytes) -> str:
    import base64
    return base64.b64encode(b).decode("ascii")


def _capture_screen_default():
    raise RuntimeError("screenshot not configured")