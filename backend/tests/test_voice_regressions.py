import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parents[1]))

from voice_dsp import SR, UltronVoice


class VoiceDspTests(unittest.TestCase):
    def test_flush_drain_preserves_short_response_duration(self):
        samples = (np.sin(2 * np.pi * 220 * np.arange(SR // 2) / SR) * 0.7 * 32767).astype(np.int16)
        voice = UltronVoice(enabled=True)
        output = voice.process(samples.tobytes())
        output += voice.flush_drain()

        ratio = (len(output) // 2) / len(samples)
        self.assertGreater(ratio, 0.9)
        self.assertLess(ratio, 1.1)

    def test_short_response_does_not_manufacture_a_long_tail(self):
        samples = (np.sin(2 * np.pi * 220 * np.arange(100) / SR) * 0.7 * 32767).astype(np.int16)
        voice = UltronVoice(enabled=True)
        output = voice.process(samples.tobytes())
        output += voice.flush_drain()

        self.assertLessEqual(len(output) // 2, len(samples) * 2)

    def test_extreme_dsp_parameters_are_rejected(self):
        voice = UltronVoice(enabled=True)

        with self.assertRaises(ValueError):
            voice.set(semitones=1000)
        with self.assertRaises(ValueError):
            voice.set(chorus=2)

    def test_odd_byte_chunks_are_realigned(self):
        samples = (np.sin(2 * np.pi * 220 * np.arange(2400) / SR) * 0.7 * 32767).astype(np.int16)
        voice = UltronVoice(enabled=True)
        raw = samples.tobytes()
        output = b""

        for index in range(0, len(raw), 3):
            output += voice.process(raw[index:index + 3])

        self.assertEqual(len(output) % 2, 0)


if __name__ == "__main__":
    unittest.main()
