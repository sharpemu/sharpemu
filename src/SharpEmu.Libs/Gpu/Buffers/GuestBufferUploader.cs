// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The cache owns the staging ring; temporary sources retire after their submission.
internal sealed class GuestBufferUploader(
    GpuDeviceInfo device, SubmissionScheduler scheduler, ICpuMemory guest, GpuRingBuffer staging)
{
    private const ulong TemporaryPieceBytes = 1UL << 30;

    public GpuBuffer? PrepareSource(ulong bufferAddress, Span<BufferCopy> regions,
        ulong totalSize, ulong requestedAddress, ulong requestedSize)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.BufferStagingUpload);
        if (regions.IsEmpty)
        {
            return null;
        }

        if (staging.TryMap(totalSize, out var baseOffset, 4))
        {
            foreach (ref var copy in regions)
            {
                ReadGuestBytes(bufferAddress + copy.DstOffset,
                    staging.Mapped.Slice((int)(baseOffset + copy.SrcOffset), (int)copy.Size), requestedAddress, requestedSize);
                copy.SrcOffset += baseOffset;
            }

            staging.Commit();
            return staging;
        }

        var temporary = new GpuBuffer(device, scheduler, GpuBufferUsage.Upload, 0, BufferUsageFlags.TransferSrcBit, totalSize);
        foreach (ref readonly var copy in regions)
        {
            for (var done = 0UL; done < copy.Size;)
            {
                var piece = (int)Math.Min(copy.Size - done, TemporaryPieceBytes);
                ReadGuestBytes(bufferAddress + copy.DstOffset + done,
                    temporary.MappedRange(copy.SrcOffset + done, piece), requestedAddress, requestedSize);
                done += (ulong)piece;
            }
        }

        temporary.Flush(0, totalSize);
        scheduler.QueueCompletionAction(temporary.Dispose);
        return temporary;
    }

    private const ulong GuestPageBytes = 0x1000;
    private const int MaxUnmappedReports = 8;
    private static int _unmappedReports;

    private void ReadGuestBytes(ulong address, Span<byte> destination, ulong requestedAddress, ulong requestedSize)
    {
        if (guest.TryRead(address, destination))
        {
            return;
        }

        // A buffer can cover pages the guest no longer maps: they hold nothing a GPU access may read, so they stage
        // as zero. A mapped page that cannot be read still stops the game below.
        if (TryReadMappedPages(guest, address, destination, out var unmappedBytes))
        {
            ReportUnmappedPages(address, destination.Length, unmappedBytes, requestedAddress, requestedSize);
            return;
        }

        Console.Error.WriteLine($"[GPU][READ_FAILURE] addr=0x{address:X16} size=0x{destination.Length:X}");
        Console.Error.WriteLine($"[GPU][READ_FAILURE] binding=0x{requestedAddress:X16} size=0x{requestedSize:X}");
        try
        {
            Console.Error.WriteLine(guest.DescribeReadRange(address, (ulong)destination.Length));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[GPU][READ_FAILURE] mapping query failed: {error.GetType().Name}");
        }
        throw SubmissionScheduler.Fatal($"Could not read guest memory: addr=0x{address:X16} size=0x{destination.Length:X}");
    }

    // Reads the range page by page: a page without a guest mapping reads as zero, a mapped page must read. False when a
    // mapped page could not be read.
    internal static bool TryReadMappedPages(ICpuMemory memory, ulong address, Span<byte> destination, out ulong unmappedBytes)
    {
        unmappedBytes = 0;
        var offset = 0;
        while (offset < destination.Length)
        {
            var pageAddress = address + (ulong)offset;
            var length = (int)Math.Min((ulong)(destination.Length - offset), GuestPageBytes - (pageAddress & (GuestPageBytes - 1)));
            var piece = destination.Slice(offset, length);
            if (memory.CanRead(pageAddress, (ulong)length))
            {
                if (!memory.TryRead(pageAddress, piece))
                {
                    return false;
                }
            }
            else
            {
                piece.Clear();
                unmappedBytes += (ulong)length;
            }

            offset += length;
        }

        return true;
    }

    private void ReportUnmappedPages(ulong address, int length, ulong unmappedBytes, ulong requestedAddress, ulong requestedSize)
    {
        var report = Interlocked.Increment(ref _unmappedReports);
        if (report > MaxUnmappedReports)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[GPU][WARN] A buffer upload covers guest pages without a mapping; they read as zero: addr=0x{address:X16} " +
            $"size=0x{length:X} unmapped=0x{unmappedBytes:X} binding=0x{requestedAddress:X16} binding_size=0x{requestedSize:X}");
        try
        {
            Console.Error.WriteLine(guest.DescribeReadRange(address, (ulong)length));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[GPU][WARN] mapping query failed: {error.GetType().Name}");
        }

        if (report == 1)
        {
            Console.Error.WriteLine($"[GPU][WARN] first unmapped upload requested from:{Environment.NewLine}{Environment.StackTrace}");
        }
    }
}
