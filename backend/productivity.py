"""Productivity engine for ULTRON: builds real Office documents and runs a
persistent task inbox.

- create_document: renders structured input into a .docx / .xlsx / .pptx saved
  under the user's Documents folder (or a given path).
- task inbox: a durable local task list (JSON) with add / list / complete.
"""

from __future__ import annotations

import json
import os
from datetime import datetime


def _documents_root() -> str:
    base = os.path.join(os.path.expanduser("~"), "Documents", "Ultron")
    return base


def _inbox_path() -> str:
    base = os.environ.get("LOCALAPPDATA", ".")
    return os.path.join(base, "Ultron", "tasks.json")


# ---------------------------------------------------------------- documents

def create_document(category: str, output: str, data: dict) -> str:
    category = (category or "").lower().strip()
    root = _documents_root()
    os.makedirs(root, exist_ok=True)
    if not output or not output.strip():
        stamp = datetime.now().strftime("%Y-%m-%d_%H%M")
        ext = {".docx": "docx", ".xlsx": "xlsx", ".pptx": "pptx"}.get("." + category[:0])
        default_ext = {"word": "docx", "excel": "xlsx", "powerpoint": "pptx"}.get(category, "docx")
        output = f"{category}_{stamp}.{default_ext}"
    output = output.strip()
    if not output.lower().endswith((".docx", ".xlsx", ".pptx")):
        ext = {"word": "docx", "excel": "xlsx", "powerpoint": "pptx"}.get(category, "docx")
        output = output.rstrip(".") + "." + ext
    path = output if os.path.dirname(output) else os.path.join(root, output)
    os.makedirs(os.path.dirname(path), exist_ok=True)

    if category in ("word", "document", "doc"):
        return _build_word(path, data)
    if category in ("excel", "xlsx", "spreadsheet"):
        return _build_excel(path, data)
    if category in ("powerpoint", "ppt", "slides", "pptx"):
        return _build_ppt(path, data)
    raise ValueError(f"Unknown document category '{category}' — use word, excel, or powerpoint.")


def _build_word(path: str, data: dict) -> str:
    try:
        import docx
    except ImportError:
        raise RuntimeError("python-docx is not installed.")
    doc = docx.Document()
    title = data.get("title") or "ULTRON Document"
    doc.add_heading(title, level=0)
    for section in data.get("sections", data.get("content", [])):
        if isinstance(section, dict):
            doc.add_heading(str(section.get("heading", "")), level=1)
            paras = section.get("paragraphs", section.get("points", []))
            if isinstance(paras, str):
                paras = [paras]
            for p in paras:
                if isinstance(p, dict):
                    doc.add_paragraph(str(p.get("text", "")))
                else:
                    doc.add_paragraph(str(p))
        elif isinstance(section, str):
            doc.add_paragraph(section)
    body = data.get("body")
    if isinstance(body, str) and not data.get("sections") and not data.get("content"):
        for line in body.splitlines():
            doc.add_paragraph(line)
    doc.save(path)
    return f"Created Word document: {path}"


def _build_excel(path: str, data: dict) -> str:
    try:
        import openpyxl
    except ImportError:
        raise RuntimeError("openpyxl is not installed.")
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = (str(data.get("sheet", "Sheet1"))[:31])
    title = data.get("title")
    headers = data.get("headers")
    rows = data.get("rows", [])
    r = 1
    if title:
        ws.cell(row=r, column=1, value=str(title))
        r += 1
    if headers:
        for c, h in enumerate(headers, start=1):
            ws.cell(row=r, column=c, value=str(h))
        r += 1
    for row in rows:
        if isinstance(row, dict):
            vals = [row.get(h) if isinstance(h, str) else None for h in (headers or [])]
            if not headers:
                vals = list(row.values())
        elif isinstance(row, (list, tuple)):
            vals = list(row)
        else:
            vals = [row]
        for c, v in enumerate(vals, start=1):
            ws.cell(row=r, column=c, value=v)
        r += 1
    wb.save(path)
    return f"Created Excel workbook: {path}"


def _build_ppt(path: str, data: dict) -> str:
    try:
        from pptx import Presentation
        from pptx.util import Inches
    except ImportError:
        raise RuntimeError("python-pptx is not installed.")
    prs = Presentation()
    title = data.get("title") or "ULTRON Presentation"
    slides = data.get("slides", data.get("content", []))
    if not slides:
        slides = [{"title": title, "bullets": data.get("bullets", [])}]
    for i, s in enumerate(slides):
        if isinstance(s, str):
            s = {"title": f"Slide {i + 1}", "bullets": [s]}
        layout = prs.slide_layouts[1]
        slide = prs.slides.add_slide(layout)
        slide.shapes.title.text = str(s.get("title", f"Slide {i + 1}"))
        body = slide.placeholders[1].text_frame
        bullets = s.get("bullets", s.get("points", []))
        if isinstance(bullets, str):
            bullets = [bullets]
        first = True
        for b in bullets:
            p = body.paragraphs[0] if first else body.add_paragraph()
            first = False
            p.text = str(b)
    prs.save(path)
    return f"Created PowerPoint deck: {path}"


# ---------------------------------------------------------------- task inbox

def _load_inbox() -> list[dict]:
    try:
        with open(_inbox_path(), "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, list) else []
    except Exception:
        return []


def _save_inbox(tasks: list[dict]) -> None:
    os.makedirs(os.path.dirname(_inbox_path()), exist_ok=True)
    with open(_inbox_path(), "w", encoding="utf-8") as f:
        json.dump(tasks, f, indent=2)


def inbox_add(task: str, due: str) -> str:
    tasks = _load_inbox()
    tasks.append({"id": len(tasks) + 1, "task": task, "due": due, "done": False})
    _save_inbox(tasks)
    return f"Task #{tasks[-1]['id']} added to your inbox" + (f" (due {due})" if due else "") + "."


def inbox_list() -> str:
    tasks = _load_inbox()
    open_tasks = [t for t in tasks if not t.get("done")]
    if not open_tasks:
        return "Your task inbox is empty."
    lines = [f"Open tasks ({len(open_tasks)}):"]
    for t in open_tasks:
        due = f", due {t['due']}" if t.get("due") else ""
        lines.append(f"  #{t['id']} — {t['task']}{due}")
    return "\n".join(lines)


def inbox_complete(id_: str) -> str:
    tasks = _load_inbox()
    for t in tasks:
        if str(t.get("id")) == str(id_):
            t["done"] = True
            _save_inbox(tasks)
            return f"Task #{id_} marked complete."
    return f"No open task #{id_} found. Current tasks:\n{inbox_list()}"


def inbox_clear() -> str:
    _save_inbox([])
    return "Task inbox cleared."


def inbox_due() -> list[str]:
    """Tasks that are overdue (due time passed) or not done. Pure read for the
    guardian so it can raise them aloud."""
    tasks = _load_inbox()
    now = datetime.now()
    due_list: list[str] = []
    for t in tasks:
        if t.get("done"):
            continue
        due = str(t.get("due") or "").strip()
        if not due:
            continue
        if _due_passed(due, now):
            due_list.append(f"#{t['id']} — {t['task']} (due {due})")
    return due_list


def _due_passed(due: str, now: datetime) -> bool:
    txt = due.lower()
    try:
        if txt in ("now", "asap", "today"):
            return True
        for fmt in ("%Y-%m-%d", "%Y-%m-%d %H:%M", "%m/%d/%Y", "%m/%d/%Y %H:%M"):
            try:
                return now >= datetime.strptime(due, fmt)
            except ValueError:
                continue
    except Exception:
        return False
    return False