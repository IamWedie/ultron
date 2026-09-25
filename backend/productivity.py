"""Productivity engine for ULTRON: builds real Office documents and runs a
persistent task inbox.

- create_document: renders structured input into a .docx / .xlsx / .pptx saved
  under the user's Documents folder (or a given path).
- task inbox: a durable local task list (JSON) with add / list / complete.
"""

from __future__ import annotations

import json
import os
import tempfile
from datetime import datetime, timedelta

import json_store


def _documents_root() -> str:
    base = os.path.join(os.path.expanduser("~"), "Documents", "Ultron")
    return base


def _safe_document_path(root: str, output: str) -> str:
    if not output or "\x00" in output or os.path.isabs(output):
        raise ValueError("Document output must be a relative filename under Documents/Ultron.")
    if any(part == ".." for part in output.replace("\\", "/").split("/")):
        raise ValueError("Document output cannot leave Documents/Ultron.")
    root_abs = os.path.abspath(root)
    path = os.path.abspath(os.path.join(root_abs, output))
    real_root = os.path.realpath(root_abs)
    real_path = os.path.realpath(path)
    try:
        contained = os.path.commonpath([real_root, real_path]) == real_root
    except ValueError:
        contained = False
    if not contained or real_path == real_root:
        raise ValueError("Document output must remain under Documents/Ultron.")
    current = root_abs
    for part in path[len(root_abs):].strip(os.path.sep).split(os.path.sep):
        if not part:
            continue
        current = os.path.join(current, part)
        if os.path.islink(current):
            raise ValueError("Document output cannot use a symlink or junction.")
    return path


def _inbox_path() -> str:
    base = os.environ.get("LOCALAPPDATA", ".")
    return os.path.join(base, "Ultron", "tasks.json")


# ---------------------------------------------------------------- documents

def _build_document_atomic(path: str, category: str, data: dict) -> str:
    directory = os.path.dirname(path)
    suffix = os.path.splitext(path)[1]
    fd, temporary = tempfile.mkstemp(prefix=".ultron-", suffix=suffix, dir=directory)
    os.close(fd)
    os.remove(temporary)
    try:
        if category in ("word", "document", "doc"):
            result = _build_word(temporary, data)
        elif category in ("excel", "xlsx", "spreadsheet"):
            result = _build_excel(temporary, data)
        elif category in ("powerpoint", "ppt", "slides", "pptx"):
            result = _build_ppt(temporary, data)
        else:
            raise ValueError(f"Unknown document category '{category}' — use word, excel, or powerpoint.")
        os.replace(temporary, path)
        return result.replace(temporary, path)
    finally:
        try:
            os.remove(temporary)
        except FileNotFoundError:
            pass


def _validate_document_data(data: dict) -> None:
    if not isinstance(data, dict):
        raise ValueError("Document data must be an object.")
    if len(json.dumps(data, ensure_ascii=False, default=str)) > 1_000_000:
        raise ValueError("Document data is too large.")
    for key, limit in (("sections", 200), ("rows", 5000), ("slides", 200)):
        value = data.get(key)
        if isinstance(value, list) and len(value) > limit:
            raise ValueError(f"Document {key} exceeds the size limit.")


def create_document(category: str, output: str, data: dict) -> str:
    _validate_document_data(data)
    category = (category or "").lower().strip()
    root = _documents_root()
    os.makedirs(root, exist_ok=True)
    auto_named = not output or not output.strip()
    if auto_named:
        stamp = datetime.now().strftime("%Y-%m-%d_%H%M")
        default_ext = {"word": "docx", "excel": "xlsx", "powerpoint": "pptx"}.get(category, "docx")
        output = f"{category}_{stamp}.{default_ext}"
    output = output.strip()
    if not output.lower().endswith((".docx", ".xlsx", ".pptx")):
        ext = {"word": "docx", "excel": "xlsx", "powerpoint": "pptx"}.get(category, "docx")
        output = output.rstrip(".") + "." + ext
    path = _safe_document_path(root, output)
    if auto_named:
        stem, extension = os.path.splitext(path)
        suffix = 2
        while os.path.exists(path):
            path = f"{stem}-{suffix}{extension}"
            suffix += 1
    elif os.path.exists(path):
        raise FileExistsError(f"Document already exists: {path}")
    os.makedirs(os.path.dirname(path), exist_ok=True)

    return _build_document_atomic(path, category, data)


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
            if isinstance(v, str) and v[:1] in ("=", "+", "-", "@"):
                v = "'" + v
            ws.cell(row=r, column=c, value=v)
        r += 1
    wb.save(path)
    return f"Created Excel workbook: {path}"


def _build_ppt(path: str, data: dict) -> str:
    try:
        from pptx import Presentation
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
    return json_store.load(_inbox_path(), [])


def _save_inbox(tasks: list[dict]) -> None:
    json_store.save(_inbox_path(), tasks)


def inbox_add(task: str, due: str) -> str:
    task = str(task or "").strip()[:500]
    due = str(due or "").strip()[:100]
    if not task:
        return "Task text is required."
    tasks = _load_inbox()
    if len(tasks) >= 1000:
        return "Task inbox is full."
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


def _due_datetime(due: str, now: datetime) -> datetime | None:
    txt = str(due or "").strip()
    if not txt:
        return None
    lowered = txt.lower()
    if lowered in ("now", "asap"):
        return now
    if lowered == "today":
        return datetime.combine(now.date(), datetime.max.time())
    if lowered == "tomorrow":
        return datetime.combine(now.date() + timedelta(days=1), datetime.max.time())
    for fmt in ("%Y-%m-%d %H:%M", "%m/%d/%Y %H:%M"):
        try:
            return datetime.strptime(txt, fmt)
        except ValueError:
            continue
    for fmt in ("%Y-%m-%d", "%m/%d/%Y"):
        try:
            return datetime.combine(datetime.strptime(txt, fmt).date(), datetime.max.time())
        except ValueError:
            continue
    return None


def _due_passed(due: str, now: datetime) -> bool:
    due_datetime = _due_datetime(due, now)
    return due_datetime is not None and now >= due_datetime