<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Native function tracing

This temporary Windows x64 diagnostic is off by default. It records up to 64
entry and return hits in the normal log without an external debugger.

Set `SHARPEMU_TRACE_NATIVE_FUNCTION=1`, `SHARPEMU_TRACE_NATIVE_ENTRY_ADDRESS`,
and `SHARPEMU_TRACE_NATIVE_RETURN_ADDRESS`. Addresses are absolute hexadecimal
guest addresses. Verify them for the executable and image base in use.

The entry must start with `push rbp; mov rbp,rsp; push r15; push r14; push r12`.
The selected return point must start with `add rsp,0x30`, while RBP still identifies
the frame. A mismatch stops setup without starting that execution frame.
This is not a general function hook or an automatic function finder.

The trace uses in-process breakpoints and resume code. Resume code executes the
displaced instruction and jumps back without changing registers or flags first.
Each hit records RAX, RDI, RSI, RDX, the managed host thread ID, and a checked caller
read. Site 0 is entry; site 1 is return. The caller can be unreadable.
No guest result is replaced. The trace still changes timing at each hit.

Do not enable crash capture or attach another debugger for this test.
Use the log to confirm both trace addresses before interpreting missing hits.
Normal backend disposal restores the bytes after guest threads stop. If threads
cannot stop, the backend retains the trace with the rest of the live session.
Logs can contain game data. Keep them private.

## Combined probes

Multiple native modes can run together. When two or more modes are enabled,
set a separate address pair for each mode. For example, schema-walk mode uses
`SHARPEMU_TRACE_NATIVE_SCHEMA_WALK_ENTRY_ADDRESS` and
`SHARPEMU_TRACE_NATIVE_SCHEMA_WALK_RETURN_ADDRESS`. Replace `SCHEMA_WALK`
with `ASSET_READ`, `OBJECT_REFERENCE`, or `SERIALIZATION` for the other modes.
The shared ENTRY_ADDRESS and RETURN_ADDRESS settings apply only to a single mode.

All instructions are checked before any breakpoint is written. Probes at the same
address share one breakpoint if their displaced instructions agree. Partial
instruction overlaps are rejected. Each enabled observer runs before the displaced
instruction resumes once. No guest result is changed.

The modes share a 32-record recent history. The first detected invalid reference,
invalid schema descriptor, or asset-size mismatch writes this history. A native
exception also writes the current history. This does not cover every guest fatal
path or a GPU fail-fast. Mesh tracing and Vulkan validation can run with these probes.
Combined probes add exception and logging overhead. Do not measure FPS with them.

## Serialization probe

Set `SHARPEMU_TRACE_NATIVE_SERIALIZATION=1` with the native trace switch to use
the serialization probe instead. Set `SHARPEMU_TRACE_NATIVE_ASSET_FILTER` to
an asset name substring. Only matching assets consume the 64-event limit.

In this mode, ENTRY_ADDRESS selects the schema site. It must contain
`movzx ecx,byte ptr [rsp+0x110]`. RETURN_ADDRESS selects the header-read site,
which must contain `mov rax,qword ptr [rbp-0x50]`. These are interior probes,
not a function entry and return pair. Both displaced instructions resume unchanged.

The schema site records R15 as the owner, RAX as the cache, its count, and the
first decoded fragment. The header site records the archive cursor, buffered
position, and next two bytes. Match events by thread and caller frame.
The header event precedes the schema event. The schema event's nextWord is
after header consumption; it is not the decoded header.

This temporary probe requires the verified archive wrapper and stack layouts.
Instruction checks alone do not establish those layouts for another executable.
Reads use ReadProcessMemory and report validity. An unreadable archive chain
produces one warning. Each site reports its first hit before filtering. The first
four name-read failures and filter mismatches are reported separately. Names are
read through their UTF-16 terminator, up to 256 characters; an unreadable byte or
a missing terminator rejects the name. Invalid reads do not change guest data or results.
The trace adds exception overhead even when the asset filter excludes a hit.
The probe retains the last 32 readable header/schema records across all assets.
A reported native exception writes this history, including assets outside the filter.
Matching events also record up to 192 bytes from the current buffer start with its
base position. Compare the same asset and position between runs. These snapshots
show bytes at the probe; they do not identify which read or copy supplied them.
Do not use it for performance measurements. Leave crash capture off.

## Object-reference probe

Set `SHARPEMU_TRACE_NATIVE_OBJECT_REFERENCE=1` with the native trace switch.
For a single mode, ENTRY_ADDRESS selects `mov rdx,[rax]` before
the archive reads a reference. RETURN_ADDRESS selects `mov esi,[rbp-0x1C8]`
after that read. Both instructions resume unchanged.

This probe requires the verified object-reader layout. It records the archive
cursor, buffer bounds, up to 32 source bytes, the decoded reference, and the
import and export counts. Frame and owner addresses identify each record.
It prints the first eight results and the first 16 invalid references. It also
retains the last 32 results for native exception reporting.

The probe does not infer an asset name or file offset from unverified fields.
If the buffer needs a refill, the source snapshot precedes that refill and is
not necessarily the consumed data. Nested reads can replace the saved source;
compare frame and owner values before pairing it with a result. Use these
records to identify the next buffer to inspect. Do not use them as proof of
which operation wrote that buffer. Leave crash capture and RenderDoc off.

## Asset-size probe

Set `SHARPEMU_TRACE_NATIVE_ASSET_READ=1` with the native trace switch.
The entry site must contain
`test byte ptr [r12+8],0x10`. The mismatch site must contain
`mov rsi,[rbp-0x2A0]`. Both displaced instructions resume unchanged.

The probe records the reader cursor, buffer bounds, expected size, and up to
128 bytes before asset serialization. At a size mismatch, it records the
asset name and the same fields again. Addresses and layouts must be verified
for the guest executable. The position is relative to the archive reader;
it is not necessarily an offset in the containing PAK file.

The first eight starts and the first 16 mismatches are printed. Up to 128
thread/frame pairs retain start records; nested calls use separate frames.
Missing or unreadable data is marked. A refill can change the buffer between
the two sites. These samples do not identify the operation that supplied data.
When the frame table is full, a new start replaces the oldest recorded start.
An updated frame moves to the newest position. The table does not stop recording.
Leave RenderDoc and crash capture off for this comparison.

## Schema-walk probe

Set `SHARPEMU_TRACE_NATIVE_SCHEMA_WALK=1`.
The setup site must contain `movzx ecx,byte ptr [rsp+0x110]`. The descriptor
site must contain `mov rax,[rbx+8]`. Verify the surrounding guest layout first.

This mode records the schema base, its entry count, and decoded fragment bytes.
At the descriptor site, it records both primitive and property paths, including
the packed value, property pointer, descriptor index, fragment, and remaining count.
It reports an all-ones descriptor, a null pointer on the property path, or an index
outside the recorded count. This does not detect every invalid descriptor.
It retains at most 128 thread/frame pairs and prints at most 16 failure records.
New setup records replace the oldest records when the table is full.
The recorded count is from setup, not a lifetime guarantee. A missing pair or
unreadable memory is not evidence of an out-of-range access. Timing can change.
