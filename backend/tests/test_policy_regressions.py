import asyncio
import datetime
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).parents[1]))

import gemini_backend
import productivity
import telegram_client


class TelegramPolicyTests(unittest.IsolatedAsyncioTestCase):
    def test_missing_configuration_does_not_invent_a_call_target(self):
        controller = telegram_client.TelegramController(lambda event: None)
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(
            telegram_client.os.path, "exists", return_value=False
        ):
            _, _, target = controller._creds_from_config()

        self.assertEqual(target, "")

    def test_explicit_false_fallback_environment_disables_fallback(self):
        controller = telegram_client.TelegramController(lambda event: None)
        with mock.patch.dict(os.environ, {"ULTRON_CALL_FALLBACK_ENABLED": "false"}, clear=True):
            self.assertFalse(controller._fallbacks_enabled())

    def test_fallback_target_can_be_configured(self):
        controller = telegram_client.TelegramController(lambda event: None)
        with mock.patch.dict(os.environ, {"ULTRON_CALL_FALLBACK_CHAT_ID": "12345"}, clear=True):
            self.assertEqual(controller._fallback_target(), "12345")

    async def test_call_binds_to_the_resolved_peer_instead_of_reloading_target_text(self):
        controller = telegram_client.TelegramController(lambda event: None)
        controller._target_user_id = "123"
        controller._target_access_hash = 456
        controller._calls_enabled = lambda: True
        controller._creds_from_config = lambda: ("1", "hash", "different-target")
        calls = []

        async def fake_run(*args):
            calls.append(args)

        controller._run_call_task = fake_run
        with mock.patch.object(telegram_client, "load_session", return_value="session"):
            await controller.call_start({}, asyncio.Queue(), asyncio.Queue())
            await asyncio.sleep(0)

        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0][4], telegram_client.t.InputPeerUser(123, 456))


class DocumentPolicyTests(unittest.TestCase):
    def test_document_output_cannot_escape_the_document_root(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = os.path.join(temp_dir, "Ultron")
            os.makedirs(root)
            with mock.patch.object(productivity, "_documents_root", return_value=root):
                with self.assertRaises(ValueError):
                    productivity.create_document("word", "../escape.docx", {})


class TaskPolicyTests(unittest.TestCase):
    def test_today_is_not_overdue_before_end_of_day(self):
        now = datetime.datetime(2026, 1, 1, 12, 0)

        self.assertFalse(productivity._due_passed("today", now))

    def test_tomorrow_resolves_to_the_next_local_day(self):
        now = datetime.datetime(2026, 1, 3, 12, 0)

        due = productivity._due_datetime("tomorrow", now)

        self.assertEqual(due.date(), datetime.date(2026, 1, 4))
        self.assertEqual(due.time(), datetime.time.max)


class GuardianRoutingTests(unittest.IsolatedAsyncioTestCase):
    async def test_guardian_starts_telegram_call_through_controller(self):
        class Telegram:
            def __init__(self):
                self.calls = []

            async def call_start(self, msg, mic_queue, audio_out_queue):
                self.calls.append((msg, mic_queue, audio_out_queue))

        session = gemini_backend.GeminiSession("test-key")
        telegram = Telegram()
        session.telegram = telegram

        await session._trigger_telegram_call("CPU high", False, "high")

        self.assertEqual(len(telegram.calls), 1)
        self.assertEqual(telegram.calls[0][0]["payload"]["body"], "CPU high")


if __name__ == "__main__":
    unittest.main()
