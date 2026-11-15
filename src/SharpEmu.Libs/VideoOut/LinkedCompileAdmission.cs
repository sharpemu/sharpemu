// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

// Which linked compute compiles may run at once. A linked compile can hold over 10 GB of driver memory for minutes, and
// two of that size at once can exhaust the commit limit and lose the device; running linked compiles strictly one at a
// time instead holds every other one back behind a long one for a whole session, and their programs' dispatches are
// skipped meanwhile. So a compile starts when the free commit, less the estimates of the linked compiles already
// running, still holds its own estimate (0.7 GB per MB of SPIR-V) plus a margin. A compile whose estimate reaches the
// margin starts only while no other one that large runs, and while the free commit cannot be read, one compile runs at
// a time.
internal sealed class LinkedCompileAdmission(Func<long> availableCommit)
{
    internal const long BytesPerSpirvByte = 700;
    internal const long MarginBytes = 4L << 30;

    private readonly object _gate = new();
    private long _reserved;
    private int _running;
    private bool _largeRunning;

    internal static long Estimate(long spirvBytes) => Math.Max(0, spirvBytes) * BytesPerSpirvByte;

    // Starts the compile when it fits, and then reserves its estimate until Release. available is the free commit it read
    // (-1 when unknown) and reserved the estimates of the compiles running before it.
    public bool TryAdmit(long spirvBytes, out long available, out long reserved)
    {
        var estimate = Estimate(spirvBytes);
        var large = estimate >= MarginBytes;
        lock (_gate)
        {
            available = availableCommit();
            reserved = _reserved;
            var fits = available < 0 ? _running == 0 : available - _reserved >= estimate + MarginBytes;
            if (!fits || (large && _largeRunning))
            {
                return false;
            }

            _reserved += estimate;
            _running++;
            _largeRunning |= large;
            return true;
        }
    }

    public void Release(long spirvBytes)
    {
        var estimate = Estimate(spirvBytes);
        lock (_gate)
        {
            _reserved -= estimate;
            _running--;
            if (estimate >= MarginBytes)
            {
                _largeRunning = false;
            }
        }
    }
}
