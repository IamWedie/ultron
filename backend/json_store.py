from __future__ import annotations

import json
import os
import tempfile
from typing import Any


class CorruptStoreError(ValueError):
    pass


def load(path: str, default: Any) -> Any:
    try:
        with open(path, "r", encoding="utf-8") as handle:
            value = json.load(handle)
    except FileNotFoundError:
        return default
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise CorruptStoreError(f"Could not read JSON store: {path}") from exc
    if not isinstance(value, type(default)):
        raise CorruptStoreError(f"JSON store has an unexpected shape: {path}")
    return value


def save(path: str, value: Any) -> None:
    directory = os.path.dirname(path) or "."
    os.makedirs(directory, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".json-", suffix=".tmp", dir=directory)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(value, handle, indent=2, ensure_ascii=False, allow_nan=False)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        try:
            os.remove(temporary)
        except FileNotFoundError:
            pass
