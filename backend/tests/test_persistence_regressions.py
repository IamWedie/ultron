import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1]))

from json_store import CorruptStoreError, load, save


class JsonStoreTests(unittest.TestCase):
    def test_save_and_load_round_trip(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            path = Path(temp_dir) / "state.json"
            save(str(path), {"enabled": True})

            self.assertEqual(load(str(path), {}), {"enabled": True})

    def test_corrupt_store_is_not_silently_replaced(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            path = Path(temp_dir) / "state.json"
            path.write_text("not json", encoding="utf-8")

            with self.assertRaises(CorruptStoreError):
                load(str(path), {})


if __name__ == "__main__":
    unittest.main()
