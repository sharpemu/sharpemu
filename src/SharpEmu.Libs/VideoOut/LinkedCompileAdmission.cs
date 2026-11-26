// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;

namespace SharpEmu.Libs.VideoOut;

// Which linked compute compiles may run at once. A linked compile can hold over 10 GB of driver memory for minutes, and
// two of that size at once can exhaust the commit limit and lose the device; running linked compiles strictly one at a
// time instead holds every other one back behind a long one for a whole session, and their programs' dispatches are
// skipped meanwhile. So a compile starts when the free commit, less the estimates of the linked compiles already
// running, still holds its own estimate (0.7 GB per MB of SPIR-V) plus a margin. A compile whose estimate reaches the
// margin starts only while no other one that large runs, and while the free commit cannot be read, one compile runs at
// a time.
internal sealed class LinkedCompileAdmission(Func<HostMemoryAvailability> readMemory)
{
    internal enum Result { Admitted, Wait, WaitForRam }
    internal enum Source { Demand, BootPrewarm, BackgroundPrewarm }

    internal const long BytesPerSpirvByte = 700;
    internal const long MarginBytes = 4L << 30;

    private readonly object _gate = new();
    private long _reserved;
    private int _running;
    private bool _largeRunning;
    private bool _lowRam;

    internal static long Estimate(long spirvBytes) => Math.Max(0, spirvBytes) * BytesPerSpirvByte;

    // A speculative worker can wait before entering the driver. The first compile and demand work still make progress
    // under the usual commit guard. Both the pressure decision and the reservation use the same admission lock.
    public Result Admit(long spirvBytes, out HostMemoryAvailability memory, out long reserved, Source source = Source.Demand)
    {
        var estimate = Estimate(spirvBytes);
        var large = estimate >= MarginBytes;
        lock (_gate)
        {
            memory = readMemory();
            reserved = _reserved;
            var available = memory.AvailableCommitBytes;
            var backgroundPrewarm = source == Source.BackgroundPrewarm;
            if (backgroundPrewarm)
            {
                var low = Math.Clamp(memory.TotalPhysicalBytes / 16, 512L << 20, 2L << 30);
                _lowRam = memory.TotalPhysicalBytes > 0 && memory.AvailablePhysicalBytes >= 0 &&
                    (_lowRam ? memory.AvailablePhysicalBytes < low + low / 2 : memory.AvailablePhysicalBytes <= low);
            }

            var fits = available < 0 ? _running == 0 : available - _reserved >= estimate + MarginBytes;
            if (!fits || (large && _largeRunning))
            {
                return Result.Wait;
            }

            if (backgroundPrewarm && _lowRam && _running != 0)
            {
                return Result.WaitForRam;
            }

            _reserved += estimate;
            _running++;
            _largeRunning |= large;
            return Result.Admitted;
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
