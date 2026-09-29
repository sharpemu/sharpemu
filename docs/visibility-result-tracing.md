<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Visibility result tracing

Set `SHARPEMU_TRACE_VISIBILITY=1` before launch. The default is off.
Full AGC logging, shader dumps, and RenderDoc are not required.

```powershell
$env:SHARPEMU_TRACE_VISIBILITY = '1'
$env:SHARPEMU_TRACE_DRAW_INDEX_COUNT = '360'
./SharpEmu.exe
```

The second setting is optional. It connects a selected indexed draw to each flip.

`Visibility published` checks the 16 result slots after an occlusion-counter event.
It reads the backing alias, without a GPU readback or a guest protection change.
`mismatchMask` identifies slots that differ from the published counter.
`readable=False` means that a backing snapshot was unavailable.
The trace does not change the synthetic counter or the ready bit.

`VisibilitySummary` appears every 60 Vulkan flips. Counts are cumulative for the process.
It reports publications, unreadable snapshots, mismatches, memory predicates, skipped
predicate decisions, and readback replacements. It includes the last publication and
predicate address, value, queue, and submission. A skipped decision is not a draw count.

The trace retains the last 256 publications. Before an existing buffer download writes
guest backing, it checks any complete result slots in the download. `download-replaces`
means the backing still contains a retained counter, but the download will write a
different value. The slot mask identifies those counters. This can be legitimate reuse;
the record alone does not prove an ordering error. Partial qwords, older publications,
other aliases, and unrelated write paths are not checked.

Detailed output stops after 8,192 records. Summaries continue. Backing reads and trace
output add overhead. Do not use this run for FPS comparisons. The trace does not add
GPU waits. It does not observe direct guest CPU reads or establish which guest
instruction consumes a result. A successful publication is not proof of consumption.
