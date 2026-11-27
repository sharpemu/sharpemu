// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

// Read obtains remember the persistent buffer that held each range. A later read of the same range takes it while the
// buffer is still registered and still holds the whole range, and no page of the range is CPU-dirty: registered buffers
// never overlap, so the full path would find that buffer and upload nothing. The memo keeps two ways per set; a range
// takes its own way, else an empty one, else the one used less recently. A served read still runs every per-use effect
// of a clean read: the buffer's lifetime touch, a texel read's image bytes and the overlay of pending dumps.
// SHARPEMU_OBTAIN_MEMO=0 turns the memo off; =verify takes the full path for every read the memo would serve and counts
// the reads where it found another buffer or offset.
public sealed unsafe partial class GuestBufferCache
{
    internal enum ObtainMemoMode
    {
        Off,
        On,
        Verify,
    }

    internal static readonly ObtainMemoMode ConfiguredObtainMemo = Environment.GetEnvironmentVariable("SHARPEMU_OBTAIN_MEMO") switch
    {
        "0" => ObtainMemoMode.Off,
        "verify" => ObtainMemoMode.Verify,
        _ => ObtainMemoMode.On,
    };

    private const int ObtainMemoSets = 4096;

    private readonly record struct ObtainMemo(ulong Address, ulong Size, ResourceSlotIdentifier Identifier, ulong Offset, GpuBuffer? Buffer)
    {
        public long Serial { get; init; } = -1;
        public long Epoch { get; init; } = -1;
        public bool Holds(ulong address, ulong size) => Buffer is not null && Address == address && Size == size;
    }

    private readonly ObtainMemo[] _obtainMemos = new ObtainMemo[ObtainMemoSets * 2];
    private readonly long[] _obtainMemoUses = new long[ObtainMemoSets * 2];
    private long _obtainMemoClock;

    private static readonly ThreadCounter _obtainMemoServed = new();
    private static readonly ThreadCounter _obtainMemoFull = new();
    private static readonly ThreadCounter _obtainMemoVerified = new();
    private static readonly ThreadCounter _obtainMemoMismatches = new();
    private static int _obtainMemoMismatchReports;

    // Tests choose the mode per cache.
    internal ObtainMemoMode ObtainMemoUse { get; set; } = ConfiguredObtainMemo;

    // Reads this cache served from the memo, reads it compared in verify mode, and the comparisons that differed.
    internal long ObtainMemoHits { get; private set; }

    internal long ObtainMemoVerifications { get; private set; }

    internal long ObtainMemoMismatches { get; private set; }

    // Reads served, reads that took the full path with the memo on, and the verify comparisons.
    public static string TakeObtainMemoReport() => FormattableString.Invariant(
        $"obtain_memo=served:{_obtainMemoServed.Take()},full:{_obtainMemoFull.Take()},verified:{_obtainMemoVerified.Take()},mismatches:{_obtainMemoMismatches.Take()}");

    // The first of the two ways of the range's set.
    internal static int ObtainMemoSet(ulong address, ulong size) =>
        (int)(((address >> 4) ^ (address >> 17) ^ (size * 0x9E3779B1UL)) & (ObtainMemoSets - 1)) * 2;

    // Serves a clean read of a remembered range; true when served. In verify mode it serves none and returns the answer it
    // would have served, for the full path to compare (RememberObtain).
    private bool TryServeObtainMemo(int set, ulong address, ulong size, bool isTexelBuffer, out (GpuBuffer? Buffer, ulong Offset) kept)
    {
        kept = default;
        var way = _obtainMemos[set].Holds(address, size) ? set : _obtainMemos[set + 1].Holds(address, size) ? set + 1 : -1;
        if (way < 0)
        {
            _obtainMemoFull.Increment();
            return false;
        }

        ref var memo = ref _obtainMemos[way];
        var buffer = memo.Buffer!;
        if (!IsCleanOwner(ref memo, address, size))
        {
            _obtainMemoFull.Increment();
            return false;
        }

        _obtainMemoUses[way] = ++_obtainMemoClock;
        // Image synchronization and publication callbacks may obtain other ranges in this set.
        kept = (buffer, memo.Offset);
        if (ObtainMemoUse == ObtainMemoMode.Verify)
        {
            _obtainMemoFull.Increment();
            return false;
        }

        ObtainMemoHits++;
        _obtainMemoServed.Increment();
        TouchBuffer(memo.Identifier);
        // A clean texel read still takes the bytes of an image over its range, as SynchronizeBuffer does for it.
        if (isTexelBuffer)
        {
            _ = SynchronizeTexelFromImage(buffer, address, size);
        }

        OverlayGuestPublications?.Invoke(buffer, kept.Offset, address, size);
        return true;
    }

    private bool IsCleanOwner(ref ObtainMemo memo, ulong address, ulong size)
    {
        var serial = _registry.RegistrationSerial;
        // Capture before the dirty-page check. A racing CPU fault invalidates this proof for the next read.
        var epoch = _tracker.CpuDirtyEpoch;
        if (memo.Serial == serial && memo.Epoch == epoch)
        {
            return true;
        }
        if (!ReferenceEquals(_registry.TryGetRegisteredBuffer(memo.Identifier), memo.Buffer) ||
            !memo.Buffer!.IsInBounds(address, size) || _tracker.MayHaveCpuDirtyPages(address, size))
            return false;
        memo = memo with { Serial = serial, Epoch = epoch };
        return true;
    }

    // After the full path of a read that bound a persistent buffer: remembers it, and compares it with the answer the memo
    // would have served (verify mode).
    private void RememberObtain(int set, ulong address, ulong size, ResourceSlotIdentifier identifier, GpuBuffer buffer,
        (GpuBuffer? Buffer, ulong Offset) kept)
    {
        var offset = buffer.Offset(address);
        if (kept.Buffer is not null)
        {
            ObtainMemoVerifications++;
            _obtainMemoVerified.Increment();
            if (!ReferenceEquals(kept.Buffer, buffer) || kept.Offset != offset)
            {
                ObtainMemoMismatches++;
                _obtainMemoMismatches.Increment();
                if (Interlocked.Increment(ref _obtainMemoMismatchReports) <= 16)
                {
                    Console.Error.WriteLine(
                        $"[GPU][WARN] The obtain memo's buffer differs from the full path: address=0x{address:X16} size=0x{size:X} " +
                        $"memo_offset=0x{kept.Offset:X} offset=0x{offset:X} same_buffer={ReferenceEquals(kept.Buffer, buffer)}");
                }
            }
        }

        var way = _obtainMemos[set].Holds(address, size) ? set
            : _obtainMemos[set + 1].Holds(address, size) ? set + 1
            : _obtainMemos[set].Buffer is null ? set
            : _obtainMemos[set + 1].Buffer is null ? set + 1
            : _obtainMemoUses[set] <= _obtainMemoUses[set + 1] ? set : set + 1;
        _obtainMemos[way] = new ObtainMemo(address, size, identifier, offset, buffer);
        _obtainMemoUses[way] = ++_obtainMemoClock;
    }
}
