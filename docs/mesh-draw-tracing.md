<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Mesh draw tracing

Set `SHARPEMU_TRACE_MESH=1` before launch. The default is off.

```powershell
$env:SHARPEMU_TRACE_MESH = '1'
.\SharpEmu.exe
```

This trace does not change draw selection, resource access, or GPU synchronization.
Logging can change timing. Do not use a traced run as an FPS baseline.
AGC logging, RenderDoc, and crash dumps are not required.

## Performance profile

Set `SHARPEMU_PROFILE_PERFORMANCE=1` to report `[PERF][MESH_COMPILE]` when a
Vulkan mesh permutation compiles. Mesh tracing is not required. The record gives
the shader hash, SPIR-V byte count, and static counts of control barriers, loops,
subgroup elections, and atomic OR instructions. These are compiled instruction
counts, not execution counts. Atomic OR counts can include guest operations.
Use the hash to match existing GPU pipeline timing records. Permutations can
share a hash. No shader counters or readbacks are added by this report.
`bound_format_specialization` identifies the bound-buffer optimization.
`buffer_formats` lists descriptor formats in binding order. These values are not
load execution counts. Dynamic device-address loads retain runtime decoding.
`static_instructions`, `static_loads`, and `static_stores` count emitted SPIR-V
instructions. Loads and stores include all storage classes, not only lane state.
These counts do not measure runtime memory traffic or driver machine code.
`static_before_first_loop` counts function instructions before the first loop
declaration. It includes setup and any dispatcher prefix moved outside the loop.
It is zero when there is no loop. It is not a runtime loop count.

`GPU_INTERVAL` identifies mesh commands as `kind=MeshDraw`. For these commands,
`max_args` contains group counts X, Y, and Z for the slowest interval.
`mesh_groups` sums X times Y times Z for collected commands in the report.
It counts launched workgroups, not emitted primitives. `avg_ms` is time per interval.
`GPU_MESH_MAX` gives target size, viewport size, scissor size, and depth test/write
state for that slowest mesh interval. `GPU_GRAPHICS_STATE` gives each pipeline's
shader stage, topology, sample state, culling, and attachment formats once.
Match all records by pipeline ID within one run. Pipeline IDs can change between runs.
These records use the existing timestamp queries. They add no GPU queries or readbacks.
Completion intervals do not isolate mesh or pixel execution and can include overlap
with later work. Target dimensions do not measure the number of shaded pixels.

## Records

Each `[MESH][TRACE]` line has an event ID, host thread ID, draw ID, guest submission ID,
shader address, and operation. Event IDs give the record order if thread output interleaves.
Indexed and automatic draws with the geometry-enable bit set start a trace scope.
Records cover selection, the geometry gate, launch planning, shader identity, user data,
buffer and image bindings, target requests, attachment acquisition, command recording,
write barriers, queue submission, and caught draw exceptions.

Queue records give the first and last mesh draw IDs and draw count for the command buffer.
`accepted=true` means the host accepted submission. It does not prove GPU completion.
`recorded-not-yet-completed` means a mesh command was recorded, not executed successfully.
`returned-or-unwound-before-recording` is an early return without a recorded mesh command.
The failure record distinguishes caught exceptions from an ordinary early return.

Buffer and image transfer records include the guest address and size. Publication records
are emitted after the bytes are written to guest backing memory.
`historicalMatch=true` identifies the most recent retained mesh range that overlaps a later
transfer. It is not proof of ownership or corruption. Memory can be reused after that draw.
The trace does not inspect every CPU write or the complete lifetime of every allocation.

## Limits

The trace keeps 8,192 state keys. Repeated state is printed at occurrences 1, 2, 4, 8, and
later powers of two. The live output stops at 32,768 lines and prints a limit notice.
An occurrence of zero means that the state-key table was full; that state was not counted.
The trace retains the last 4,096 events even after live output stops.
The range history and pending command table each hold at most 1,024 entries.

Recent history is printed during normal process exit or a managed unhandled exception.
Native termination and `FailFast` can prevent that final history from being printed.
Use the live records when no history footer exists. Close normally after the first guest
failure when possible. Keep the full log, including the first error and shutdown records.

## Interpretation

Compare the first failing guest operation with earlier mesh resource ranges and submissions.
An address overlap is a lead, not a diagnosis. Confirm the data and allocation lifetime before
changing rendering or memory ownership. Skipped mesh draws do not appear in a GPU capture.
