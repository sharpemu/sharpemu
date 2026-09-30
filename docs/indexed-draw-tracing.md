<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Indexed draw tracing

Set `SHARPEMU_TRACE_DRAW_INDEX_COUNT` to a decimal index count from 1 through 4096.
The default is off. Full AGC logging and image-clear tracing are not required.

The trace reads CPU-visible index bytes for draws with the selected count. It
converts each index to a little-endian 32-bit value and computes a SHA-256 hash.
Equal index sequences have the same hash across 8-bit, 16-bit, and 32-bit inputs.

`IndexedDraw` entries identify these stages:

- `command`: an indexed draw reaches the command host.
- `predicated-candidate`: a draw is found in a predicated packet or its skipped buffer.
- `conditional-execute-candidate`: a draw is found in a skipped conditional range.
- `executor`: an indexed draw reaches the renderer.
- `resource-rejected`: resource preparation rejects an image binding.
- `emitted`: the host draw call returns. This does not prove correct pixels.

Each entry includes the index address, count, type, submission, and packet address.
The detailed trace keeps up to 256 hash/stage pairs and prints the first four
occurrences of each pair. It reports when the pair limit is reached.

On Vulkan, `IndexedDrawFrame` closes the interval between two captured guest flips.
`IndexedDrawCounts` reports the counts for each known hash in that interval. These
counts continue after the first four details. Zero counts show that a known hash
was absent from that stage. These are global recording intervals, not per-queue
frame ownership. The unfinished interval is not printed if no next flip occurs.

The trace enables Vulkan debug labels. An `IndexedDraw` marker immediately before
the host draw gives its CPU-side hash, guest address, submission, packet, and prior
flip version. Compare the next draw's bound indices with that hash in RenderDoc.

Skipped-buffer inspection uses local index state and does not execute commands.
It follows at most eight levels and 4,096 packets per scan. Nested state changes
are not propagated back to the parent. Conditional state changes invalidate the
local index state. Found draws are candidates, not proof that every nested branch
would execute. Indirect draw arguments are not evaluated. `IndexedDrawScan` gives
the first four incomplete-scan reasons per flip; `scanIssues` counts all such
issues and hash/stage limit omissions in that interval.

The trace does not run skipped packets or synchronize GPU writes for its samples.
GPU-generated indices can therefore produce a stale CPU-visible hash. An absent
hash does not prove that the guest never built the draw. Sampling and output add
cost; turn the trace off for performance measurements.
