import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1]))

from computer_use import validate_action


class ComputerActionTests(unittest.TestCase):
    def test_valid_click_is_accepted(self):
        action = {"type": "click", "x": 100, "y": 200}

        self.assertEqual(validate_action(action), action)

    def test_unknown_action_is_rejected(self):
        with self.assertRaises(ValueError):
            validate_action({"type": "run_shell", "command": "calc"})

    def test_out_of_range_coordinates_are_rejected(self):
        with self.assertRaises(ValueError):
            validate_action({"type": "click", "x": -1, "y": 200})

    def test_unbounded_text_is_rejected(self):
        with self.assertRaises(ValueError):
            validate_action({"type": "type", "text": "x" * 5000})


if __name__ == "__main__":
    unittest.main()
