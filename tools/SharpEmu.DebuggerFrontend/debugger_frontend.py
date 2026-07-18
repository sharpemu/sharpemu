#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Local web frontend and TCP bridge for the SharpEmu live debugger."""

from __future__ import annotations

import argparse
import copy
from collections import deque
import errno
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import platform
import queue
import re
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import threading
import time
from typing import Any
from urllib.parse import parse_qs, urlparse
from urllib.error import URLError
from urllib.request import Request, urlopen
import webbrowser


APP_ROOT = Path(__file__).resolve().parent
REPO_ROOT = APP_ROOT.parents[1]
WEB_ROOT = APP_ROOT / "web"
MAX_REQUEST_BYTES = 1024 * 1024
MAX_JOURNAL_MESSAGES = 1000
APPLICATION_ID = "sharpemu-debugger-frontend"

KNOWN_WAIT_IMPORTS: dict[str, tuple[str, str, str]] = {
    "9UK1vLZQft4": ("libKernel", "scePthreadMutexLock", "mutex"),
    "7H0iTOciTLo": ("libKernel", "pthread_mutex_lock", "mutex"),
    "WKAXJ4XBPQ4": ("libKernel", "scePthreadCondWait", "condition"),
    "BmMjYxmew1w": ("libKernel", "scePthreadCondTimedwait", "condition"),
    "Op8TBGY5KHg": ("libKernel", "pthread_cond_wait", "condition"),
    "27bAgiJmOh0": ("libKernel", "pthread_cond_timedwait", "condition"),
    "Zxa0VhQVTsk": ("libKernel", "sceKernelWaitSema", "wait"),
    "JTvBflhYazQ": ("libKernel", "sceKernelWaitEventFlag", "wait"),
    "fzyMKs9kim0": ("libKernel", "sceKernelWaitEqueue", "wait"),
    "j6RaAUlaLv0": ("libSceVideoOut", "sceVideoOutWaitVblank", "timeline"),
    "1jfXLRVzisc": ("libKernel", "sceKernelUsleep", "sleep"),
}


def analyze_debug_stop(stop: dict[str, Any]) -> dict[str, Any] | None:
    """Turns structured or legacy stall evidence into actionable guidance."""

    if str(stop.get("reason", "")).lower() != "stall":
        return None
    stall = copy.deepcopy(stop.get("stall")) if isinstance(stop.get("stall"), dict) else {}
    detail = str(stop.get("detail", ""))
    legacy = _parse_stall_detail(detail)
    for key, value in legacy.items():
        stall.setdefault(key, value)

    kind = str(stall.get("kind", "Unknown"))
    nid = str(stall.get("nid", "")).strip() or None
    known = KNOWN_WAIT_IMPORTS.get(nid or "")
    library = str(stall.get("library") or (known[0] if known else "")).strip() or None
    function = str(stall.get("function") or (known[1] if known else "")).strip() or None
    resolved = bool(stall.get("resolved")) or function is not None
    category = known[2] if known else _classify_stall_function(function)
    dispatch_index = _parse_optional_int(stall.get("dispatchIndex"))
    instruction_pointer = stall.get("instructionPointer") or legacy.get("instructionPointer")
    argument0 = stall.get("argument0")

    export_label = f"{library}:{function}" if library and function else function or nid or "unknown import"
    evidence = [
        "The import-loop watchdog observed a repeating pattern of at most two imports/return sites for several seconds.",
    ]
    if nid:
        evidence.append(
            f"NID {nid} resolves to {export_label}." if resolved else f"NID {nid} is not resolved to an HLE export."
        )
    if dispatch_index is not None:
        evidence.append(f"The stop occurred after import dispatch #{dispatch_index:,}, indicating sustained retry rather than a short spin.")
    if instruction_pointer:
        evidence.append(f"The repeating guest return site is {instruction_pointer}.")
    if argument0 and str(argument0) not in {"0", "0x0000000000000000"}:
        evidence.append(f"The first ABI argument is {argument0}; for synchronization calls this is usually the waited object address.")

    if not resolved:
        return {
            "severity": "High",
            "confidence": "High",
            "title": "Unresolved import is being retried",
            "summary": f"The guest repeatedly calls {nid or 'an unknown NID'} because the expected service never completes.",
            "cause": "The NID has no matching HLE export for this target generation/library, so the fallback result sends the guest back into its retry path.",
            "fix": "Implement or correctly register the missing export, including its expected return code and output-memory side effects. Confirm the library and Gen5 target flags match the game import.",
            "actions": [
                "Search the HLE export registry for the NID and verify its library and target generation.",
                "Trace the import arguments and determine which output/status value the guest expects to change.",
                "Add a focused export test before disabling the loop guard; disabling it only hides the livelock.",
            ],
            "evidence": evidence,
            "kind": kind,
            "category": "unresolved-import",
        }

    if category == "mutex":
        return {
            "severity": "High",
            "confidence": "High",
            "title": "Mutex lock is livelocking",
            "summary": f"The guest keeps re-entering {export_label} from the same small call pattern without acquiring the lock or blocking.",
            "cause": "The mutex is probably contended or self-owned, but the HLE/scheduler path is returning control to the guest without durable progress. A mismatched owner identity, incorrect mutex type, or unlock path that does not wake the waiter can produce the same loop.",
            "fix": "Start in KernelPthreadCompatExports.PthreadMutexLockCore: make sure a contended lock parks the guest waiter through GuestThreadExecution, and make sure PthreadMutexUnlockCore grants and wakes the queued waiter. Also validate NORMAL/RECURSIVE/ERRORCHECK self-lock behavior and thread-owner IDs.",
            "actions": [
                f"Trace lock/unlock ownership for {argument0 or 'the arg0 mutex address'} and compare the current thread handle with OwnerThreadId.",
                "Verify GuestThreadExecution.IsGuestThread and TryGetCurrentImportCallFrame are true on the contended path so RequestCurrentThreadBlock is actually used.",
                "Break on scePthreadMutexUnlock (NID tn3VlD0hG60) and confirm it removes/grants a waiter and signals the same wake key.",
                "Do not simply increase or disable SHARPEMU_IMPORT_LOOP_GUARD_SECONDS; that masks the synchronization bug.",
            ],
            "evidence": evidence,
            "kind": kind,
            "category": category,
        }

    if category == "condition":
        title = "Condition wait is not sleeping or waking correctly"
        cause = "The condition wait is returning into the guest retry loop instead of atomically releasing its mutex and parking, or its signal/broadcast path never wakes the waiter."
        fix = "Audit the condition wait lifecycle: release the mutex, enqueue and block the guest thread, then reacquire the mutex only after signal, broadcast, or timeout completion."
        actions = [
            "Trace the condition and mutex addresses passed in arg0/arg1.",
            "Verify signal/broadcast targets the same condition key and wakes at least one queued guest waiter.",
            "Check timeout clock units and ensure a timed wait does not immediately retry forever.",
        ]
    elif category == "wait":
        title = "Kernel wait primitive is not reaching completion"
        cause = "The wait call returns or is redispatched without the object becoming signaled, usually because the waiter was not parked or the producer does not issue its wake/completion transition."
        fix = "Connect the wait export to guest-thread blocking and audit the matching signal/post/event producer so it updates state before waking the waiter."
        actions = [
            "Trace the waited object address/ID and its signal/post counterpart.",
            "Confirm the wait path queues once instead of adding or polling a waiter on every import call.",
            "Verify completion writes all output fields and returns the guest-expected status code.",
        ]
    elif category == "timeline":
        title = "Producer timeline is not advancing"
        cause = "The guest repeatedly waits for a vblank/GPU/event milestone whose producer counter or completion fence is not being advanced."
        fix = "Audit the producer pump and completion callback, then ensure the wait export blocks until the timeline changes instead of immediately returning unchanged state."
        actions = [
            "Compare the requested timeline/fence value with the current producer value.",
            "Confirm the vblank, submit, or completion pump is running on the host.",
            "Verify a completion wakes every waiter registered for the reached value.",
        ]
    elif category == "sleep":
        title = "Sleep/yield boundary is being redispatched"
        cause = "The delay call is not yielding host execution or advancing the guest-visible clock, so the guest immediately retries its timing loop."
        fix = "Treat the call as a scheduler boundary, block for the requested duration using the correct clock units, and update guest-visible time before resuming."
        actions = [
            "Verify the delay argument units and overflow handling.",
            "Confirm the guest thread yields instead of busy-returning on the same host thread.",
            "Check monotonic clock progression observed by the game's follow-up time query.",
        ]
    else:
        title = "Resolved HLE import is not making forward progress"
        cause = f"{export_label} exists, but its return value or side effects leave the guest condition unchanged, causing the same import path to repeat."
        fix = "Audit the export contract: return status, output pointers, state transitions, and any scheduler wake/completion it promises. Compare those effects with the condition checked at the repeating guest return site."
        actions = [
            "Trace the import arguments, return value, and output memory for several consecutive iterations.",
            "Identify the guest branch at the repeating return site and the exact state it expects to change.",
            "Add a focused regression test for that state transition before changing the loop detector.",
        ]

    return {
        "severity": "High",
        "confidence": "Medium" if category == "generic" else "High",
        "title": title,
        "summary": f"{export_label} repeats without forward progress.",
        "cause": cause,
        "fix": fix,
        "actions": actions,
        "evidence": evidence,
        "kind": kind,
        "category": category,
    }


def _parse_stall_detail(detail: str) -> dict[str, Any]:
    patterns = {
        "kind": r"(?:^|,\s*)kind=([^,]+)",
        "nid": r"(?:^|,\s*)nid=([^,]+)",
        "instructionPointer": r"(?:^|,\s*)rip=(0x[0-9a-fA-F]+)",
        "dispatchIndex": r"(?:^|,\s*)dispatch#(\d+)",
        "argument0": r"(?:^|,\s*)arg0=(0x[0-9a-fA-F]+)",
        "argument1": r"(?:^|,\s*)arg1=(0x[0-9a-fA-F]+)",
    }
    result: dict[str, Any] = {}
    for key, pattern in patterns.items():
        match = re.search(pattern, detail)
        if match:
            result[key] = int(match.group(1)) if key == "dispatchIndex" else match.group(1).strip()
    return result


def _classify_stall_function(function: str | None) -> str:
    normalized = (function or "").lower()
    if "mutex" in normalized and ("lock" in normalized or "wait" in normalized):
        return "mutex"
    if "cond" in normalized and "wait" in normalized:
        return "condition"
    if any(token in normalized for token in ("vblank", "waitregmem", "fence", "flip")):
        return "timeline"
    if any(token in normalized for token in ("usleep", "nanosleep", "sleep")):
        return "sleep"
    if any(token in normalized for token in ("wait", "sema", "event")):
        return "wait"
    return "generic"


def _parse_optional_int(value: Any) -> int | None:
    try:
        return int(value) if value is not None else None
    except (TypeError, ValueError):
        return None


def _attach_stop_analysis(stop: dict[str, Any]) -> dict[str, Any]:
    decorated = copy.deepcopy(stop)
    analysis = analyze_debug_stop(decorated)
    if analysis is not None:
        decorated["analysis"] = analysis
    return decorated


class BridgeError(RuntimeError):
    """Raised when the debugger bridge cannot complete an operation."""


class EventJournal:
    """A bounded, cursor-addressable activity stream for the browser."""

    def __init__(self, capacity: int = MAX_JOURNAL_MESSAGES) -> None:
        self._lock = threading.Lock()
        self._messages: deque[dict[str, Any]] = deque(maxlen=capacity)
        self._next_id = 1

    def append(self, kind: str, summary: str, payload: Any = None) -> None:
        with self._lock:
            item = {
                "id": self._next_id,
                "time": time.strftime("%H:%M:%S"),
                "kind": kind,
                "summary": summary,
            }
            if payload is not None:
                item["payload"] = copy.deepcopy(payload)
            self._messages.append(item)
            self._next_id += 1

    def since(self, cursor: int) -> tuple[list[dict[str, Any]], int]:
        with self._lock:
            messages = [copy.deepcopy(item) for item in self._messages if item["id"] > cursor]
            return messages, self._next_id - 1


class EmulatorProcessManager:
    """Launches and supervises the one emulator process owned by the UI."""

    def __init__(self, journal: EventJournal) -> None:
        self.journal = journal
        self._lock = threading.RLock()
        self._process: subprocess.Popen[str] | None = None
        self._reader_thread: threading.Thread | None = None
        self._eboot_path: str | None = None
        self._emulator_path: str | None = None
        self._started_at: str | None = None
        self._exit_code: int | None = None

    def launch(
        self,
        eboot_path: str,
        debug_port: int,
        emulator_path: str | None = None,
    ) -> dict[str, Any]:
        eboot = Path(eboot_path).expanduser().resolve()
        if not eboot.is_file():
            raise BridgeError(f"EBOOT file does not exist: {eboot}")
        if debug_port < 1 or debug_port > 65535:
            raise BridgeError("The debugger port must be between 1 and 65535.")
        if self._port_is_open("127.0.0.1", debug_port):
            raise BridgeError(f"Port {debug_port} is already in use. Stop the existing debugger or choose another port.")

        executable = self.resolve_emulator(emulator_path)
        with self._lock:
            self._refresh_process_locked()
            if self._process is not None and self._process.poll() is None:
                raise BridgeError("An emulator launched by this frontend is already running.")

            environment = os.environ.copy()
            local_dotnet = REPO_ROOT.parent / ".dotnet"
            if local_dotnet.is_dir():
                environment["DOTNET_ROOT"] = str(local_dotnet)
                environment["PATH"] = f"{local_dotnet}{os.pathsep}{environment.get('PATH', '')}"

            command = [
                str(executable),
                f"--debug-server=127.0.0.1:{debug_port}",
                str(eboot),
            ]
            popen_options: dict[str, Any] = {
                "cwd": str(REPO_ROOT),
                "env": environment,
                "stdout": subprocess.PIPE,
                "stderr": subprocess.STDOUT,
                "text": True,
                "encoding": "utf-8",
                "errors": "replace",
                "bufsize": 1,
            }
            if os.name == "nt":
                popen_options["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
            else:
                popen_options["start_new_session"] = True

            try:
                process = subprocess.Popen(command, **popen_options)
            except OSError as exc:
                raise BridgeError(f"Could not launch SharpEmu: {exc}") from exc

            self._process = process
            self._eboot_path = str(eboot)
            self._emulator_path = str(executable)
            self._started_at = time.strftime("%H:%M:%S")
            self._exit_code = None
            self.journal.append("emulator", f"Launched {eboot.name} (PID {process.pid})", {
                "executable": str(executable),
                "eboot": str(eboot),
                "debugPort": debug_port,
            })
            reader_thread = threading.Thread(
                target=self._read_output,
                args=(process,),
                name="sharpemu-emulator-output",
                daemon=True,
            )
            self._reader_thread = reader_thread
            reader_thread.start()
            return self._snapshot_locked()

    def stop(self, timeout: float = 5.0, log: bool = True) -> dict[str, Any]:
        with self._lock:
            self._refresh_process_locked()
            process = self._process
            if process is None or process.poll() is not None:
                return self._snapshot_locked()
            if log:
                self.journal.append("emulator", f"Stopping emulator (PID {process.pid})")

        try:
            if os.name == "nt":
                process.terminate()
            else:
                os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=timeout)
        except ProcessLookupError:
            pass
        except subprocess.TimeoutExpired:
            self.journal.append("error", "Emulator did not stop gracefully; terminating it now.")
            try:
                if os.name == "nt":
                    process.kill()
                else:
                    os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=2)

        with self._lock:
            self._refresh_process_locked()
            return self._snapshot_locked()

    def wait_for_debugger(self, port: int, timeout: float = 12.0) -> None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            with self._lock:
                self._refresh_process_locked()
                process = self._process
                exit_code = self._exit_code
            if process is None or exit_code is not None:
                raise BridgeError(f"SharpEmu exited before its debugger started (exit code {exit_code}).")
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.25):
                    return
            except OSError:
                time.sleep(0.15)
        raise BridgeError(f"SharpEmu did not open debugger port {port} within {timeout:g} seconds.")

    def select_eboot(self, initial_path: str | None = None) -> str | None:
        initial = self._picker_initial_path(initial_path)
        zenity = shutil.which("zenity")
        kdialog = shutil.which("kdialog")
        if zenity:
            command = [
                zenity,
                "--file-selection",
                "--title=Choose a SharpEmu eboot",
                "--file-filter=Game executable | eboot.bin *.bin *.elf",
                "--file-filter=All files | *",
            ]
            if initial:
                command.append(f"--filename={initial}")
        elif kdialog:
            command = [
                kdialog,
                "--getopenfilename",
                initial or str(Path.home()),
                "Game executable (eboot.bin *.bin *.elf)",
                "--title",
                "Choose a SharpEmu eboot",
            ]
        else:
            raise BridgeError("No desktop file picker was found. Enter the full eboot path manually.")

        try:
            result = subprocess.run(command, capture_output=True, text=True, check=False)
        except OSError as exc:
            raise BridgeError(f"Could not open the file picker: {exc}") from exc
        if result.returncode != 0:
            return None
        selected = result.stdout.strip()
        return str(Path(selected).expanduser().resolve()) if selected else None

    def resolve_emulator(self, explicit_path: str | None = None) -> Path:
        if explicit_path:
            candidate = Path(explicit_path).expanduser().resolve()
        else:
            system = platform.system().lower()
            machine = platform.machine().lower()
            rid = {
                ("linux", "x86_64"): "linux-x64",
                ("linux", "amd64"): "linux-x64",
                ("windows", "amd64"): "win-x64",
                ("windows", "x86_64"): "win-x64",
                ("darwin", "x86_64"): "osx-x64",
            }.get((system, machine))
            executable_name = "SharpEmu.exe" if os.name == "nt" else "SharpEmu"
            candidates: list[Path] = []
            if rid:
                candidates.append(REPO_ROOT / "artifacts" / "bin" / "Release" / "net10.0" / rid / executable_name)
            candidates.extend(
                path for path in (REPO_ROOT / "artifacts" / "bin" / "Release" / "net10.0").glob(f"*/{executable_name}")
                if path not in candidates
            )
            candidate = next((path.resolve() for path in candidates if path.is_file()), candidates[0] if candidates else Path())

        if not candidate.is_file():
            raise BridgeError(
                "The SharpEmu Release executable was not found. Build the solution first with "
                "'dotnet build SharpEmu.slnx --configuration Release'."
            )
        if os.name != "nt" and not os.access(candidate, os.X_OK):
            raise BridgeError(f"The SharpEmu executable is not runnable: {candidate}")
        return candidate

    def snapshot(self) -> dict[str, Any]:
        with self._lock:
            self._refresh_process_locked()
            return self._snapshot_locked()

    def _read_output(self, process: subprocess.Popen[str]) -> None:
        output = process.stdout
        if output is not None:
            try:
                for line in output:
                    text = line.rstrip("\r\n")
                    if text:
                        self.journal.append("emulator", text[:4000])
            except (OSError, ValueError):
                pass
            finally:
                output.close()
        try:
            process.wait()
        except OSError:
            pass
        with self._lock:
            self._refresh_process_locked()

    def _refresh_process_locked(self) -> None:
        if self._process is None:
            return
        exit_code = self._process.poll()
        if exit_code is not None and self._exit_code is None:
            self._exit_code = exit_code
            kind = "emulator" if exit_code == 0 else "error"
            self.journal.append(kind, f"Emulator exited with code {exit_code}")

    def _snapshot_locked(self) -> dict[str, Any]:
        process = self._process
        running = process is not None and process.poll() is None
        return {
            "running": running,
            "pid": process.pid if running else None,
            "eboot": self._eboot_path,
            "executable": self._emulator_path,
            "startedAt": self._started_at,
            "exitCode": self._exit_code,
        }

    @staticmethod
    def _port_is_open(host: str, port: int) -> bool:
        try:
            with socket.create_connection((host, port), timeout=0.2):
                return True
        except OSError:
            return False

    @staticmethod
    def _picker_initial_path(initial_path: str | None) -> str | None:
        if not initial_path:
            return None
        path = Path(initial_path).expanduser()
        if path.is_file():
            return str(path.resolve())
        if path.is_dir():
            return f"{path.resolve()}{os.sep}"
        parent = path.parent
        return f"{parent.resolve()}{os.sep}" if parent.is_dir() else None


class DebuggerBridge:
    """Owns one debugger socket and serializes request/reply traffic."""

    def __init__(self, default_host: str = "127.0.0.1", default_port: int = 5714) -> None:
        self.default_host = default_host
        self.default_port = default_port
        self.journal = EventJournal()

        self._state_lock = threading.RLock()
        self._lifecycle_lock = threading.RLock()
        self._command_lock = threading.Lock()
        self._send_lock = threading.Lock()
        self._responses: queue.Queue[tuple[int, Any]] = queue.Queue()
        self._generation = 0
        self._hello_event = threading.Event()
        self._socket: socket.socket | None = None
        self._reader_thread: threading.Thread | None = None

        self._connected = False
        self._endpoint: str | None = None
        self._protocol: str | None = None
        self._target_state = "Disconnected"
        self._last_stop: dict[str, Any] | None = None
        self._registers: dict[str, Any] = {}
        self._breakpoints: list[dict[str, Any]] = []

    def connect(self, host: str, port: int, timeout: float = 3.0) -> None:
        with self._lifecycle_lock:
            self._connect(host, port, timeout)

    def _connect(self, host: str, port: int, timeout: float) -> None:
        host = host.strip()
        if not host:
            raise BridgeError("A debugger host is required.")
        if port < 1 or port > 65535:
            raise BridgeError("The debugger port must be between 1 and 65535.")

        self.disconnect(log=False)
        self._drain_responses()
        try:
            client = socket.create_connection((host, port), timeout=timeout)
            client.settimeout(None)
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            reader = client.makefile("r", encoding="utf-8", newline="\n")
        except OSError as exc:
            message = f"Could not connect to {host}:{port}: {exc}"
            self.journal.append("error", message)
            raise BridgeError(message) from exc

        with self._state_lock:
            self._generation += 1
            generation = self._generation
            self._socket = client
            self._connected = True
            self._endpoint = f"{host}:{port}"
            self._protocol = None
            self._target_state = "Connecting"
            self._last_stop = None
            self._registers = {}
            self._breakpoints = []
            self._hello_event = threading.Event()

        self.journal.append("system", f"Connected to {host}:{port}")
        thread = threading.Thread(
            target=self._read_loop,
            args=(generation, client, reader),
            name="sharpemu-debug-reader",
            daemon=True,
        )
        self._reader_thread = thread
        thread.start()

        self._hello_event.wait(timeout=min(timeout, 1.5))
        with self._state_lock:
            if generation != self._generation or not self._connected:
                raise BridgeError(f"The debugger at {host}:{port} closed the connection.")

    def disconnect(self, reason: str = "Disconnected", log: bool = True) -> None:
        with self._lifecycle_lock:
            self._disconnect(reason, log)

    def _disconnect(self, reason: str, log: bool) -> None:
        with self._state_lock:
            client = self._socket
            was_connected = self._connected
            self._generation += 1
            generation = self._generation
            self._socket = None
            self._connected = False
            self._protocol = None
            self._target_state = "Disconnected"
            self._last_stop = None
            self._registers = {}
            self._breakpoints = []

        if client is not None:
            try:
                client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            client.close()

        if was_connected:
            self._responses.put((generation - 1, BridgeError(reason)))
            if log:
                self.journal.append("system", reason)

    def request(self, payload: dict[str, Any], timeout: float = 6.0) -> dict[str, Any]:
        command = payload.get("command")
        if not isinstance(command, str) or not command.strip():
            raise BridgeError("Every request requires a non-empty 'command'.")

        wire_data = (json.dumps(payload, separators=(",", ":")) + "\n").encode("utf-8")
        if len(wire_data) > MAX_REQUEST_BYTES:
            raise BridgeError("The debugger request is too large.")

        with self._command_lock:
            with self._state_lock:
                client = self._socket
                generation = self._generation
                connected = self._connected
            if client is None or not connected:
                raise BridgeError("Not connected to a SharpEmu debugger.")

            try:
                with self._send_lock:
                    client.sendall(wire_data)
            except OSError as exc:
                self._connection_lost(generation, f"Connection lost while sending: {exc}")
                raise BridgeError("The debugger connection was lost while sending.") from exc

            self.journal.append("request", command, payload)
            deadline = time.monotonic() + timeout
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    self.disconnect(f"Debugger request '{command}' timed out.")
                    raise BridgeError(f"Debugger request '{command}' timed out.")
                try:
                    response_generation, item = self._responses.get(timeout=remaining)
                except queue.Empty as exc:
                    self.disconnect(f"Debugger request '{command}' timed out.")
                    raise BridgeError(f"Debugger request '{command}' timed out.") from exc
                if response_generation != generation:
                    continue
                if isinstance(item, BaseException):
                    raise BridgeError(str(item)) from item
                response = item
                break

            self._apply_response(response)
            if response.get("ok"):
                self.journal.append("response", f"{command} succeeded", response)
            else:
                self.journal.append("error", response.get("error", f"{command} failed"), response)
            return response

    def snapshot(self, cursor: int = 0) -> dict[str, Any]:
        messages, next_cursor = self.journal.since(max(0, cursor))
        with self._state_lock:
            return {
                "connected": self._connected,
                "endpoint": self._endpoint,
                "defaultEndpoint": {
                    "host": self.default_host,
                    "port": self.default_port,
                },
                "protocol": self._protocol,
                "state": self._target_state,
                "lastStop": copy.deepcopy(self._last_stop),
                "registers": copy.deepcopy(self._registers),
                "breakpoints": copy.deepcopy(self._breakpoints),
                "messages": messages,
                "cursor": next_cursor,
            }

    def _read_loop(
        self,
        generation: int,
        client: socket.socket,
        reader: Any,
    ) -> None:
        reason = "Debugger closed the connection."
        try:
            while True:
                line = reader.readline()
                if line == "":
                    break
                line = line.strip()
                if not line:
                    continue
                try:
                    message = json.loads(line)
                except json.JSONDecodeError:
                    self.journal.append("error", "Debugger sent malformed JSON", line)
                    continue
                if not isinstance(message, dict):
                    self.journal.append("error", "Debugger sent a non-object message", message)
                    continue

                if "event" in message:
                    self._apply_event(message)
                    event_name = str(message.get("event", "event"))
                    self.journal.append("event", event_name, message)
                else:
                    self._responses.put((generation, message))
        except (OSError, ValueError) as exc:
            reason = f"Debugger connection lost: {exc}"
        finally:
            try:
                reader.close()
            except OSError:
                pass
            try:
                client.close()
            except OSError:
                pass
            self._connection_lost(generation, reason)

    def _connection_lost(self, generation: int, reason: str) -> None:
        with self._state_lock:
            if generation != self._generation:
                return
            self._socket = None
            self._connected = False
            self._protocol = None
            self._target_state = "Disconnected"
            self._last_stop = None
            self._registers = {}
            self._breakpoints = []
            self._generation += 1
        self._responses.put((generation, BridgeError(reason)))
        self.journal.append("error", reason)

    def _apply_event(self, message: dict[str, Any]) -> None:
        event = str(message.get("event", "")).lower()
        with self._state_lock:
            if event == "hello":
                self._protocol = str(message.get("protocol", "unknown"))
                self._target_state = str(message.get("state", "Connected"))
                self._hello_event.set()
            elif event == "stopped":
                self._target_state = "Paused"
                self._last_stop = _attach_stop_analysis({
                    key: copy.deepcopy(value) for key, value in message.items() if key != "event"
                })
                registers = message.get("registers")
                if isinstance(registers, dict):
                    self._registers = copy.deepcopy(registers)
            elif event == "resumed":
                self._target_state = "Running"
            elif event == "terminated":
                self._target_state = "Terminated"

    def _apply_response(self, response: dict[str, Any]) -> None:
        if not response.get("ok"):
            return
        command = str(response.get("command", "")).lower()
        data = response.get("data")
        if not isinstance(data, dict):
            return
        with self._state_lock:
            if command in {"status", "info"}:
                self._target_state = str(data.get("state", self._target_state))
                last_stop = data.get("lastStop")
                if isinstance(last_stop, dict):
                    self._last_stop = _attach_stop_analysis(last_stop)
                    registers = last_stop.get("registers")
                    if isinstance(registers, dict):
                        self._registers = copy.deepcopy(registers)
            elif command == "state":
                self._target_state = str(data.get("state", self._target_state))
            elif command in {"registers", "regs"}:
                registers = data.get("registers")
                if isinstance(registers, dict):
                    self._registers = copy.deepcopy(registers)
            elif command in {"list-breakpoints", "breakpoints"}:
                breakpoints = data.get("breakpoints")
                if isinstance(breakpoints, list):
                    self._breakpoints = copy.deepcopy(breakpoints)

    def _drain_responses(self) -> None:
        while True:
            try:
                self._responses.get_nowait()
            except queue.Empty:
                return


class FrontendRequestHandler(BaseHTTPRequestHandler):
    """Serves static UI assets and the local bridge API."""

    bridge: DebuggerBridge
    process_manager: EmulatorProcessManager
    verbose = False

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        parsed = urlparse(self.path)
        if parsed.path == "/api/snapshot":
            values = parse_qs(parsed.query)
            try:
                cursor = int(values.get("since", ["0"])[0])
            except ValueError:
                cursor = 0
            self._send_json(HTTPStatus.OK, self._combined_snapshot(cursor))
            return
        if parsed.path == "/api/health":
            self._send_json(HTTPStatus.OK, {
                "ok": True,
                "application": APPLICATION_ID,
                "pid": os.getpid(),
            })
            return

        asset_map = {
            "/": ("index.html", "text/html; charset=utf-8"),
            "/index.html": ("index.html", "text/html; charset=utf-8"),
            "/app.js": ("app.js", "text/javascript; charset=utf-8"),
            "/styles.css": ("styles.css", "text/css; charset=utf-8"),
            "/sharpemu-logo.webp": ("sharpemu-logo.webp", "image/webp"),
        }
        asset = asset_map.get(parsed.path)
        if asset is None:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        file_name, content_type = asset
        try:
            body = (WEB_ROOT / file_name).read_bytes()
        except OSError:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header(
            "Content-Security-Policy",
            "default-src 'self'; connect-src 'self'; img-src 'self' data:; "
            "script-src 'self'; style-src 'self'; base-uri 'none'; frame-ancestors 'none'",
        )
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        parsed = urlparse(self.path)
        try:
            body = self._read_json_body()
            if parsed.path == "/api/connect":
                host = str(body.get("host", self.bridge.default_host)).strip()
                port = int(body.get("port", self.bridge.default_port))
                self.bridge.connect(host, port)
                self._prime_snapshot()
                self._send_json(HTTPStatus.OK, self._combined_snapshot())
                return
            if parsed.path == "/api/disconnect":
                self.bridge.disconnect()
                self._send_json(HTTPStatus.OK, self._combined_snapshot())
                return
            if parsed.path == "/api/select-eboot":
                selected = self.process_manager.select_eboot(body.get("initialPath"))
                self._send_json(HTTPStatus.OK, {"path": selected})
                return
            if parsed.path == "/api/launch":
                eboot_path = body.get("ebootPath")
                if not isinstance(eboot_path, str) or not eboot_path.strip():
                    raise BridgeError("Choose an eboot file before launching.")
                debug_port = int(body.get("debugPort", self.bridge.default_port))
                emulator_path = body.get("emulatorPath")
                if emulator_path is not None and not isinstance(emulator_path, str):
                    raise BridgeError("'emulatorPath' must be a string.")
                self.bridge.disconnect(log=False)
                try:
                    self.process_manager.launch(eboot_path, debug_port, emulator_path)
                    self.process_manager.wait_for_debugger(debug_port)
                    self.bridge.connect("127.0.0.1", debug_port)
                    self._prime_snapshot()
                except BridgeError:
                    self.process_manager.stop(log=False)
                    raise
                self._send_json(HTTPStatus.OK, self._combined_snapshot())
                return
            if parsed.path == "/api/stop-emulator":
                self.bridge.disconnect(log=False)
                self.process_manager.stop()
                self._send_json(HTTPStatus.OK, self._combined_snapshot())
                return
            if parsed.path == "/api/shutdown":
                self.bridge.journal.append("system", "A replacement frontend requested shutdown.")
                self._send_json(HTTPStatus.OK, {"ok": True, "pid": os.getpid()})
                threading.Thread(
                    target=self._shutdown_http_server,
                    name="sharpemu-frontend-shutdown",
                    daemon=True,
                ).start()
                return
            if parsed.path == "/api/command":
                request = body.get("request")
                if not isinstance(request, dict):
                    raise BridgeError("Expected a JSON object in 'request'.")
                response = self.bridge.request(request)
                self._send_json(HTTPStatus.OK, {"response": response})
                return
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "Unknown API endpoint."})
        except (BridgeError, ValueError, TypeError) as exc:
            self._send_json(HTTPStatus.BAD_GATEWAY, {"error": str(exc)})
        except json.JSONDecodeError:
            self._send_json(HTTPStatus.BAD_REQUEST, {"error": "Request body is not valid JSON."})

    def _prime_snapshot(self) -> None:
        try:
            self.bridge.request({"command": "status"})
            self.bridge.request({"command": "list-breakpoints"})
        except BridgeError as exc:
            self.bridge.journal.append("error", f"Initial refresh failed: {exc}")

    def _combined_snapshot(self, cursor: int = 0) -> dict[str, Any]:
        snapshot = self.bridge.snapshot(cursor)
        snapshot["emulator"] = self.process_manager.snapshot()
        return snapshot

    def _shutdown_http_server(self) -> None:
        self.server.shutdown()
        self.server.server_close()

    def _read_json_body(self) -> dict[str, Any]:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError as exc:
            raise BridgeError("Invalid Content-Length header.") from exc
        if length < 0 or length > MAX_REQUEST_BYTES:
            raise BridgeError("HTTP request body is too large.")
        raw = self.rfile.read(length)
        value = json.loads(raw.decode("utf-8") if raw else "{}")
        if not isinstance(value, dict):
            raise BridgeError("Expected a JSON object request body.")
        return value

    def _send_json(self, status: HTTPStatus, payload: Any) -> None:
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        try:
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def log_message(self, format_string: str, *args: Any) -> None:
        if self.verbose:
            super().log_message(format_string, *args)


class FrontendHttpServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def create_frontend_server(
    listen_address: str,
    port: int,
    handler_type: type[FrontendRequestHandler],
) -> FrontendHttpServer:
    """Binds the UI server, replacing an older frontend on the same port."""

    try:
        return FrontendHttpServer((listen_address, port), handler_type)
    except OSError as exc:
        if exc.errno != errno.EADDRINUSE or port == 0:
            raise SystemExit(f"Could not start the frontend on {listen_address}:{port}: {exc}") from None

    if not request_existing_frontend_shutdown(listen_address, port):
        raise SystemExit(
            f"Port {port} is already in use by another application. "
            "Choose a different port with --ui-port."
        )

    deadline = time.monotonic() + 10.0
    while time.monotonic() < deadline:
        try:
            return FrontendHttpServer((listen_address, port), handler_type)
        except OSError as exc:
            if exc.errno != errno.EADDRINUSE:
                raise SystemExit(f"Could not start the frontend on {listen_address}:{port}: {exc}") from None
            time.sleep(0.15)
    raise SystemExit(f"The previous SharpEmu frontend did not release port {port} in time.")


def request_existing_frontend_shutdown(listen_address: str, port: int) -> bool:
    """Stops only a verified SharpEmu frontend listening on the target port."""

    probe_host = "127.0.0.1" if listen_address in {"0.0.0.0", "localhost"} else listen_address
    url_host = f"[{probe_host}]" if ":" in probe_host and not probe_host.startswith("[") else probe_host
    base_url = f"http://{url_host}:{port}"
    try:
        with urlopen(f"{base_url}/api/health", timeout=0.75) as response:
            health = json.load(response)
    except (OSError, URLError, ValueError, json.JSONDecodeError):
        return False

    if isinstance(health, dict) and health.get("application") == APPLICATION_ID:
        request = Request(
            f"{base_url}/api/shutdown",
            data=b"{}",
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urlopen(request, timeout=1.5) as response:
                result = json.load(response)
            return isinstance(result, dict) and result.get("ok") is True
        except (OSError, URLError, ValueError, json.JSONDecodeError):
            return False

    # Frontends created before the shutdown API only returned {"ok": true}.
    # Verify both their page identity and the exact owning process before asking
    # that legacy instance to exit through its normal Ctrl+C cleanup path.
    if not isinstance(health, dict) or health.get("ok") is not True:
        return False
    try:
        with urlopen(f"{base_url}/", timeout=0.75) as response:
            page = response.read(128 * 1024)
    except (OSError, URLError):
        return False
    if b"<title>SharpEmu Debugger</title>" not in page:
        return False
    legacy_pid = find_legacy_frontend_pid(port)
    if legacy_pid is None:
        return False
    try:
        os.kill(legacy_pid, signal.SIGINT)
        return True
    except (OSError, ProcessLookupError):
        return False


def find_legacy_frontend_pid(port: int) -> int | None:
    """Finds the verified legacy frontend owning a Linux listening socket."""

    if platform.system() != "Linux" or shutil.which("ss") is None:
        return None
    try:
        result = subprocess.run(
            ["ss", "-ltnp", f"( sport = :{port} )"],
            capture_output=True,
            text=True,
            timeout=2,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    script_path = str(Path(__file__).resolve())
    for match in re.finditer(r"pid=(\d+)", result.stdout):
        pid = int(match.group(1))
        if pid == os.getpid():
            continue
        try:
            command_line = Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" ").decode("utf-8", "replace")
        except OSError:
            continue
        if script_path in command_line:
            return pid
    return None


def build_argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="SharpEmu live debugger web frontend")
    parser.add_argument("--debug-host", default="127.0.0.1", help="SharpEmu debugger host")
    parser.add_argument("--debug-port", type=int, default=5714, help="SharpEmu debugger port")
    parser.add_argument("--listen", default="127.0.0.1", help="Frontend HTTP bind address")
    parser.add_argument("--ui-port", type=int, default=8765, help="Frontend HTTP port (0 chooses a free port)")
    parser.add_argument("--no-connect", action="store_true", help="Do not connect to SharpEmu on startup")
    parser.add_argument("--no-browser", action="store_true", help="Do not open the frontend in a browser")
    parser.add_argument("--verbose", action="store_true", help="Print HTTP request logs")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_argument_parser().parse_args(argv)
    if args.debug_port < 1 or args.debug_port > 65535:
        raise SystemExit("--debug-port must be between 1 and 65535")
    if args.ui_port < 0 or args.ui_port > 65535:
        raise SystemExit("--ui-port must be between 0 and 65535")

    bridge = DebuggerBridge(args.debug_host, args.debug_port)
    process_manager = EmulatorProcessManager(bridge.journal)
    handler_type = type(
        "ConfiguredFrontendRequestHandler",
        (FrontendRequestHandler,),
        {"bridge": bridge, "process_manager": process_manager, "verbose": args.verbose},
    )
    server = create_frontend_server(args.listen, args.ui_port, handler_type)
    actual_port = server.server_address[1]
    browser_host = args.listen if args.listen not in {"0.0.0.0", "::"} else "127.0.0.1"
    url = f"http://{browser_host}:{actual_port}/"

    print(f"SharpEmu Debugger Frontend: {url}")
    print(f"Debugger endpoint: {args.debug_host}:{args.debug_port}")
    print("Press Ctrl+C to stop.")
    if args.listen not in {"127.0.0.1", "localhost", "::1"}:
        print("Warning: the frontend is listening beyond loopback; no authentication is provided.")

    if not args.no_connect:
        def connect_default() -> None:
            try:
                bridge.connect(args.debug_host, args.debug_port)
                bridge.request({"command": "status"})
                bridge.request({"command": "list-breakpoints"})
            except BridgeError as exc:
                bridge.journal.append("system", f"Start SharpEmu with --debug-server, then reconnect: {exc}")

        threading.Thread(target=connect_default, name="sharpemu-auto-connect", daemon=True).start()

    if not args.no_browser:
        threading.Timer(0.35, lambda: webbrowser.open(url)).start()

    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        print("\nStopping frontend...")
    finally:
        bridge.disconnect(log=False)
        process_manager.stop(log=False)
        server.shutdown()
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
