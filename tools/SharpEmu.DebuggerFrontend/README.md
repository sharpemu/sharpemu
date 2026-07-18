<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu Debugger Frontend

A polished browser UI for SharpEmu's live debugger. A small Python bridge talks
to the emulator over its JSON-lines TCP protocol and serves the frontend on
loopback. It uses only the Python standard library; no package installation or
JavaScript build step is required.

The favicon and header mark use the official SharpEmu logo served by
`sharpemu.app`.

## Start

Start the frontend from the repository root:

```bash
./tools/SharpEmu.DebuggerFrontend/run.sh
```

Use **Browse…** to choose the game's `eboot.bin`, then select **Launch &
attach**. The frontend starts the local Release build with its debug server,
waits for it to become ready, and connects automatically. Emulator output is
shown in the Activity panel. The Stop button only controls the process launched
by this frontend.

You can still start SharpEmu manually and use the connection bar to attach:

```bash
./artifacts/bin/Release/net10.0/linux-x64/SharpEmu \
  --debug-server=127.0.0.1:5714 "/path/to/game/eboot.bin"
```

The frontend opens `http://127.0.0.1:8765/`. If SharpEmu is already running, it
connects to the default debug endpoint automatically. If it is not running yet,
the UI remains available for launching or attaching later.

Running the launcher again on the same UI port gracefully closes and replaces
the previous verified SharpEmu frontend instance. It will not stop an unrelated
application that happens to own the requested port; in that case, select a
different port with `--ui-port`.

Useful options:

```text
--debug-host HOST   Debug server host (default 127.0.0.1)
--debug-port PORT   Debug server port (default 5714)
--listen ADDRESS    Web UI bind address (default 127.0.0.1)
--ui-port PORT      Web UI port; 0 chooses a free port (default 8765)
--no-connect        Do not connect to SharpEmu automatically
--no-browser        Do not open a browser automatically
--verbose           Print HTTP request logs
```

## Features

- Live connection and target-state display
- Native file picker, local emulator launch, automatic debugger attach, and process stop
- Live output from the frontend-launched SharpEmu process
- Continue, pause, and frame-step controls with keyboard shortcuts
- Register inspection and editing
- Hex/ASCII memory reads and validated memory writes
- Breakpoint and watchpoint creation, toggling, and deletion
- Stop reason, frame, result, opcode, and fault details
- Evidence-based stall diagnosis with likely causes, ranked fixes, and targeted checks
- Raw JSON command console for new protocol operations
- Searchable activity stream containing requests, replies, and async events

The debugger currently stops and steps at guest frame boundaries. Data
watchpoints and per-instruction stepping are exposed in the protocol but depend
on future CPU backend hooks, as documented in `docs/debugger-server.md`.

## Test

```bash
python3 -m unittest discover -s tools/SharpEmu.DebuggerFrontend/tests -v
node --check tools/SharpEmu.DebuggerFrontend/web/app.js
```

The HTTP service binds to loopback by default and has no authentication. Only
use a non-loopback `--listen` address on a trusted network.

On Linux, the Browse button uses `zenity` or `kdialog`. A full path can always
be entered manually. Closing the frontend also stops the emulator process it
launched so its captured output pipe cannot be orphaned; manually launched
emulators are never stopped by the frontend.
