"""Regression tests: a stray edit in Settings must not silently redirect calls.

Once the call target has been set up and proven, a later typo in the Settings
text (or a cleared field) must not quietly point alerts at a different person.
The first successful setup locks the resolved identity; changing it takes an
explicit unlock.
"""

import asyncio
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).parents[1]))

import telegram_client


class _FakeResolveClient:
    """Telethon stand-in that resolves any handle to a preset user."""

    def __init__(self, users):
        self._users = users

    def is_connected(self):
        return True

    async def is_user_authorized(self):
        return True

    async def get_entity(self, target):
        return self._users[target]

    async def get_input_entity(self, entity):
        return telegram_client.t.InputPeerUser(entity.id, entity.id * 7 + 1)


class TargetLockTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        patcher = mock.patch.object(
            telegram_client, "TARGET_LOCK_FILE",
            str(Path(self._temp.name) / "target_lock.dat"),
        )
        patcher.start()
        self.addCleanup(patcher.stop)
        # Real Telethon User objects: resolve_target validates the entity type.
        self.users = {
            "@owner": telegram_client.t.User(
                id=111, username="owner", first_name="Owner", bot=False),
            "@someone-else": telegram_client.t.User(
                id=999, username="someone_else", first_name="Someone", bot=False),
        }

    def _controller(self, events):
        controller = telegram_client.TelegramController(events.append)
        controller._api_id = 1
        controller._api_hash = "hash"
        controller._client = _FakeResolveClient(self.users)
        return controller

    async def _resolve(self, controller, target):
        await controller.resolve_target({"target": target})

    async def test_first_successful_setup_locks_the_target(self):
        events = []
        controller = self._controller(events)

        await self._resolve(controller, "@owner")

        results = [e for e in events if e.get("type") == "telegram_target_result"]
        self.assertTrue(results[0]["ok"])
        self.assertEqual(controller._target_user_id, "111")
        self.assertTrue(
            any(e.get("type") == "telegram_target_lock" and e.get("locked") for e in events),
            "the first successful setup must report that the target is now locked",
        )

    async def test_locked_target_refuses_to_redirect_to_another_person(self):
        events = []
        controller = self._controller(events)
        await self._resolve(controller, "@owner")
        events.clear()

        # Simulate the Settings field being edited to a different handle.
        await self._resolve(controller, "@someone-else")

        results = [e for e in events if e.get("type") == "telegram_target_result"]
        self.assertFalse(
            results[0]["ok"],
            "a locked target must not be redirected by editing the Settings text",
        )
        self.assertIn("locked", results[0]["message"].lower())
        self.assertEqual(
            controller._target_user_id, "111",
            "the previously locked identity must remain the call target",
        )

    async def test_unlocked_target_can_be_repointed_deliberately(self):
        events = []
        controller = self._controller(events)
        await self._resolve(controller, "@owner")

        await controller.set_target_lock({"locked": False})
        events.clear()
        await self._resolve(controller, "@someone-else")

        results = [e for e in events if e.get("type") == "telegram_target_result"]
        self.assertTrue(results[0]["ok"])
        self.assertEqual(controller._target_user_id, "999")

    async def test_locking_requires_a_resolved_target(self):
        events = []
        controller = self._controller(events)

        await controller.set_target_lock({"locked": True})

        results = [e for e in events if e.get("type") == "telegram_target_lock"]
        self.assertFalse(
            results[0]["locked"],
            "there is nothing safe to lock before a target is resolved",
        )

    async def test_lock_survives_a_backend_restart(self):
        events = []
        controller = self._controller(events)
        await self._resolve(controller, "@owner")

        # Fresh controller, same lock file: this is what a restart looks like.
        restarted_events = []
        restarted = self._controller(restarted_events)
        await restarted.resolve_target({"target": "@someone-else"})

        results = [e for e in restarted_events if e.get("type") == "telegram_target_result"]
        self.assertFalse(results[0]["ok"], "the lock must outlive a backend restart")
        self.assertIsNone(restarted._target_user_id)


if __name__ == "__main__":
    unittest.main()
