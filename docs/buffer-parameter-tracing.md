<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Buffer parameter tracing

Use this Vulkan diagnostic to compare CPU backing bytes with a RenderDoc buffer.
It does not download GPU data, change cache ownership, or correct parameter values.
It is off by default.

Set `SHARPEMU_TRACE_BUFFER_SHADER` to the hexadecimal shader hash.
Set `SHARPEMU_TRACE_BUFFER_SLOT` to the zero-based buffer resource slot.
Both selections must match before the diagnostic reads memory.

For vertex input, the same slot variable selects the semantic attribute ID,
not the merged vertex binding slot. `VertexBufferParameter` lines are limited
to 64 samples per presenter. They include the resolved descriptor words,
stride, record count, format, bounds mode, requested size, available acquired
range, host binding, and 16 bytes from guest backing and the mapped stream.
Descriptor words include the attribute format and address adjustments.
The acquired range can include merged attributes. It is not a per-attribute
upload count. Stream samples can include bytes outside the requested range;
this is intentional so a short upload is visible. Device-local data reports
`unavailable`. No GPU download or wait is added. Samples are not atomic with
guest writes or GPU execution. Component count is not a byte count.

Each `BufferParameter` line records the submission tick, guest address, buffer handle,
buffer offset, range size, write access, and the first 128 bytes at most.
The trace stops after 16,384 samples per presenter.
An unreadable range reports `unavailable`, not zero bytes.

Take a RenderDoc capture while the fault is visible. Match the shader and buffer
slot in the capture. Compare its descriptor offset and bytes with the trace.
RenderDoc resource IDs are not Vulkan buffer handles; use the resource details
to identify the original handle. Account for descriptor alignment adjustments.

The CPU sample is taken after buffer acquisition. It is not an atomic snapshot
with the GPU read. A guest CPU write between the upload and sample can change it.
A mismatch needs an ownership and timing check before it proves a cache defect.
Buffers written by the GPU can legitimately differ from CPU backing.

These logs can contain guest data. Review them before sharing them.
Remove both variables after the test. Logging and RenderDoc can reduce performance.
