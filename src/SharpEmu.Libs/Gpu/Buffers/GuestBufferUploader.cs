// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The cache owns the staging ring; temporary sources retire after their submission.
internal sealed class GuestBufferUploader(
    GpuDeviceInfo device, SubmissionScheduler scheduler, ICpuMemory guest, GpuRingBuffer staging,
    ParallelGuestCopy? parallelCopy = null)
{
    private const ulong TemporaryPieceBytes = 1UL << 30;

    // An upload of GuestBufferCache.ParallelSweepCopyBytes or more copies its guest bytes in pieces with the parallel
    // copy's helper threads, as a large sweep does: a loading screen can upload pools of gigabytes, and a serial copy
    // holds the render thread for each of them. SHARPEMU_PARALLEL_UPLOAD_COPY=0 keeps the serial copy.
    private static readonly bool ParallelUploadCopy = Environment.GetEnvironmentVariable("SHARPEMU_PARALLEL_UPLOAD_COPY") != "0";
    private const ulong ParallelPieceBytes = 64 * 1024;
    private ParallelGuestCopy.Piece[] _pieces = [];

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
            var copied = TryCopyInParallel(staging, baseOffset, bufferAddress, regions, totalSize);
            foreach (ref var copy in regions)
            {
                if (!copied)
                {
                    ReadGuestBytes(bufferAddress + copy.DstOffset,
                        staging.Mapped.Slice((int)(baseOffset + copy.SrcOffset), (int)copy.Size), requestedAddress, requestedSize);
                }

                copy.SrcOffset += baseOffset;
            }

            staging.Commit();
            return staging;
        }

        var temporary = new GpuBuffer(device, scheduler, GpuBufferUsage.Upload, 0, BufferUsageFlags.TransferSrcBit, totalSize);
        if (!TryCopyInParallel(temporary, 0, bufferAddress, regions, totalSize))
        {
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
        }

        temporary.Flush(0, totalSize);
        scheduler.QueueCompletionAction(temporary.Dispose);
        return temporary;
    }

    // Copies each region to target at targetOffset plus its source offset with the parallel copy. False when there is
    // no parallel copy, the upload is small, or a read failed; the caller then copies serially, which stages pages
    // without a guest mapping as zero and reports a mapped page that cannot be read.
    private unsafe bool TryCopyInParallel(GpuBuffer target, ulong targetOffset, ulong bufferAddress, Span<BufferCopy> regions,
        ulong totalSize)
    {
        if (parallelCopy is null || !ParallelUploadCopy || totalSize < GuestBufferCache.ParallelSweepCopyBytes)
        {
            return false;
        }

        var count = 0;
        foreach (ref readonly var copy in regions)
        {
            count += checked((int)((copy.Size + ParallelPieceBytes - 1) / ParallelPieceBytes));
        }

        if (_pieces.Length < count)
        {
            _pieces = new ParallelGuestCopy.Piece[Math.Max(count, _pieces.Length * 2)];
        }

        count = 0;
        foreach (ref readonly var copy in regions)
        {
            for (var done = 0UL; done < copy.Size; done += ParallelPieceBytes)
            {
                var length = (int)Math.Min(ParallelPieceBytes, copy.Size - done);
                var destination = target.MappedRange(targetOffset + copy.SrcOffset + done, length);
                _pieces[count++] = new ParallelGuestCopy.Piece(bufferAddress + copy.DstOffset + done,
                    (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(destination)), length);
            }
        }

        return parallelCopy.Run(_pieces, count, parallel: true);
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
