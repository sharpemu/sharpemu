<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu.DebugClient

A small, standalone command-line client that connects to the SharpEmu
emulator's **live debug server** and drives it interactively. It ships as its
own executable (`SharpEmu.DebugClient`) and takes no dependency on the emulator
assemblies — it speaks the server's line-delimited JSON protocol directly over
TCP, so you can also drive the server from `nc`, a script, or your own tool.

> **Status:** infrastructure. The transport, protocol, session model, and
> breakpoint store are in place. Stops are delivered at **frame boundaries**
> (process entry and each module initializer); per-instruction stepping and data
> watchpoints are part of the surface and become live as the CPU backend grows
> the corresponding hooks. See [`docs/debugger-server.md`](../../docs/debugger-server.md)
> for the architecture and protocol reference.

## How it fits together

```
+-------------------------+           TCP (JSON lines)         +----------------------+
|  SharpEmu (emulator)    |  <------------------------------>  |  SharpEmu.DebugClient |
|  --debug-server         |                                    |  (this executable)    |
|                         |                                    |                       |
|  DebuggerServerHost     |                                    |  REPL / --exec        |
|   +- DebuggerServer     |  frame boundaries via ICpuDebugHook|                       |
|   +- DebuggerSession  <-+------ CPU dispatcher ------------  |                       |
+-------------------------+                                    +----------------------+
```

The emulator is the **server**; this client is a separate process that connects
to it and issues commands. The two never share memory — everything crosses the
socket as JSON.

## Building

```bash
dotnet build src/SharpEmu.DebugClient/SharpEmu.DebugClient.csproj
```

## Quick start

1. Launch the emulator with the debug server enabled. It listens on
   `127.0.0.1:5714` by default and, with stop-at-entry on, parks the guest at
   its first frame until you continue:

   ```bash
   SharpEmu --debug-server "/path/to/game/eboot.bin"
   # or choose an endpoint:
   SharpEmu --debug-server=127.0.0.1:5714 "/path/to/game/eboot.bin"
   ```

2. In another terminal, attach the client:

   ```bash
   SharpEmu.DebugClient                 # defaults to 127.0.0.1:5714
   SharpEmu.DebugClient 127.0.0.1:5714  # explicit endpoint
   ```

3. Drive the target:

   ```
   status
   regs
   break 0x00000008801234a0
   continue
   mem 0x00000008802000000 64
   ```

## Invocation

```
SharpEmu.DebugClient [host:port] [--exec "<command>"]... [--quiet]
```

| Option        | Meaning                                                        |
| ------------- | ------------------------------------------------------------- |
| `host:port`   | Server endpoint. Default `127.0.0.1:5714`. `localhost` is fine. |
| `--exec, -e`  | Run one command non-interactively, then exit. Repeatable.     |
| `--quiet`     | Suppress the connection banner.                               |
| `--help, -h`  | Show usage and the command list.                              |

Non-interactive example (scriptable):

```bash
SharpEmu.DebugClient --exec "break 0x8801234a0" --exec "continue"
```

## Commands

Addresses and values accept decimal or `0x`-prefixed hex. Register and memory
commands only succeed while the target is **paused**.

| Command | Server verb | Description |
| ------- | ----------- | ----------- |
| `status` \| `info` | `status` | Target state plus the last stop. |
| `state` | `state` | Run state only (`Running`/`Paused`/…). |
| `regs` \| `registers` | `registers` | Dump the integer registers. |
| `setreg <reg> <value>` | `set-register` | Set `rip`, `rflags`, or a GP register. |
| `mem <addr> <len>` \| `read <addr> <len>` | `read-memory` | Read guest memory as hex. |
| `write <addr> <hex>` | `write-memory` | Write guest memory from a hex string. |
| `break <addr> [kind] [len]` \| `b …` | `add-breakpoint` | Add a breakpoint. `kind`: `execute` (default), `readwatch`, `writewatch`, `accesswatch`. |
| `bp` \| `breakpoints` | `list-breakpoints` | List breakpoints. |
| `del <id>` \| `rm <id>` | `remove-breakpoint` | Remove a breakpoint. |
| `enable <id>` / `disable <id>` | `enable-breakpoint` | Toggle a breakpoint. |
| `continue` \| `c` | `continue` | Resume a paused target. |
| `step` \| `s` | `step` | Resume and stop at the next frame boundary. |
| `pause` | `pause` | Ask a running target to stop at the next boundary. |
| `ping` | `ping` | Round-trip liveness check. |
| `raw <json>` | *(passthrough)* | Send a literal JSON request. |
| `help` \| `?` | — | Show the command list (local). |
| `quit` \| `exit` | — | Disconnect and exit (local). |

## Output

The client prints two kinds of lines as they arrive:

- `reply>` — the response to a command you sent (`ok`, plus `data` or `error`).
- `event>` — an unsolicited notification: `hello` on connect, `stopped` when the
  target hits a breakpoint / entry / step / pause, `resumed` on continue, and
  `terminated` when the run ends.

Because replies and events share one stream, the client prints everything it
receives rather than pairing replies to requests — a `stopped` event may arrive
between your command and its reply.

## Protocol (for building your own client)

One JSON object per line, UTF-8, `\n`-terminated, in both directions.

Request:

```json
{"command":"read-memory","address":"0x8802000000","length":64}
```

Reply:

```json
{"ok":true,"command":"read-memory","data":{"address":"0x0000000880200000","length":64,"bytes":"48894C24.."}}
```

Event:

```json
{"event":"stopped","reason":"Breakpoint","address":"0x00000008801234A0","frameKind":"ProcessEntry","frameLabel":"eboot.bin","registers":{ ... }}
```

The full verb list and payload fields live in
[`docs/debugger-server.md`](../../docs/debugger-server.md).
