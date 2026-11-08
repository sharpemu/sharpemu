<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Image memory diagnostics

Set `SHARPEMU_PROFILE_PERFORMANCE=1` to record `ImageMemory` at each 300th
image collection call. Image creation failures record a report without this switch.
The diagnostics do not change image allocation or removal.

- `allocated_bytes` is the sum of Vulkan allocation sizes for cached images.
- `indexed_bytes` is the cache's existing memory-pressure accounting.
- `pending_bytes` covers unregistered nonempty images that still occupy cache slots.
  Their slots are normally removed by a GPU completion action.
- `created` and `destroyed` are cumulative slot creation and removal counts.
- `gpu_modified_bytes` and `tiled_gpu_modified_bytes` describe retained image contents.
  The collector can retain tiled GPU-written images to preserve those contents.
- `device_allocations` counts live device-memory allocations through the shared device
  object. Compare it with `device_allocation_limit`.

Each report includes `ImageMemoryLargest`, which lists the eight largest cached allocations.
It records their registration, GPU ownership, tiling, depth association, and last access.
These fields describe retention state; they do not prove that an image can be removed.

`collection_last_use` and `collection_age` use the collector's clock, not GPU ticks.
The age-eligibility fields apply the current normal and aggressive thresholds.
Aggressive eligibility is false when critical pressure is absent. Periodic reports
use the current pass tick; failure reports use the next collection tick.
`retention` describes the ownership gate separately from age. A removal candidate
can still be too young or outside the bounded scan. Readback success is not assumed.

With profiling enabled, `ImageMemoryPaths` reports cumulative lookup results and
collection decisions. Retention counts are visits, not unique images.
An image can remain GPU-owned when a dirty buffer overlap prevents readback.
`retention=dirty-buffer-overlap` identifies this ownership gate in `ImageMemoryLargest`.
The report does not resolve the overlap or change image ownership.
`buffer_overlap_retentions` counts these decisions, not confirmed buffer writes.
One retained image can be counted many times. `ImageMemoryCreated` groups successful allocation
counts and bytes by the cache method that requested them. These are cumulative
allocation totals, not live memory totals. Overlap matches can include image growth.

`ImageMemoryCreateLarge` records the first 64 allocations of at least 64 MiB.
It includes layers, mip levels, samples, guest size, and allocation size.
`ImageMemoryLookupLarge` records the first 64 large lookup misses and up to eight
overlapping candidates per miss. These records require performance profiling.

The byte totals exclude driver overhead and allocations outside the image cache.
They are not a Vulkan heap budget or total VRAM usage measurement.
No shader dump or RenderDoc capture is required.

Image collection starts at 1.5 GiB of indexed image memory and stops below that
threshold. Explicit release and unmapping still remove images independently.
`collection_enabled` reports this gate. Age eligibility alone does not enable
collection. This threshold is not a measurement of available device memory.

`ImageLifetime` follows the first 256 deleted guest addresses, with at most 4096
records per cache instance. It records collection decisions before GPU ownership
is cleared, depth-association removal, recreation, and population attempts.
Records include ownership flags, buffer overlap, and the last recorded CPU write.
`previous_deletion` links to the last deletion record at the same start address.
An address match is not proof that two allocations represent the same resource.
A population attempt is not proof of a completed upload. A queued readback is not
proof of completed publication. The trace does not cover every deletion path or
recreation at a different start address. It uses the performance-profile switch.
