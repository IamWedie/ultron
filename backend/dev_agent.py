"""
dev_agent — small agent for scaffolding, running and fixing tiny projects.

Cost control: the model writes the whole project in ONE response (all files as
a JSON array), then the agent runs the entry point, captures the output, and
sends errors back for at most 2 fix passes. Everything is confined to
C:\\Users\\<user>\\Desktop\\dev_projects\\<project>.
"""

from __future__ import annotations

import asyncio
import json
import os
import re
import subprocess
import time
from pathlib import Path

from google import genai
from google.genai import types


def _slug(name: str) -> str:
    s = re.sub(r"[^A-Za-z0-9_-]+", "_", name.strip()).strip("_")
    return (s or "app").lower()


def _project_root(name: str) -> Path:
    root = Path(os.path.expanduser("~")) / "Desktop" / "dev_projects"
    return root / _slug(name)


def _extract_json(text: str):
    cleaned = text.strip()
    if cleaned.startswith("```"):
        cleaned = re.sub(r"^```[A-Za-z]*\s*", "", cleaned)
        cleaned = re.sub(r"\s*```$", "", cleaned)
    return json.loads(cleaned)


def _detect_entry(dirp: Path):
    for cand in ("main.py", "index.py", "app.py", "index.js", "main.js", "package.json"):
        if (dirp / cand).exists():
            return cand
    # Any top-level runnable
    for cand in sorted(dirp.glob("*.py")):
        return cand.name
    for cand in sorted(dirp.glob("*.js")):
        return cand.name
    return None


def _run(dirp: Path, entry: str, timeout: float = 40.0) -> tuple[int, str]:
    cmd = []
    if entry.endswith(".py"):
        cmd = [sys_executable(), str(dirp / entry)]
    elif entry.endswith(".js") or entry == "package.json":
        cmd = ["node", str(dirp / entry)]
    else:
        return (2, f"No runner for {entry}")
    try:
        r = subprocess.run(cmd, cwd=str(dirp), capture_output=True,
                           text=True, timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)
        tail_out = (r.stdout or "").strip()[-4000:]
        tail_err = (r.stderr or "").strip()[-4000:]
        return r.returncode, (tail_out + "\n" + tail_err).strip()
    except subprocess.TimeoutExpired:
        return (1, "Timed out after {}s".format(int(timeout)))
    except Exception as e:
        return (2, str(e))


def sys_executable() -> str:
    for cand in (os.environ.get("PYTHON312", ""),
                 os.path.join(os.environ.get("LOCALAPPDATA", ""),
                              "Programs", "Python", "Python312", "python.exe"),
                 os.path.join(os.environ.get("LOCALAPPDATA", ""),
                              "Programs", "Python", "Python311", "python.exe"),
                 "python"):
        if cand and Path(cand).exists():
            return cand
    return "python"


async def run_dev_agent(client: genai.Client, task: str, name: str = "app",
                        max_fix_passes: int = 2) -> str:
    dirp = _project_root(name)
    dirp.mkdir(parents=True, exist_ok=True)
    colors = ["Made", "Created", "Built"][len(list(dirp.iterdir())) % 3]

    system = (
        "You are a senior software engineer scaffolding a small program based "
        "on the user's one-line idea. Keep it small, dependency-free, and "
        "complete enough to actually run. Reply with ONLY a JSON array of "
        "objects: [{\"path\": \"main.py\", \"content\": \"...\"}, ...]. "
        "No markdown fences, no commentary. Escape the content as JSON. "
        "Include a runnable entry file. Prefer Python unless told otherwise. "
        "Keep combined size small (one file is ideal for tiny ideas)."
    )

    def do_call(prompt: str) -> str:
        resp = client.models.generate_content(
            model="gemini-2.5-flash",
            contents=prompt,
            config=types.GenerateContentConfig(
                temperature=0.3,
                response_modalities=["TEXT"],
                system_instruction=system,
            ),
        )
        return resp.text or ""

    # Pass 0 — generate the project
    written = {}
    for attempt_i in range(3):
        try:
            body = _extract_json(do_call(task))
            for item in body:
                p = item.get("path", "")
                content = str(item.get("content", ""))
                if not p or ".." in p or p.lstrip("\\/").startswith(("\\", "/")):
                    continue
                fp = dirp / p.replace("\\", "/")
                try:
                    fp.resolve().relative_to(dirp.resolve())
                except Exception:
                    continue
                fp.parent.mkdir(parents=True, exist_ok=True)
                fp.write_text(content, encoding="utf-8")
                written[p] = len(content)
            if written:
                break
        except Exception:
            continue
    if not written:
        return "dev_agent could not scaffold the project (model output parse failed)."

    entry = _detect_entry(dirp)
    if entry is None:
        return f"Scaffolded {len(written)} file(s) to {dirp} but found no runnable entry point."

    lines = [f"{colors} a project at {dirp} ({len(written)} files, {sum(written.values()):,} chars)," f" entry={entry}."]
    rc, out = _run(dirp, entry)
    lines.append(f"Run result: exit={rc}.")
    if rc != 0 and out.strip():
        lines.append("Output:\n" + out[:1500])

    # Fix passes
    fix_prompt = (
        f"The program in {dirp} (entry {entry}) failed to run:\n\n{out[:3000]}"
        "\n\nRewrite the full project as the same JSON array of {path, content} "
        "with the bug fixed. Reply with ONLY the JSON array."
    )
    for pass_i in range(max_fix_passes):
        if rc == 0:
            break
        try:
            body = _extract_json(do_call(fix_prompt))
        except Exception:
            break
        for item in body:
            p = item.get("path", "")
            content = str(item.get("content", ""))
            if not p or ".." in p:
                continue
            fp = dirp / p.replace("\\", "/")
            try:
                fp.resolve().relative_to(dirp.resolve())
            except Exception:
                continue
            fp.parent.mkdir(parents=True, exist_ok=True)
            fp.write_text(content, encoding="utf-8")
            written[p] = len(content)
        rc, out = _run(dirp, entry)
        lines.append(f"Fix pass {pass_i + 1}: exit={rc}.")
        if out.strip():
            lines.append("Output:\n" + out[:1500])

    final = f"OK — runs cleanly." if rc == 0 else f"Last run failed (exit {rc})."
    lines.append(final)
    lines.append("Say the project name to hear more — details are on the transcript.")
    return "\n".join(lines)


# for future use by callers that want a plain callable
if __name__ == "__main__":
    print("module only")