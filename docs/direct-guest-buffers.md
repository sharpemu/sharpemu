# Direct guest buffers (experimental)

`SHARPEMU_DIRECT_GUEST_BUFFERS=1` enables optional buffer imports through
`VK_EXT_external_memory_host`. It is disabled by default. Unsupported devices and
unimportable buffers retain the existing mirrored-buffer path. Images continue to
use image-cache synchronization. No title-specific address or signal overrides are
used.

## Ownership and lifetime

Imported buffers bind to the permanent physical backing alias, not a guest virtual
address that can be unmapped or replaced. One Vulkan memory allocation is shared by
all buffer views in the same 4 MiB backing region. This bounds the amount of wired
unused memory and avoids independent overlapping imports, which produced stale
GPU reads on MoltenVK.

Each allocation holds a backing lease. Retired buffer handles keep their allocation
alive until GPU completion; the Vulkan allocation is freed before its lease is
released. A guest mapping change invalidates cached virtual bindings without
invalidating commands that already reference the old physical backing. Pending
write dependencies use physical alias byte ranges, so a second guest virtual alias
cannot bypass an unfinished writer.

Imports require host-coherent, host-visible compatible memory, aligned pointer and
binding offset, sufficient backing bounds, and no required dedicated allocation.
Noncontiguous guest mappings and buffers crossing an import-region boundary fall
back to mirrored buffers. An imported-to-mirrored merge completes preceding direct
writers and preserves their bytes before restoring page tracking.

## Ordering

GPU writes to imported buffers no longer require buffer page-protection faults,
uploads, or download copies. Guest CPU code relies on its completion protocol.
Completion labels are recorded after preceding GPU writes, with a device-to-host
memory dependency. They become visible to the CPU when the GPU executes them;
parsing a label packet does not publish CPU completion.

The command processor is also a consumer of GPU-written data. Ordinary command
reads wait for the exact physical-range producer. A signal wait can read live
coherent memory and suspend its guest queue without draining other queues. A known
4- or 8-byte queued signal can be forwarded to the command decoder while its write
is still unsubmitted, preserving GPU command order without publishing the value to
the CPU. Predictions expire on submission and overlapping writes invalidate them;
submitted signals must be read live because the CPU may have reset them.

Staging reservations can submit work. Copy producers are therefore tracked after
reservation, on the recording tick that contains the copy. Writable shader ranges
are retained through resource preparation and recorded again with the actual draw
or dispatch tick.

Command bytes which have no GPU buffer/image byte owner can also be read through
the stable backing alias without changing CPU page permissions. This optimization
is independent of imports and can be disabled with
`SHARPEMU_COMMAND_BACKING_READS=0`.

## Validation and remaining limits

Device tests cover shared imports, physical aliases, guest remapping, deferred
lease release, mirrored fallback, image overlap preservation, completion visibility,
command-only submission, signal prediction expiry, and staging submission boundaries.
Translated buffer-device-address loads and stores have been exercised. Translated
atomics are not validated on this test device: an existing baseline test fails
pipeline creation before importing memory.

Early Silent Hill rooftop-menu runs removed the original counter-page false-sharing
waits, but 64 MiB and 16 MiB import regions were slower than the mirrored control.
Four MiB regions reduce pinned memory further. This does not establish a net FPS
improvement or broad game compatibility. Keep imports opt-in until matched runtime
measurements, scene validation, and shutdown checks support enabling them.
