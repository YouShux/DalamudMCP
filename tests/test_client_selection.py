"""Verify that automation never silently changes the selected game."""
import contextlib
import importlib.util
import io
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("mcp_client", Path(__file__).parents[1] / "scripts/mcp_client.py")
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)

FIRST = {"gameProcessID": 101, "instanceID": "first", "endpoint": "http://127.0.0.1:61016/mcp"}
SECOND = {"gameProcessID": 202, "instanceID": "second", "endpoint": "http://127.0.0.1:61017/mcp"}


class SelectionTests(unittest.TestCase):
    def invoke(self, arguments, instances, identity=None):
        self.sent = []
        outer = self

        class FakeClient:
            def __init__(self, endpoint, *args):
                outer.endpoint = endpoint

            def initialize(self):
                return {}

            def rpc(self, method, params):
                outer.sent.append(params["name"])
                return {"structuredContent": {"data": identity or {"processId": 202, "instanceId": "second"}}}

            def close(self):
                pass

        with patch.object(sys, "argv", ["mcp_client.py", *arguments]), \
             patch.object(client, "discover_instances", return_value=instances), \
             patch.object(client, "local_bearer_token", return_value=None), \
             patch.object(client, "Client", FakeClient), contextlib.redirect_stdout(io.StringIO()):
            return client.main()

    def test_multiple_requires_selection(self):
        with self.assertRaisesRegex(ValueError, "Multiple game instances"):
            self.invoke(["call", "get_player_context"], [FIRST, SECOND])
        self.assertEqual(self.sent, [])

    def test_pid_selects_exact_endpoint(self):
        self.assertEqual(self.invoke(["--pid", "202", "call", "get_player_context"], [FIRST, SECOND]), 0)
        self.assertEqual(self.endpoint, SECOND["endpoint"])
        self.assertEqual(self.sent, ["get_session_status", "get_player_context"])

    def test_instance_selects_exact_lifetime(self):
        self.invoke(["--instance", "second", "call", "get_player_context"], [FIRST, SECOND])
        self.assertEqual(self.endpoint, SECOND["endpoint"])

    def test_missing_selection_does_not_fall_back(self):
        with self.assertRaisesRegex(ValueError, "unavailable"):
            self.invoke(["--pid", "999", "call", "get_player_context"], [FIRST, SECOND])
        self.assertEqual(self.sent, [])

    def test_reused_port_does_not_forward(self):
        with self.assertRaisesRegex(RuntimeError, "changed game instances"):
            self.invoke(["--instance", "second", "call", "get_player_context"], [SECOND],
                        {"processId": 202, "instanceId": "reloaded"})
        self.assertEqual(self.sent, ["get_session_status"])

    def test_conflicting_pid_and_instance_refuses(self):
        with self.assertRaisesRegex(ValueError, "unavailable"):
            self.invoke(["--pid", "101", "--instance", "second", "call", "get_player_context"], [FIRST, SECOND])


if __name__ == "__main__":
    unittest.main()
