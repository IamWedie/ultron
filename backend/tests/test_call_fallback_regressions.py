"""Regression tests: a call that fails before ringing must still reach the owner.

A guardian alert is only useful if the owner always finds out. When the call
never rings (network failure, unauthorized session, media setup failure), the
text fallback is the only delivery path, so every non-cancel failure must
attempt it exactly once.
"""

import asyncio
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).parents[1]))

import telegram_client


class _FakeCallClient:
    """Telethon stand-in whose connect() fails, like a real network hiccup."""

    def __init__(self, *args, **kwargs):
        self.sent = []
        self.event_handlers = []
        self.disconnected = False

    def add_event_handler(self, handler, _event):
        self.event_handlers.append(handler)

    async def connect(self):
        raise ConnectionError("voice-call connection failed before ringing")

    async def send_message(self, target, text):
        self.sent.append((target, text))
        return "msg-1"

    async def disconnect(self):
        self.disconnected = True


class CallFailureFallbackTests(unittest.IsolatedAsyncioTestCase):
    def _controller(self, events, payload=None):
        controller = telegram_client.TelegramController(events.append)
        controller._call_payload = payload if payload is not None else {
            "source": "guardian",
            "title": "System Alert",
            "body": "Battery at 5%",
            "priority": "high",
        }
        controller._fallbacks_enabled = lambda: True
        controller._fallback_target = lambda: "@owner"
        return controller

    async def test_call_that_fails_before_ringing_sends_the_fallback_text(self):
        events = []
        controller = self._controller(events)
        created = []

        def build_client(*args, **kwargs):
            client = _FakeCallClient(*args, **kwargs)
            created.append(client)
            return client

        with mock.patch.object(telegram_client, "TelegramClient", build_client), \
                mock.patch.object(telegram_client, "StringSession", lambda value: object()):
            await controller._run_call_task("1", "hash", "session", "@owner")

        self.assertEqual(
            len(created), 1,
            "the call should have been attempted before failing",
        )
        self.assertEqual(
            len(created[0].sent), 1,
            "a call that never rang must still send the owner one text",
        )
        target, text = created[0].sent[0]
        self.assertEqual(target, "@owner")
        self.assertIn("Battery at 5%", text)
        self.assertTrue(
            any(e.get("type") == "telegram_call_state" and e.get("state") == "error"
                for e in events),
            "the failure must still be reported to the host",
        )
        self.assertTrue(
            any(e.get("type") == "telegram_call_fallback_sent" and e.get("ok")
                for e in events),
            "the host must be told the fallback text went out",
        )

    async def test_fallback_text_is_sent_at_most_once_per_call(self):
        events = []
        controller = self._controller(events)
        client = _FakeCallClient()

        await controller._fail_call(client, "boom", "connect_failed")
        await controller._fail_call(client, "boom again", "connect_failed")

        self.assertEqual(
            len(client.sent), 1,
            "one alert must not turn into several texts from one failed call",
        )

    async def test_manual_call_without_payload_sends_no_fallback(self):
        events = []
        controller = self._controller(events, payload={})
        client = _FakeCallClient()

        await controller._fail_call(client, "boom", "connect_failed")

        self.assertEqual(client.sent, [])

    async def test_disabled_fallback_is_respected_even_on_failure(self):
        events = []
        controller = self._controller(events)
        controller._fallbacks_enabled = lambda: False
        client = _FakeCallClient()

        await controller._fail_call(client, "boom", "connect_failed")

        self.assertEqual(client.sent, [])

    async def test_startup_failure_still_attempts_the_fallback_text(self):
        events = []
        controller = self._controller(events)
        client = _FakeCallClient()
        controller._client = client
        client.connect = lambda: asyncio.sleep(0)  # authorized and connected
        client.is_connected = lambda: True
        controller._calls_enabled = lambda: True
        controller._target_user_id = None
        controller._creds_from_config = lambda: ("1", "hash", "@owner")

        await controller.call_start({"payload": controller._call_payload})

        self.assertEqual(
            len(client.sent), 1,
            "a call that cannot even start must still try to reach the owner",
        )
        self.assertTrue(
            any(e.get("type") == "telegram_call_fallback_sent" and e.get("ok")
                for e in events),
            "the host must be told the fallback text went out",
        )

    async def test_failed_fallback_delivery_is_reported_not_swallowed(self):
        events = []
        controller = self._controller(events)
        client = _FakeCallClient()

        async def explode(*args, **kwargs):
            raise RuntimeError("telegram unreachable")

        client.send_message = explode

        await controller._fail_call(client, "boom", "connect_failed")

        failures = [e for e in events
                    if e.get("type") == "telegram_call_fallback_sent" and not e.get("ok")]
        self.assertEqual(
            len(failures), 1,
            "a fallback that cannot be delivered must be visible, not silent",
        )


if __name__ == "__main__":
    unittest.main()
