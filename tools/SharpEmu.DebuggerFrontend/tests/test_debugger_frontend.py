# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

from __future__ import annotations

import json
from pathlib import Path
import socket
import sys
import tempfile
import textwrap
import threading
import unittest
from urllib.request import Request, urlopen


FRONTEND_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(FRONTEND_ROOT))

from debugger_frontend import (  # noqa: E402
    BridgeError,
    DebuggerBridge,
    EmulatorProcessManager,
    FrontendHttpServer,
    FrontendRequestHandler,
    analyze_debug_stop,
    create_frontend_server,
)


REGISTERS = {
    "rax": "0x0000000000000001",
    "rip": "0x00000008801234A0",
    "rflags": "0x0000000000000202",
}


class FakeDebuggerServer:
    def __init__(self) -> None:
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.port = self.listener.getsockname()[1]
        self.client: socket.socket | None = None
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self) -> None:
        try:
            client, _ = self.listener.accept()
            self.client = client
            reader = client.makefile("r", encoding="utf-8", newline="\n")
            writer = client.makefile("w", encoding="utf-8", newline="\n")
            self._write(writer, {"event": "hello", "protocol": "json-lines/1", "state": "Paused"})
            for line in reader:
                request = json.loads(line)
                command = request["command"]
                if command == "status":
                    self._write(writer, {
                        "ok": True,
                        "command": command,
                        "data": {"state": "Paused", "breakpoints": 1},
                    })
                elif command == "registers":
                    self._write(writer, {
                        "event": "stopped",
                        "reason": "Breakpoint",
                        "address": REGISTERS["rip"],
                        "frameKind": "ProcessEntry",
                        "frameLabel": "eboot.bin",
                        "registers": REGISTERS,
                    })
                    self._write(writer, {
                        "ok": True,
                        "command": command,
                        "data": {"registers": REGISTERS},
                    })
                elif command == "list-breakpoints":
                    self._write(writer, {
                        "ok": True,
                        "command": command,
                        "data": {
                            "breakpoints": [{
                                "id": 1,
                                "kind": "Execute",
                                "address": REGISTERS["rip"],
                                "length": 1,
                                "enabled": True,
                            }],
                        },
                    })
                else:
                    self._write(writer, {"ok": True, "command": command})
        except OSError:
            pass

    @staticmethod
    def _write(writer: object, payload: dict[str, object]) -> None:
        writer.write(json.dumps(payload, separators=(",", ":")) + "\n")
        writer.flush()

    def close(self) -> None:
        if self.client is not None:
            try:
                self.client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.client.close()
        self.listener.close()
        self.thread.join(timeout=1)


class DebuggerBridgeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.server = FakeDebuggerServer()
        self.bridge = DebuggerBridge()
        self.bridge.connect("127.0.0.1", self.server.port)

    def tearDown(self) -> None:
        self.bridge.disconnect(log=False)
        self.server.close()

    def test_connect_receives_hello(self) -> None:
        snapshot = self.bridge.snapshot()
        self.assertTrue(snapshot["connected"])
        self.assertEqual("json-lines/1", snapshot["protocol"])
        self.assertEqual("Paused", snapshot["state"])

    def test_request_pairs_reply_while_processing_event(self) -> None:
        reply = self.bridge.request({"command": "registers"})
        self.assertTrue(reply["ok"])
        snapshot = self.bridge.snapshot()
        self.assertEqual(REGISTERS["rip"], snapshot["registers"]["rip"])
        self.assertEqual("Breakpoint", snapshot["lastStop"]["reason"])
        self.assertTrue(any(item["summary"] == "stopped" for item in snapshot["messages"]))

    def test_breakpoint_snapshot_is_updated(self) -> None:
        self.bridge.request({"command": "list-breakpoints"})
        snapshot = self.bridge.snapshot()
        self.assertEqual(1, len(snapshot["breakpoints"]))
        self.assertEqual("Execute", snapshot["breakpoints"][0]["kind"])

    def test_invalid_request_is_rejected_locally(self) -> None:
        with self.assertRaises(BridgeError):
            self.bridge.request({})

    def test_journal_cursor_returns_only_new_messages(self) -> None:
        cursor = self.bridge.snapshot()["cursor"]
        self.bridge.request({"command": "status"})
        snapshot = self.bridge.snapshot(cursor)
        self.assertGreaterEqual(len(snapshot["messages"]), 2)
        self.assertTrue(all(message["id"] > cursor for message in snapshot["messages"]))


class StallAnalysisTests(unittest.TestCase):
    def test_legacy_mutex_stall_identifies_likely_scheduler_fix(self) -> None:
        analysis = analyze_debug_stop({
            "reason": "Stall",
            "detail": "kind=ImportLoop, nid=9UK1vLZQft4, dispatch#40667904, rip=0x0000000801CE2418",
        })
        self.assertIsNotNone(analysis)
        self.assertEqual("Mutex lock is livelocking", analysis["title"])
        self.assertEqual("High", analysis["confidence"])
        self.assertIn("PthreadMutexLockCore", analysis["fix"])
        self.assertTrue(any("9UK1vLZQft4" in item for item in analysis["evidence"]))

    def test_unresolved_import_stall_recommends_export_implementation(self) -> None:
        analysis = analyze_debug_stop({
            "reason": "Stall",
            "stall": {
                "kind": "ImportLoop",
                "nid": "missing-nid",
                "resolved": False,
                "dispatchIndex": 8192,
                "instructionPointer": "0x0000000000001234",
            },
        })
        self.assertIsNotNone(analysis)
        self.assertEqual("Unresolved import is being retried", analysis["title"])
        self.assertIn("Implement or correctly register", analysis["fix"])


class FrontendHttpTests(unittest.TestCase):
    def setUp(self) -> None:
        self.debugger = FakeDebuggerServer()
        self.bridge = DebuggerBridge()
        self.process_manager = EmulatorProcessManager(self.bridge.journal)
        handler = type(
            "TestFrontendRequestHandler",
            (FrontendRequestHandler,),
            {"bridge": self.bridge, "process_manager": self.process_manager},
        )
        self.http_server = FrontendHttpServer(("127.0.0.1", 0), handler)
        self.http_port = self.http_server.server_address[1]
        self.http_thread = threading.Thread(target=self.http_server.serve_forever, daemon=True)
        self.http_thread.start()

    def tearDown(self) -> None:
        self.bridge.disconnect(log=False)
        self.process_manager.stop(log=False)
        self.http_server.shutdown()
        self.http_server.server_close()
        self.http_thread.join(timeout=1)
        self.debugger.close()

    def post(self, path: str, payload: dict[str, object]) -> dict[str, object]:
        request = Request(
            f"http://127.0.0.1:{self.http_port}{path}",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urlopen(request, timeout=2) as response:
            return json.load(response)

    def test_api_connect_and_command_round_trip(self) -> None:
        connected = self.post("/api/connect", {"host": "127.0.0.1", "port": self.debugger.port})
        self.assertTrue(connected["connected"])
        self.assertEqual("json-lines/1", connected["protocol"])

        result = self.post("/api/command", {"request": {"command": "registers"}})
        self.assertTrue(result["response"]["ok"])

    def test_static_frontend_is_served(self) -> None:
        with urlopen(f"http://127.0.0.1:{self.http_port}/", timeout=2) as response:
            body = response.read().decode("utf-8")
            self.assertEqual("text/html; charset=utf-8", response.headers["Content-Type"])
        self.assertIn("SharpEmu <span>Debugger</span>", body)
        self.assertIn('rel="icon" type="image/webp"', body)

        with urlopen(f"http://127.0.0.1:{self.http_port}/sharpemu-logo.webp", timeout=2) as response:
            logo = response.read()
            self.assertEqual("image/webp", response.headers["Content-Type"])
        self.assertTrue(logo.startswith(b"RIFF"))

    def test_api_launches_and_stops_emulator_with_auto_attach(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            eboot = root / "eboot.bin"
            eboot.write_bytes(b"test")
            emulator = root / "fake-sharpemu"
            emulator.write_text(textwrap.dedent("""\
                #!/usr/bin/env python3
                import json
                import socket
                import sys

                endpoint = next(arg.split("=", 1)[1] for arg in sys.argv if arg.startswith("--debug-server="))
                port = int(endpoint.rsplit(":", 1)[1])
                listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                listener.bind(("127.0.0.1", port))
                listener.listen(4)
                print("Fake SharpEmu debug server ready", flush=True)
                while True:
                    client, _ = listener.accept()
                    with client:
                        reader = client.makefile("r", encoding="utf-8")
                        writer = client.makefile("w", encoding="utf-8")
                        writer.write(json.dumps({"event": "hello", "protocol": "json-lines/1", "state": "Paused"}) + "\\n")
                        writer.flush()
                        for line in reader:
                            request = json.loads(line)
                            command = request["command"]
                            data = {"state": "Paused", "breakpoints": 0} if command == "status" else {"breakpoints": []}
                            writer.write(json.dumps({"ok": True, "command": command, "data": data}) + "\\n")
                            writer.flush()
                """), encoding="utf-8")
            emulator.chmod(0o755)

            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reservation:
                reservation.bind(("127.0.0.1", 0))
                debug_port = reservation.getsockname()[1]

            launched = self.post("/api/launch", {
                "ebootPath": str(eboot),
                "debugPort": debug_port,
                "emulatorPath": str(emulator),
            })
            self.assertTrue(launched["connected"])
            self.assertTrue(launched["emulator"]["running"])
            self.assertEqual(str(eboot), launched["emulator"]["eboot"])

            stopped = self.post("/api/stop-emulator", {})
            self.assertFalse(stopped["connected"])
            self.assertFalse(stopped["emulator"]["running"])

    def test_rerun_replaces_existing_frontend_on_same_port(self) -> None:
        replacement_handler = type(
            "ReplacementFrontendRequestHandler",
            (FrontendRequestHandler,),
            {"bridge": self.bridge, "process_manager": self.process_manager},
        )
        old_thread = self.http_thread
        replacement = create_frontend_server("127.0.0.1", self.http_port, replacement_handler)
        old_thread.join(timeout=2)
        self.assertFalse(old_thread.is_alive())

        self.http_server = replacement
        self.http_thread = threading.Thread(target=replacement.serve_forever, daemon=True)
        self.http_thread.start()
        with urlopen(f"http://127.0.0.1:{self.http_port}/api/health", timeout=2) as response:
            health = json.load(response)
        self.assertEqual("sharpemu-debugger-frontend", health["application"])


if __name__ == "__main__":
    unittest.main()
