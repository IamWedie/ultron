import asyncio
import collections
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parents[1]))

import gemini_backend
from call_audio_bridge import InCallAudioBridge


class FakeVad:
    threshold = 0.5

    def __init__(self, values=None):
        self.calls = []
        self.values = list(values or [])

    def process(self, chunk):
        self.calls.append(len(chunk))
        if self.values:
            return self.values.pop(0)
        return 1.0


class FakeSender:
    def __init__(self):
        self.chunks = []

    async def __call__(self, data):
        self.chunks.append(data)


class VADGateTests(unittest.TestCase):
    def test_large_mic_chunk_is_split_and_emitted_once(self):
        gate = object.__new__(gemini_backend.VADGate)
        gate.vad = FakeVad()
        gate.lead_buffer = collections.deque(maxlen=8)
        gate.tailing = 0
        gate.talk_ticks = 2
        gate.speech = False
        emitted = []

        gate.process_and_emit(np.ones(1024, dtype=np.float32), emitted.append)

        self.assertEqual(gate.vad.calls, [512, 512])
        self.assertEqual(len(emitted), 1)
        self.assertEqual(len(emitted[0]) // 2, 1024)

    def test_retrigger_does_not_reemit_the_previous_tail(self):
        gate = object.__new__(gemini_backend.VADGate)
        gate.vad = FakeVad([1.0, 0.0, 1.0])
        gate.lead_buffer = collections.deque(maxlen=8)
        gate.tailing = 0
        gate.talk_ticks = 1
        gate.speech = False
        emitted = []

        gate.process_and_emit(np.ones(512, dtype=np.float32), emitted.append)
        gate.process_and_emit(np.zeros(512, dtype=np.float32), emitted.append)
        gate.process_and_emit(np.ones(512, dtype=np.float32), emitted.append)

        self.assertEqual([len(chunk) // 2 for chunk in emitted], [512, 512, 512])


class InputDeviceSelectionTests(unittest.TestCase):
    def _devices(self, rows):
        return [{"name": name, "max_input_channels": ch} for name, ch in rows]

    def test_loopback_default_is_avoided(self):
        devices = self._devices([
            ("Stereo Mix (2- Realtek(R) Audio)", 2),
            ("Microphone Array (Intel Smart Sound)", 4),
        ])

        chosen = gemini_backend._resolve_default_input_device(
            devices=devices, default_index=0
        )

        self.assertEqual(chosen, 1)

    def test_sound_mapper_default_falls_back_to_a_real_microphone(self):
        devices = self._devices([
            ("Microsoft Sound Mapper - Input", 2),
            ("Microphone Array (Intel Smart Sound)", 4),
        ])

        chosen = gemini_backend._resolve_default_input_device(
            devices=devices, default_index=0
        )

        self.assertEqual(chosen, 1)

    def test_genuine_microphone_default_is_honoured(self):
        devices = self._devices([
            ("Stereo Mix (Realtek(R) Audio)", 2),
            ("Microphone Array (Intel Smart Sound)", 4),
            ("Microphone (Realtek HD Audio Mic input)", 2),
        ])

        chosen = gemini_backend._resolve_default_input_device(
            devices=devices, default_index=2
        )

        self.assertEqual(chosen, 2)

    def test_output_only_devices_are_not_candidates(self):
        devices = self._devices([
            ("Speakers (Realtek(R) Audio)", 0),
            ("Microphone (Realtek HD Audio Mic input)", 2),
        ])

        chosen = gemini_backend._resolve_default_input_device(
            devices=devices, default_index=0
        )

        self.assertEqual(chosen, 1)

    def test_all_loopback_devices_yield_none_for_system_default(self):
        devices = self._devices([
            ("Stereo Mix (Realtek(R) Audio)", 2),
            ("CABLE Output", 2),
        ])

        chosen = gemini_backend._resolve_default_input_device(
            devices=devices, default_index=0
        )

        self.assertIsNone(chosen)

    def test_loopback_names_are_rejected(self):
        for name in ("Stereo Mix (2- Realtek(R) Audio)", "CABLE Output",
                     "PC Speaker (Realtek HD Audio output)", "Monitor",
                     "Wave Out Loopback", "Sine Generator", "Silence"):
            with self.subTest(name=name):
                self.assertTrue(gemini_backend._is_loopback_name(name))

    def test_microphone_names_are_not_rejected(self):
        for name in ("Microphone Array (Intel Smart Sound)",
                     "Microphone (Realtek HD Audio Mic input)",
                     "Headset Microphone"):
            with self.subTest(name=name):
                self.assertFalse(gemini_backend._is_loopback_name(name))

    def test_named_microphone_outranks_generic_capture_driver(self):
        devices = self._devices([
            ("Primary Sound Capture Driver", 2),
            ("Microphone Array (Intel Smart Sound)", 4),
        ])

        chosen = gemini_backend._resolve_default_input_device(
            devices=devices, default_index=None
        )

        self.assertEqual(chosen, 1)


class AudioBridgeTests(unittest.IsolatedAsyncioTestCase):
    def test_local_mic_is_resampled_from_16k_to_48k(self):
        sender = FakeSender()
        bridge = InCallAudioBridge(asyncio.Queue(), asyncio.Queue(), send_frame=sender)
        bridge._call_active = True
        bridge._mic_bypassed = True
        pcm_16k = (np.ones(160, dtype=np.int16) * 1000).tobytes()

        bridge.feed_local_mic(pcm_16k)

        frame = bridge._mic_tx_queue.get_nowait()
        self.assertEqual(len(frame) // 2, 480)

    def test_gemini_audio_is_resampled_without_consuming_local_output(self):
        sender = FakeSender()
        local_output = asyncio.Queue()
        bridge = InCallAudioBridge(asyncio.Queue(), local_output, send_frame=sender)
        bridge._call_active = True
        pcm_24k = (np.ones(240, dtype=np.int16) * 1000).tobytes()

        bridge.feed_gemini_audio(pcm_24k)

        frame = bridge._gemini_tx_queue.get_nowait()
        self.assertEqual(len(frame) // 2, 480)
        self.assertTrue(local_output.empty())

    async def test_tx_loop_sends_complete_telegram_frames(self):
        sender = FakeSender()
        bridge = InCallAudioBridge(asyncio.Queue(), asyncio.Queue(), send_frame=sender)
        bridge._running = True
        bridge._call_active = True
        task = asyncio.create_task(bridge._tx_loop())

        bridge.feed_gemini_audio((np.ones(240, dtype=np.int16) * 1000).tobytes())
        for _ in range(100):
            if sender.chunks:
                break
            await asyncio.sleep(0.01)

        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual(len(sender.chunks), 1)
        self.assertEqual(len(sender.chunks[0]) // 2, 480)

    async def test_barge_in_drops_assistant_frames_but_preserves_mic_frames(self):
        bridge = InCallAudioBridge(asyncio.Queue(), asyncio.Queue(), send_frame=FakeSender())
        bridge._call_active = True
        bridge._mic_bypassed = True
        bridge.feed_gemini_audio((np.ones(480, dtype=np.int16) * 1000).tobytes())
        bridge.feed_local_mic((np.ones(160, dtype=np.int16) * 1000).tobytes())

        await bridge._handle_barge_in()

        self.assertTrue(bridge._gemini_tx_queue.empty())
        self.assertFalse(bridge._mic_tx_queue.empty())


class FakeBridge:
    def __init__(self):
        self.stopped = False

    async def stop(self):
        self.stopped = True


class FakeWrtc:
    def __init__(self):
        self.stopped_for = []

    async def stop(self, user_id):
        self.stopped_for.append(user_id)


class FakeClient:
    def __init__(self):
        self.requests = []
        self.disconnected = False

    async def __call__(self, request):
        self.requests.append(request)
        return object()

    async def disconnect(self):
        self.disconnected = True


class SessionLifecycleTests(unittest.IsolatedAsyncioTestCase):
    def test_empty_api_key_can_wait_for_startup_configuration(self):
        session = gemini_backend.GeminiSession("")

        self.assertIsNone(session.client)

    async def test_non_object_ipc_message_is_rejected_without_raising(self):
        session = gemini_backend.GeminiSession("test-key")

        await session._handle_ipc([])

    def test_model_cannot_enable_live_vision_without_user_consent(self):
        session = gemini_backend.GeminiSession("test-key")

        result = session._handle_set_live_vision({"enabled": True})

        self.assertIn("local", result.lower())
        self.assertFalse(session._live_vision)

    async def test_string_false_is_not_treated_as_true(self):
        session = gemini_backend.GeminiSession("test-key")

        await session._handle_ipc({"type": "set_away", "on": "false"})

        self.assertFalse(session._away_mode)

    async def test_request_start_returns_without_blocking_ipc_handler(self):
        session = gemini_backend.GeminiSession("test-key")
        started = asyncio.Event()
        release = asyncio.Event()

        async def fake_start():
            started.set()
            await release.wait()

        session._run_session_supervisor = fake_start
        task = session.request_start()
        await started.wait()

        self.assertFalse(task.done())
        release.set()
        await task


class GeminiAudioStateTests(unittest.TestCase):
    def test_session_has_a_safe_active_bridge_lookup(self):
        session = gemini_backend.GeminiSession("test-key")

        self.assertIsNone(session._active_audio_bridge())


class TelegramCleanupTests(unittest.IsolatedAsyncioTestCase):
    async def test_cleanup_closes_bridge_native_call_and_client(self):
        controller = gemini_backend.TelegramController(lambda event: None)
        bridge = FakeBridge()
        wrtc = FakeWrtc()
        client = FakeClient()
        controller._audio_bridge = bridge
        controller._call_start_ts = __import__("time").time()

        await controller._cleanup_call_resources(
            client, wrtc, 42, 7, 8, 9
        )

        self.assertTrue(bridge.stopped)
        self.assertEqual(wrtc.stopped_for, [42])
        self.assertEqual(len(client.requests), 1)
        self.assertTrue(client.disconnected)
        self.assertIsNone(controller._audio_bridge)


if __name__ == "__main__":
    unittest.main()
