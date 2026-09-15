"""Autonomous desktop agent for ULTRON ("computer use").

Sees the screen, asks a vision-capable model what to do next, executes the
action through the C# input layer, and iterates until the task is done or the
step budget is exhausted. Deliberately self-contained (its own generate_content
calls) so it never depends on Live-session turn semantics.
"""

from __future__ import annotations

import asyncio
import json
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


def _parse_action(text: str) -> dict | None:
    m = re.search(r"\{.*\}", text or "", re.S)
    if not m:
        return None
    try:
        obj = json.loads(m.group(0))
    except json.JSONDecodeError:
        return None
    if not isinstance(obj, dict):
        return None
    return obj


def _build_prompt(task: str, history: list[tuple[str, str]], step: int, steps: int) -> str:
    lines = [ACTION_VOCABULARY, f"\nTASK: {task}", f"STEP {step} of {steps}. Image attached is the current screen."]
    if history:
        log = "\n".join(f"  step {i}: I sent {json.dumps(a)} -> result: {r[:200]}" for i, (a, r) in enumerate(history, 1))
        lines.append("SO FAR:\n" + log)
    lines.append("Decide the single next action and reply with exactly one JSON object.")
    return "\n".join(lines)


async def run_computer_use(client, model: str, task: str, act_async, steps: int = 12, screenshot=None) -> str:
    """act_async(action: dict) -> awaitable[str] — executes the raw action."""
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