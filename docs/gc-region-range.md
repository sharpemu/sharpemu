<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# GC region range and the PS5 main-image base

Some Windows hosts fail at startup with:

```
Could not allocate main image at required base 0x0000000800000000
```

PS5 executables must be mapped at the fixed guest address `0x8_0000_0000`
(the 32 GiB mark). If anything else in the host process already owns that
address, the loader has no fallback and no game can boot (issue #235).

## Root cause

Since .NET 7 the regions-based garbage collector reserves one large
contiguous block of virtual address space during runtime startup, before
any managed code runs. The block is reservation only (it consumes no RAM),
but it can span up to 256 GB, and the OS picks its position with
bottom-up ASLR. When Windows places it low — e.g. at `0x7FFF0000` as
captured with VMMap in issue #235 — the reservation covers `0x8_0000_0000`
and the fixed mapping fails. When ASLR happens to place it high, the same
build works. That placement lottery is why the crash reproduces
deterministically on some machines and never on others.

`ServerGarbageCollection=false` does not help: workstation GC also uses
regions. `GCRetainVM` only controls whether memory is returned later, not
the size of the initial reservation.

## The fix

On Windows the launcher already relaunches a mitigated child process to
run the emulator (CET/CFG off). Because the GC reads its configuration
from the environment during runtime startup, the parent is the only place
that can shape the child's reservation: it now exports

```
DOTNET_GCRegionRange=400000000
```

for the child before `CreateProcessW`, using the same set/restore pattern
as `SHARPEMU_MITIGATED_CHILD`. CLR configuration numbers are hexadecimal,
so `400000000` is 16 GiB. Starting from the worst-case low placement
(~2 GB), a 16 GiB reservation ends around 18 GiB and can no longer reach
the main-image base at 32 GiB.

The value only caps the address-space reservation for the managed heap.
Guest memory, GPU buffers, and JIT code live in native allocations outside
the managed heap, so 16 GiB of managed-heap address space is generous for
the emulator's bookkeeping.

A value already present in the user's environment is respected, and the
parent's environment is restored after the child is created. The
main-image allocation failure message also suggests setting the variable
manually, which covers hosts running with
`SHARPEMU_DISABLE_MITIGATION_RELAUNCH=1`. Linux and macOS place the CLR
reservation high in the address space (`0x7F...`), far from the 32 GiB
mark, so they are unaffected by the reported failure mode.

## Verification

- A standalone probe (VirtualQuery walk plus the same RWX commit the
  loader performs) shows the GC reservation shrinking from ~32 GB to
  exactly 16 GiB on .NET 10 when the variable is set.
- A temporarily instrumented build confirms the mitigated child inherits
  `DOTNET_GCRegionRange=400000000` while the parent process and its
  environment stay untouched.
- The full solution test suite passes (451 tests).

## Residual risk and possible follow-up

ASLR could in principle still place the 16 GiB reservation so that it
starts between 16 GiB and 32 GiB and overlaps the image base. That window
is small, but a bulletproof follow-up exists: create the mitigated child
suspended, pre-reserve the guest window in it with `VirtualAllocEx`, and
teach the allocator to commit into that reservation. That touches
`PhysicalVirtualMemory` and was intentionally left out of the first,
focused change.
