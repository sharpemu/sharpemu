<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Live debug server

SharpEmu can expose a **live debug server** so an external process can inspect
and control a running guest over TCP. The server lives in the emulator; the
companion `SharpEmu.DebugClient` executable is one client, and the wire protocol
is simple enough to script against directly.

This document describes the moving parts and the wire protocol. For day-to-day
client usage, see
[`src/SharpEmu.DebugClient/DEVELOPER_READ.md`](../src/SharpEmu.DebugClient/DEVELOPER_READ.md).

## Layering

| Assembly | Role |
| -------- | ---- |
| `SharpEmu.Core` | Defines the dispatcher seam `ICpuDebugHook` / `ICpuDebugFrame` (namespace `SharpEmu.Core.Cpu.Debug`) and the `CpuExecutionOptions.DebugHook` slot. Core has **no** reference to the debugger. |
| `SharpEmu.Debugger` | The debugger: `DebuggerSession` (implements the hook), `BreakpointStore`, the TCP `DebuggerServer`, the pluggable `IDebugProtocol` with a JSON-lines implementation, and the `DebuggerServerHost` one-call wiring. |
| `SharpEmu.CLI` | Parses `--debug-server`, builds a `DebuggerServerHost`, hands its `Hook` to `SharpEmuRuntimeOptions.DebugHook`, and manages its lifetime. |
| `SharpEmu.DebugClient` | A standalone client executable. Depends only on the BCL. |

The dependency direction is important: Core stays debugger-agnostic and only
publishes the seam. Anything that observes execution implements
`ICpuDebugHook` and is injected through the options, so the debugger can evolve
without touching the CPU core.

## Execution model

`CpuDispatcher` enters a fresh frame for the process entry point and for each
module initializer. When a `DebugHook` is attached it is notified at those
boundaries:

- `OnFrameEnter(frame)` — before the native backend runs the frame. The
  `DebuggerSession` decides whether to stop (pause request, breakpoint on the
  entry address, single-step, or stop-at-entry). To stop, it **parks the
  emulation thread** inside this call on a gate; the frame stays live, so a
  client can read and write registers and memory while parked. `continue` /
  `step` release the gate.
- `OnFrameExit(frame, result)` — after the frame completes.

Because pausing parks the one thread that owns the guest context, register and
memory accessors are only served while the session reports `Paused`; otherwise
they return "not paused" so a client never observes torn state.

### What is and isn't live yet

- **Live:** attach/handshake, run-state tracking, register read/write, memory
  read/write, breakpoint management, execution breakpoints at frame entry,
  pause, frame-level step, continue, and stop/resume/terminate events.
- **Surface only (armed as the backend grows hooks):** per-instruction
  stepping and data watchpoints (`readwatch` / `writewatch` / `accesswatch`).
  The verbs and types exist so clients and tooling can be written now.

## Enabling the server

```bash
SharpEmu --debug-server "/path/to/eboot.bin"            # 127.0.0.1:5714
SharpEmu --debug-server=0.0.0.0:5714 "/path/to/eboot.bin"
```

The bind address defaults to loopback; a routable address must be given
explicitly. With stop-at-entry (the default `DebuggerSessionOptions.StopAtEntry`),
the guest parks at its first frame until a client connects and issues
`continue`, giving you a window to set breakpoints before any guest code runs.

## Browser frontend

The dependency-free Python frontend can choose and launch an `eboot.bin`, attach
to its debugger automatically, and provides execution controls, registers,
memory inspection, breakpoint management, process output, and a live protocol
activity stream:

```bash
./tools/SharpEmu.DebuggerFrontend/run.sh
```

It connects to `127.0.0.1:5714` and opens `http://127.0.0.1:8765/` by default.
See [`tools/SharpEmu.DebuggerFrontend/README.md`](../tools/SharpEmu.DebuggerFrontend/README.md)
for configuration and testing options.

## Wire protocol (json-lines/1)

One JSON object per line, UTF-8, `\n`-terminated, in both directions.

### Requests

A `command` string plus command-specific fields. Numeric fields accept a JSON
number or a `0x`-prefixed hex string.

| `command` | Fields | Reply `data` |
| --------- | ------ | ------------ |
| `ping` | — | — |
| `status` (`info`) | — | `state`, `breakpoints`, `lastStop?` |
| `state` | — | `state` |
| `registers` (`regs`) | — | `registers` (rax..r15, rip, rflags, fs_base, gs_base) |
| `set-register` | `register`, `value` | — |
| `read-memory` | `address`, `length` (≤ 65536) | `address`, `length`, `bytes` (hex) |
| `write-memory` | `address`, `bytes` (hex) | `written` |
| `list-breakpoints` (`breakpoints`) | — | `breakpoints[]` |
| `add-breakpoint` (`break`) | `address`, `kind?`, `length?` | `breakpoint` |
| `remove-breakpoint` (`delete-breakpoint`) | `id` | — |
| `enable-breakpoint` | `id`, `enabled?` (default true) | — |
| `continue` (`cont`, `c`) | — | — |
| `step` (`s`) | — | — |
| `pause` | — | — |

### Replies

```json
{"ok":true,"command":"registers","data":{ "registers": { "rax":"0x…", … } }}
{"ok":false,"command":"read-memory","error":"Target is not paused."}
```

### Events (unsolicited)

```json
{"event":"hello","protocol":"json-lines/1","state":"Paused"}
{"event":"stopped","reason":"Breakpoint","address":"0x…","frameKind":"ProcessEntry","frameLabel":"eboot.bin","registers":{…},"breakpoint":{…}}
{"event":"resumed"}
{"event":"terminated"}
```

`reason` is one of `EntryPoint`, `Breakpoint`, `Watchpoint`, `Step`, `Pause`,
`Fault`, or `Stall`.

Stall stops include structured evidence in addition to the human-readable
detail. Import-loop evidence identifies the NID, resolved HLE export, repeating
guest return site, dispatch count, and first two ABI arguments:

```json
{
  "event": "stopped",
  "reason": "Stall",
  "stall": {
    "kind": "ImportLoop",
    "nid": "9UK1vLZQft4",
    "instructionPointer": "0x0000000801CE2418",
    "dispatchIndex": 40667904,
    "argument0": "0x0000000812345000",
    "argument1": "0x0000000000000000",
    "resolved": true,
    "library": "libKernel",
    "function": "scePthreadMutexLock"
  }
}
```

The Python frontend uses this evidence to explain the likely failure class and
rank concrete checks/fixes. Its diagnosis is intentionally labelled heuristic:
it helps locate the responsible HLE/scheduler path but does not replace tracing.

## Swapping the protocol

`DebuggerServer` takes an `IDebugProtocol` factory. The default is
`JsonLineDebugProtocol`; a GDB remote serial stub (or any other framing) can be
dropped in without changing the session or command semantics, which live in
`DebugCommandDispatcher`.

## Embedding the server

```csharp
using SharpEmu.Debugger;
using SharpEmu.Core.Runtime;

await using var host = new DebuggerServerHost();
host.Start();

var options = new SharpEmuRuntimeOptions { DebugHook = host.Hook };
using var runtime = SharpEmuRuntime.CreateDefault(options);
var result = runtime.Run(ebootPath);

host.NotifyRunCompleted();
```
