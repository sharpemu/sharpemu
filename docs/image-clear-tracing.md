<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Image clear tracing

## Invalid texture layers

An out-of-range texture base layer includes the shader hash, image slot,
decoded descriptor type and depth, shader image shape, and eight descriptor
words in the fatal message. No trace switch or address filter is required.
The renderer still rejects the invalid view. Descriptor history from normal
shutdown is not available when the process stops through `FailFast`.

Set `SHARPEMU_TRACE_IMAGE_CLEARS=1` before launch to inspect Vulkan color-target and volume clear decisions.
The default is off. Full AGC logging, shader dumps, and RenderDoc are not required.

```powershell
$env:SHARPEMU_TRACE_IMAGE_CLEARS = '1'
./SharpEmu.exe
```

Search the log for `ImageClear` and `VolumeClear`. The `ImageClear` trace records these details:

- `target`: guest image and metadata addresses, format, extent, clear word, and DCC control.
- `state`: the tracked metadata fill, layer mask, and fill size before clear resolution.
- `metadata` and `history`: recognized fills, invalidation, and retained metadata events.
- `decision`: whether the attachment will clear and the four raw clear-value words.
- `applied`: a native metadata clear, its image, slice, layer, and clear code.

Each distinct `ImageClear` message appears once. That channel stops after 8,192 distinct messages and reports
the limit. It is a state summary, not a complete event timeline. Retained history covers the
last 32 transitions for up to 256 metadata addresses. CPU events include notified writes only.
Native GPU writes can be absent from the metadata history. An absent event does not prove that
the guest did not write metadata.

For non-volume images, the `native` entries contain a 64-byte prefix of the metadata allocation. GPU-dirty data
is synchronized through the buffer cache before the backing bytes are read. This can wait
for GPU completion and can download a wider range under the existing readback policy.
The sample is not proof that the full metadata allocation has one value.

Sampling runs before image lookup and final draw uploads. It records up to four GPU-dirty
samples and one CPU-backed sample per image/metadata address pair, with 2,048 attempts per
image cache. Identical output is omitted. Clear decisions are unchanged. Turn the trace off
for performance measurements because readback changes timing and submission boundaries.

## Native color clears

Supported single-mip, single-sample color targets read native DCC metadata before use.
The buffer cache completes GPU writes before this read. A clear requires one supported
code across the full metadata slice. Mixed slices and unsupported codes are not cleared.
The clear uses the target view. The consumed slice is then set to the uncompressed code.
This prevents a later draw from applying the same clear again. This path is not a general
DCC decompressor. It runs with the trace off as well.

## Volume image states

The same switch records `VolumeImage` states for 3D images. These include creation,
lookup, acquisition, upload layout, and release. Each entry identifies the guest address,
host image, format, extent, view, dirty flags, and last recorded CPU write. Upload entries
include the source buffer and offset. Region entries include the mip and copy depth.
The cache retains the 2,048 most recently used distinct states. A repeated state updates
its UTC time and tick. Normal cache disposal writes these states in last-use order and
reports evictions. Close the window normally to save the tail; a crash can lose it.
This is not a complete event history. The trace does not read image bytes or add GPU waits.

`ResourceHistory` uses separate rings for 8,192 lifecycle events, 16,384 binding
and transfer events, 4,096 CPU write records, and 4,096 GPU write or buffer-fill records.
CPU writes cannot evict GPU records. Write traffic cannot
evict lifecycle or binding events. At the first lookup of a volume address, it
prints overlapping events from each ring.
Events include image creation, acquisition, upload, release, GPU image-write markers,
CPU write notifications, GPU buffer-write notifications, and unmaps. Entries include
the queue, submission, scheduler tick, range, format, extent, and image role.
An acquisition records a binding, not proof that a shader wrote a pixel. Memory
notifications do not identify a guest instruction or capture every guest CPU store.
Consecutive write notifications on one 4 KiB page are combined only when their
operation, queue, submission, and scheduler tick match. An intervening event ends
the group. A combined range is an envelope, not proof that all bytes were written.
Entries retain the first and last sequence and the number of notifications.
Up to 128 volume addresses are reported, with up to 256 matches per channel and address.
Each header reports dropped notifications and the coalesced count. Each footer
reports omitted matches. Sequence numbers establish order across channels.
This history uses the same switch and adds no readbacks or GPU waits.

`ImageDescriptor` connects a materialized volume descriptor to its shader hash,
shader address, image index, first-use instruction, and source graph. It records
the descriptor words and captured user data. Graph entries use node IDs to handle
cycles. Related flattened-table values come from the existing snapshot, not new
guest reads. A bounded history retains the latest record for the 512 most recently used
shader/image/address tuples. Normal Vulkan or Metal shutdown writes this history.
Records include UTC times. Earlier tuples are evicted, not a reason to stop tracing.
Each graph is limited to 256 nodes and reports truncation. A crash can lose this history.
`ImageDescriptorTableSource` links each flattened slot to its original memory-read node.
The walk follows that node's handle and offset, including other table slots. User-data
nodes include their captured values. A visited-node set prevents cycles. These records
add no guest reads and do not evaluate a second resource snapshot.
`ImageDescriptorMemory` includes each scalar read's PC, byte offset, and component.
`ImageDescriptorInstruction` includes the corresponding decoded instruction and words.
These records permit an offset check without a full shader dump. They contain shader
instruction data; do not publish the log without review.
These records describe resource resolution, not proof that a dispatch executed.
They contain guest descriptor data; review logs before sharing them.

Owner entries such as `texture-owner-search` and `texture-owner-candidate` list overlapping
cached images before selection. Volume metadata now uses `VolumeClear` records instead of
the diagnostic-only synchronized `texture-metadata` prefix read.

## Volume clear decisions

The same switch enables `VolumeClear` for 3D images. No additional switch is required.
These records are separate from the `ImageClear` and `VolumeImage` channels.
Phases identify the point at which each observation was made:

- `before-texture-lookup`: the request before a cached owner is resolved.
- `resolved-shader-image`: the resolved image, shader stage, shader hash, and image slot.
- `before-attachment-decision` and `after-attachment-decision`: the attachment clear check and result.
- `before-sampled-decision` and `sampled-skip`: the sampled-image clear check or skipped clear.
- `sampled-clear-recorded`: the recorded clear, raw RGBA words, and clear scope.
- `sampled-consumed` and `sampled-tracked-consumed`: metadata consumption in the sampled-image path.

Each record includes a sequence, scheduler tick, queue, submission, guest image address,
host image handle, owner-resolution state, image role, extent, format, view, and GPU-modified state.
Texture requests include descriptor words and compression flags when available. The descriptor
metadata address and cached metadata address are reported separately. Missing descriptor data
is reported as `none`.

For each distinct metadata address, the trace reports registration and tracked-fill state.
It derives a candidate DCC slice size and scans each depth slice in 4 KiB chunks.
Each slice reports its address, bytes read, first byte, uniformity, readability,
GPU-dirty state before and after the scan, and tracked clear state. A failed read stops
that slice's scan. Mixed bytes and unsupported clear codes remain visible in the report.

`candidate=True` means the backing bytes were readable and uniform, the code was recognized,
and neither dirty-state check found GPU writes. It does not prove that a clear is legal or
that the bytes form an atomic CPU/GPU snapshot. `synchronization=none` means this diagnostic
adds no GPU wait or readback. It does not register metadata, change the request, or select
a rendering clear. Normal rendering synchronization is unchanged.

Zero or invalid ranges, address overflow, and candidate ranges larger than 8 MiB are reported
as `unavailable-invalid-or-over-8MiB`. The scan bound applies separately to each distinct
metadata range. Repeated identical state for an image-address/role/phase key is omitted.
Each image cache retains at most 256 keys and writes at most 2,048 changed records.
A new key beyond the key limit or the record limit stops this channel and writes a limit message.
These limits are separate from the `ImageClear` limit. CPU scans and log output can affect
timing even without GPU waits. Disable tracing for performance comparisons.

## Binding provenance

The same switch keeps the first texture binding of each format and extent at an address, for up
to 4,096 addresses and four descriptions per address. Each record has the shader hash, image
index, descriptor words, unmap-notification count, and an FNV-1a hash of the first 64 KiB of
guest bytes (or the image size, if smaller). At the first upload of each volume, for up to 128
volumes, `BindingProvenance` compares every earlier record at that address with a hash of the
current guest bytes over the same length. `match=True` means that the sampled ranges have equal hashes.
`unmapsBetween=0` means that no unmap notification was recorded between the samples.
These values do not prove equal full images or exclude format, tiling, or GPU ownership errors.

## Address-range tracing

Set `SHARPEMU_TRACE_IMAGE_RANGE` to follow one guest memory range of any image type.
It requires `SHARPEMU_TRACE_IMAGE_CLEARS=1`; without it, a warning is written.
Each entry is `start+size` or `start-end` in hexadecimal. The end is exclusive.
Separate up to 16 entries with commas or semicolons. Invalid entries are ignored with a warning.

```powershell
$env:SHARPEMU_TRACE_IMAGE_CLEARS = '1'
$env:SHARPEMU_TRACE_IMAGE_RANGE = '0x2036720000+0x200000'
$env:SHARPEMU_LOG_DIRECT_MEMORY = '1'
./SharpEmu.exe
```

With a range set, the `ResourceHistory` rings keep only events that overlap a range.
Unrelated CPU writes then cannot evict the events of interest. Images that overlap a range
are handled as volumes are: the first lookup reports history, and `VolumeImage` states and
upload layouts are recorded. The label stays `VolumeImage` for existing scripts.
`ImageDescriptor` records any descriptor whose base address is inside a range, of any type.
At normal cache disposal, each range writes its full retained history with
`ResourceHistory range=0x<start>+0x<size>`. That report includes events after the first
lookup, such as a later color-target acquisition or GPU image write in the range.
It lists up to 16,384 matches per channel.

Guest addresses change between runs. To select an image by its use instead of its address,
set `SHARPEMU_TRACE_IMAGE_FOLLOW` to `shaderHash:imageIndex`. The hash is hexadecimal and the
index is decimal, as `ImageDescriptor` reports them. Separate up to 16 entries with commas.

```powershell
$env:SHARPEMU_TRACE_IMAGE_CLEARS = '1'
$env:SHARPEMU_TRACE_IMAGE_FOLLOW = '0x0F48CA4B466361E8:3'
```

When that shader uses that image, the descriptor base address is noted. The next image lookup
at that address adds its footprint as a range and writes `ImageTraceRange follow`.
The rings stay unfiltered until the first range is known. The first-lookup report then shows
earlier events that the rings retained, such as a producer that ran once. After that, only events
in a range are kept, so unrelated writes cannot evict later-frame events from the shutdown report.
Overlapping or adjacent footprints merge. A larger footprint at the same address extends the range.
The trace retains up to 16 separate ranges in total, fixed and followed.
Both variables can be set together. A fixed range can also come from `map_direct applied`
with `SHARPEMU_LOG_DIRECT_MEMORY=1`. Include the whole mapping to find partial writers.
Two more channels record writers that can bypass image invalidation. `command-writes`
contains command-stream fills (`command-fill`, source is the fill value), copies
(`command-copy`, source is the source address), and direct writes (`command-write-<handler>`),
such as `WRITE_DATA` and end-of-pipe data. `shader-writes` contains every writable buffer
binding (`shader-buffer-write`), including raw storage and device-address ranges, with the
shader hash when it is known. A binding shows that a shader could write the range, not that
it did. `draw-targets` contains each active color-target base of every Vulkan draw, as
`draw-target-<outcome>`. The outcome is `recorded` or the reason the draw stopped: `zero-count`,
`metadata-operation`, `missing-vertex-shader`, `unsupported-geometry-stage`, `no-topology`,
`no-targets`, `adapted`, `resource-rejected`, or `rectangle-list-without-exports`. A target entry does not prove a
pixel write. For these entries, `source` is the pixel shader address and
`shader` is the vertex export shader address. The entry has size 1 at the base address.
Each channel keeps 16,384 records and uses its own lock.

At the first lookup of an image in a range or of a volume, `ResourceContext` also lists the retained bindings,
draw targets, shader writes, and command writes of the current and previous submission on that
queue, at any address. A producer that writes a different address than the consumer reads
appears here with its own target address, format, and extent. Up to 32 images report context.
Volumes are included because a volume can be first read by a shader that is not followed.

Packets that the command stream skips are also recorded. `CommandSkip kind=predicated` is a
packet skipped by `SET_PREDICATION`; `detail` is its opcode. `kind=conditional-execute` is a
block skipped by `COND_EXEC` because its condition word was zero; `detail` is the low 32 bits
of the condition address. The first 256 skips are written to the log. Each skip is also a
`command-skip-<kind>` record at the packet address, so it appears in `ResourceContext`.
`Predication` lists the first 256 memory predicate evaluations with the address, the 64-bit
value, the condition, the wait flag, and the resulting skip state. The first 32 also print the recorded
writers of the predicate qword as `ResourceHistory predicate=0x<address>`. Up to 64 skipped
predicates are read again at later evaluations. `PredicationChanged` reports a value that changed
after the skip decision, with the number of later evaluations and the elapsed milliseconds. Sequence numbers are shared
with the other channels, so their order is comparable.

For a skipped indirect buffer, `CommandSkipTarget` decodes at most its first 4,096 dwords.
It reports at most 16 target addresses per process. It does not execute the target.
The decoded prefix is not the complete buffer when the buffer exceeds that limit.
Predicate checks can read GPU-owned memory again. Do not use tracing for performance comparisons.

Memory notifications do not identify the guest instruction. To find an HLE or managed writer,
use `SHARPEMU_WATCH_WRITE` from [guest write watch](guest-write-watch.md) on one address.

Set `SHARPEMU_DUMP_VOLUME_UPLOADS=1` to save paired GPU upload snapshots.
The default is off. Each pair contains the tiled source, the linear result, and a JSON
layout record. Files are under `user/logs/volume-uploads`, in a unique session directory.
The `.backing.bin` file records guest backing bytes before buffer acquisition, when readable.
The manifest identifies the selected GPU buffer usage. Backing bytes can differ from newer
GPU-owned data. The image-clear trace also records raw 3D descriptors in little-endian byte order.
By default, only volumes up to 64 pixels on each axis are included. Each buffer is limited to 4 MiB.
The first four uploads at each address are saved, for up to eight addresses per image cache.
Each filename and manifest includes an upload number. Uploads in the same submission do not overwrite files.
Enable `SHARPEMU_TRACE_IMAGE_CLEARS=1` to record image ownership and write history with the snapshots.
Set `SHARPEMU_DUMP_VOLUME_UPLOAD_EXTENT=32` to select only 32x32x32 volumes.
Set `SHARPEMU_DUMP_VOLUME_UPLOAD_EXTENT=1280x95x5` to select that exact volume size.
The three-axis form permits dimensions above 64. The buffer and address limits still apply.
The default is zero, which keeps the default dimension limit. Invalid filters select no volumes.
Filtering occurs before the address limit.
The GPU copies complete before files are written. This adds copies and file I/O, so do not
use the switch for performance comparisons. The binary files contain guest image data.
