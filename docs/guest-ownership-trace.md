<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Guest ownership trace

Use this diagnostic to inspect memory at selected guest import calls. A JSON plan
selects callers, imports, registers, and memory regions. The trace does not search
for owners automatically. Compare captured pointers and contents to investigate
allocation, submission, and release sequences.

The default is off. This diagnostic reads guest state. It does not change guest
allocation, lock results, or completion rules. The extra work can change timing.

## Example plan

Save the following JSON as `ownership-plan.json`. The addresses and labels are
synthetic. Replace `Caller` and `Import` with values from the run under investigation.
This example will not select a real game call without those changes.

```json
[
  {
    "Caller": "0x100",
    "Import": "free",
    "Name": "release-entry",
    "Returned": false,
    "Capacity": 32,
    "Regions": [
      {
        "Name": "object",
        "Address": "arg0",
        "Length": "40"
      },
      {
        "Name": "linked-object",
        "Address": "arg0/8",
        "Length": "20"
      }
    ]
  }
]
```

This plan captures the first 32 matching calls before host dispatch. It reads
64 bytes at `arg0`. It also reads an eight-byte pointer at `arg0 + 8`, then reads
32 bytes at that pointer. All numeric strings in these expressions are hexadecimal.

## Run a capture

In PowerShell, run these commands from the folder containing `SharpEmu.exe` and
the plan. Start a new emulator process so it reads the environment variable.

```powershell
$env:SHARPEMU_TRACE_OWNERSHIP_PLAN = (Resolve-Path .\ownership-plan.json).Path
.\SharpEmu.exe
```

Run the selected scene, then close the game and launcher normally. The trace writes
to standard error at process exit. Use the normal run log if it captures standard
error. For command-line capture, redirect both streams to a fresh file:

```powershell
.\SharpEmu.exe > .\ownership-run.log 2>&1
```

Do not start a second run until the first process exits. Remove the setting after
the investigation:

```powershell
Remove-Item Env:SHARPEMU_TRACE_OWNERSHIP_PLAN
```

A missing file or invalid plan can prevent initialization. Use valid JSON with the
property names shown here. JSON comments and trailing commas are not supported.
Null capture points, null region arrays, and null region entries are rejected when
the plan loads. Each region needs a nonempty name, address, and length or end.

## Capture point fields

| Field | Meaning and default |
| --- | --- |
| `Caller` | Required nonzero hexadecimal guest return address. This is not the import entry address. Each caller can appear only once in a plan. |
| `Import` | Required exact export name, or the NID when no export name is available. Matching is case-sensitive. |
| `Name` | Required nonempty label, written as `phase` in the output. |
| `Returned` | `false` captures before host dispatch. `true` captures after normal host dispatch return. Default: `false`. |
| `Capacity` | Number of first events retained for this point, from 1 through 16384. Default: 4096. This is a JSON integer, not a hexadecimal string. |
| `Regions` | Array of up to 16 regions. Default: empty. Register records are still captured without regions. |

Caller selection checks the direct return address first. If that address is not
configured, it checks the saved frame caller. A wrapper can therefore affect which
address matches. Capture entry and return for the same caller in separate runs;
duplicate caller entries are rejected, even when `Returned` or `Import` differs.

## Region fields and expressions

| Field | Meaning and default |
| --- | --- |
| `Name` | Region label used in the output. Use a descriptive, nonempty label. |
| `Address` | Start-address expression. |
| `Length` | Byte-count expression. Default: `"40"`, which means 64 bytes. |
| `End` | Optional exclusive end-address expression. When present, it overrides `Length`. An end below the start is invalid. |
| `FollowPointers` | Default: `false`. When true, interpret each complete eight-byte word in the captured region as a little-endian pointer and read 64 bytes there. This follows one level only. |

Expression roots are hexadecimal values or these case-sensitive names:
`arg0`, `arg1`, `arg2`, `rbx`, `frame`, `r12`, `r13`, `r14`, `r15`, and `stack`.
`frame` is the saved RBP. `stack` points to the import-entry return address.
The register values come from the import argument packet, including for return captures.

Use `+`, `-`, and `*` for checked unsigned arithmetic. Use `/offset` to read an
eight-byte pointer at the current value plus that offset. Operations run strictly
left to right; there is no arithmetic precedence or parenthesis support.
Operands are hexadecimal constants, with an optional `0x` prefix. Do not add spaces.

- `arg0+10`: the first argument plus 16 bytes.
- `arg0/8+10`: read the pointer at the first argument plus 8, then add 16.
- `r12+20/0`: read the pointer at R12 plus 32.
- `arg2*8`: multiply the third argument by 8.

Expressions are limited to 256 characters and 16 operations. Invalid expressions,
overflow, underflow, and failed pointer reads produce unreadable region records.
Use `Length` or `End` to bound the region. `FollowPointers` is not a recursive search.

## Read the output

- `ownership-summary`: total events, accounted bytes, budget drops, and timestamp frequency.
- `ownership-point`: retained and dropped event counts for each point.
- `ownership-event`: timestamp ticks, host and guest thread IDs, caller, phase,
  registers, and dispatch result. The result is zero for entry captures.
- `ownership-region`: address, requested byte count, readability, truncation, and
  hexadecimal bytes. Followed pointers use names such as `object[0]`.

Divide a difference in `ticks` by `frequency` to obtain seconds. Timestamps are
recorded after region reads, not at the exact import-entry instruction.
`readable=False` means no valid contents were captured. `truncated=True` means
the requested size exceeded the available capture size. Neither proves a guest bug.

## Limits and interaction with other diagnostics

Plans have a 64 KiB file limit and contain 1 through 32 capture points. Each region
is limited to 256 KiB. A shared 128 MiB accounting budget limits retained capture
data. This is not a strict process-memory cap; metadata and temporary allocations
also use memory. The trace retains first events, not a rolling history.

Ownership capture takes precedence over mutex import-event capture. Use these
modes in separate runs for clear results. The return-address probe and guest RIP
sampler have separate switches and can still add work. They are not required for
ownership capture. See the [environment-variable guide](sharpemu-gui-undocumented-env-vars.md).
The guest RIP sampler retains the first 4096 report lines and writes them at normal
process exit. This buffering also applies when mutex tracing is off.

Memory snapshots are not atomic across guest threads. A host return does not prove
that a deferred guest lock or continuation has completed. Forced termination or a
crash can lose the buffered trace. Capture can change thread timing and use substantial
memory, especially with pointer following enabled. Start with small capacities.

Logs can contain proprietary game data and private information. Keep the JSON plan
and raw logs private. Review and reduce any evidence before sharing it publicly.
