"""The phone tools are declared twice: in Python for the model and in C# for the
router. Nothing else keeps them in sync, so a tool added to one side and not the
other either bloats the prompt with a dead tool or silently hides a real one.
This test makes drift a CI failure.
"""

import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1]))

import gemini_backend

REPO_ROOT = Path(__file__).resolve().parents[2]
PHONE_TOOLS_CS = REPO_ROOT / "Services" / "PhoneTools.cs"


def curated_names_from_csharp() -> list[str]:
    text = PHONE_TOOLS_CS.read_text(encoding="utf-8")
    block = text.split("CuratedNames =", 1)[1].split("];", 1)[0]
    return re.findall(r'"(phone_[a-z0-9_]+)"', block)


class PhoneDeclarationSyncTests(unittest.TestCase):
    def test_backend_declares_exactly_the_curated_csharp_tools(self):
        declared = [tool["name"] for tool in gemini_backend.PHONE_TOOL_DECLARATIONS]
        self.assertEqual(declared, curated_names_from_csharp())

    def test_every_phone_declaration_reaches_the_model(self):
        visible = {tool["name"] for tool in gemini_backend.TOOL_DECLARATIONS}
        for tool in gemini_backend.PHONE_TOOL_DECLARATIONS:
            self.assertIn(tool["name"], visible)

    def test_phone_declarations_are_unique_and_namespaced(self):
        names = [tool["name"] for tool in gemini_backend.PHONE_TOOL_DECLARATIONS]
        self.assertEqual(len(names), len(set(names)))
        self.assertTrue(all(name.startswith("phone_") for name in names))
