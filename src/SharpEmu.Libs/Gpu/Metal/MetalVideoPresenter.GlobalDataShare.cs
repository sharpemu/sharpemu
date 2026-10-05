// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Metal;

// The one authoritative global data share on Metal: a shared buffer every transfer and shader
// touches in queue order. The fault bitmap of device-address programs lives beside it.
internal static partial class MetalVideoPresenter
{
    private const ulong DeviceAddressSpaceBits = 40;
    private const ulong FaultBitmapWords = 1UL << (int)(DeviceAddressSpaceBits - DeviceAddressPaging.PageBits - 5);
    private const nuint StorageModeShared = 0;
    private const nint CommandBufferCompleted = 4;

    private sealed record GlobalDataShareFill(ulong Offset, ulong Size, byte Value);

    private sealed record GlobalDataShareCopyFromGuest(ulong Offset, byte[] Bytes);

    private sealed record GlobalDataShareCopyToGuest(ulong GuestAddress, ulong Offset, ulong Size);

    // The device-address work one command buffer carries: its own fault bitmap and the programs on it.
    private sealed class FaultScan
    {
        public nint CommandBuffer;
        public nint Bitmap;
        public nint Contents;
        public readonly List<ulong> Hashes = new();
    }

    private static nint _globalDataShareBuffer;
    private static nint _globalDataShareContents;
    private static FaultScan? _openFaultScan;
    private static readonly List<FaultScan> _pendingFaultScans = new();
    private static readonly Stack<(nint Buffer, nint Contents)> _freeFaultBitmaps = new();
    private static readonly HashSet<ulong> _faultLoggedHashes = new();

    // Every transfer is ordered guest work; the returned sequence completes after the transfer lands.
    public static long SubmitGlobalDataShareFill(ulong offset, ulong size, byte value)
    {
        lock (_gate)
        {
            return _closed || _thread is null ? 0 : EnqueueGuestWorkLocked(new GlobalDataShareFill(offset, size, value));
        }
    }

    public static long SubmitGlobalDataShareCopyFromGuest(ulong offset, byte[] bytes)
    {
        lock (_gate)
        {
            return _closed || _thread is null ? 0 : EnqueueGuestWorkLocked(new GlobalDataShareCopyFromGuest(offset, bytes));
        }
    }

    public static long SubmitGlobalDataShareCopyToGuest(ulong guestAddress, ulong offset, ulong size)
    {
        lock (_gate)
        {
            return _closed || _thread is null ? 0 : EnqueueGuestWorkLocked(new GlobalDataShareCopyToGuest(guestAddress, offset, size));
        }
    }

    // Valid after the caller waited for the queued work; the shared buffer holds the GPU's result.
    public static void ReadGlobalDataShare(Span<uint> destination, uint wordOffset, uint wordCount)
    {
        var contents = _globalDataShareContents;
        if (contents == 0)
        {
            destination.Clear();
            return;
        }

        unsafe
        {
            var bytes = new ReadOnlySpan<byte>((void*)contents, ManagedCommandStreamHost.GdsBytes);
            EndOfPipe.ReadGdsWords(bytes, destination, wordOffset, wordCount);
        }
    }

    private static nint EnsureGlobalDataShareBuffer(nint device)
    {
        if (_globalDataShareBuffer == 0)
        {
            _globalDataShareBuffer = MetalNative.SendNewBuffer(
                device, MetalNative.Selector("newBufferWithLength:options:"), (nuint)ManagedCommandStreamHost.GdsBytes, StorageModeShared);
            _globalDataShareContents = MetalNative.Send(_globalDataShareBuffer, MetalNative.Selector("contents"));
            unsafe
            {
                new Span<byte>((void*)_globalDataShareContents, ManagedCommandStreamHost.GdsBytes).Clear();
            }
        }

        return _globalDataShareBuffer;
    }

    // A clean bitmap: one a completed scan returned, else a new one.
    private static (nint Buffer, nint Contents) AcquireFaultBitmap(nint device)
    {
        if (_freeFaultBitmaps.Count != 0)
        {
            return _freeFaultBitmaps.Pop();
        }

        var buffer = MetalNative.SendNewBuffer(
            device, MetalNative.Selector("newBufferWithLength:options:"), (nuint)(FaultBitmapWords * sizeof(uint)), StorageModeShared);
        var contents = MetalNative.Send(buffer, MetalNative.Selector("contents"));
        unsafe
        {
            new Span<byte>((void*)contents, (int)(FaultBitmapWords * sizeof(uint))).Clear();
        }

        return (buffer, contents);
    }

    private static nint BeginBlit(nint commandBuffer) => MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));

    private static void EndBlit(nint encoder) => MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));

    // A blit fill repeats one byte; the host sends any other pattern as a copy of expanded bytes.
    private static void ExecuteGlobalDataShareFill(nint device, nint queue, GlobalDataShareFill fill)
    {
        var buffer = EnsureGlobalDataShareBuffer(device);
        var commandBuffer = BeginBatchedGuestCommands(queue);
        var blit = BeginBlit(commandBuffer);
        MetalNative.SendFillBuffer(
            blit, MetalNative.Selector("fillBuffer:range:value:"), buffer,
            new NsRange { Location = (nuint)fill.Offset, Length = (nuint)fill.Size }, fill.Value);
        EndBlit(blit);
    }

    private static void ExecuteGlobalDataShareCopyFromGuest(nint device, nint queue, GlobalDataShareCopyFromGuest copy)
    {
        var buffer = EnsureGlobalDataShareBuffer(device);
        var commandBuffer = BeginBatchedGuestCommands(queue);
        var slice = AllocateUpload(device, Math.Max(copy.Bytes.Length, 1), out var source, out var sourceOffset);
        copy.Bytes.CopyTo(slice);
        var blit = BeginBlit(commandBuffer);
        MetalNative.SendCopyBuffer(
            blit, MetalNative.Selector("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:"),
            source, (nuint)sourceOffset, buffer, (nuint)copy.Offset, (nuint)copy.Bytes.Length);
        EndBlit(blit);
    }

    // The copy lands in an upload slice; the wait makes it a CPU-visible ordering point.
    private static void ExecuteGlobalDataShareCopyToGuest(nint device, nint queue, GlobalDataShareCopyToGuest copy)
    {
        var buffer = EnsureGlobalDataShareBuffer(device);
        var commandBuffer = BeginBatchedGuestCommands(queue);
        var slice = AllocateUpload(device, (int)copy.Size, out var destination, out var destinationOffset);
        var blit = BeginBlit(commandBuffer);
        MetalNative.SendCopyBuffer(
            blit, MetalNative.Selector("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:"),
            buffer, (nuint)copy.Offset, destination, (nuint)destinationOffset, (nuint)copy.Size);
        EndBlit(blit);
        var committed = FlushBatchedGuestCommands();
        WaitForCommittedCommandBuffer(committed);
        unsafe
        {
            fixed (byte* data = slice)
            {
                WriteBuffersBackToGuest([((nint)data, new GuestMemoryBuffer(copy.GuestAddress, [], (int)copy.Size, copy.Size, Pooled: false, Writable: true))]);
            }
        }
    }

    // The bitmap of the command buffer being encoded; the first device-address stage of it opens the scan.
    private static nint RecordFaultScan(nint device, ulong hash)
    {
        _openFaultScan ??= new FaultScan();
        var scan = _openFaultScan;
        if (scan.Bitmap == 0)
        {
            (scan.Bitmap, scan.Contents) = AcquireFaultBitmap(device);
        }

        if (!scan.Hashes.Contains(hash))
        {
            scan.Hashes.Add(hash);
        }

        return scan.Bitmap;
    }

    // A draw that commits its own command buffer must not take the open batch's scan with it.
    private static FaultScan? SuspendOpenFaultScan()
    {
        var suspended = _openFaultScan;
        _openFaultScan = null;
        return suspended;
    }

    private static void ResumeOpenFaultScan(FaultScan? suspended)
    {
        if (_openFaultScan is not null)
        {
            throw new InvalidOperationException("A fault scan is still open after its command buffer was tagged.");
        }

        _openFaultScan = suspended;
    }

    // The committed command buffer that carries the recorded device-address work.
    private static void TagFaultScan(nint commandBuffer)
    {
        if (_openFaultScan is not { } scan)
        {
            return;
        }

        _openFaultScan = null;
        scan.CommandBuffer = MetalNative.Send(commandBuffer, MetalNative.Selector("retain"));
        _pendingFaultScans.Add(scan);
    }

    // After the command buffer completes, the first faulting page is logged once per program hash.
    private static void ScanCompletedFaultBitmaps()
    {
        for (var index = _pendingFaultScans.Count - 1; index >= 0; index--)
        {
            var scan = _pendingFaultScans[index];
            if (MetalNative.Send(scan.CommandBuffer, MetalNative.Selector("status")) < CommandBufferCompleted)
            {
                continue;
            }

            MetalNative.SendVoid(scan.CommandBuffer, MetalNative.Selector("release"));
            _pendingFaultScans.RemoveAt(index);
            unsafe
            {
                var words = new Span<uint>((void*)scan.Contents, (int)FaultBitmapWords);
                var firstPage = -1L;
                for (var word = 0; word < words.Length; word++)
                {
                    if (words[word] != 0)
                    {
                        firstPage = ((long)word << 5) + System.Numerics.BitOperations.TrailingZeroCount(words[word]);
                        break;
                    }
                }

                if (firstPage >= 0)
                {
                    foreach (var hash in scan.Hashes)
                    {
                        if (_faultLoggedHashes.Add(hash))
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] metal.device_address_fault hash=0x{hash:X16} page=0x{firstPage:X} address=0x{firstPage << DeviceAddressPaging.PageBits:X16}");
                        }
                    }

                    // The bitmap returns to the pool clean; no command buffer uses it any more.
                    words.Clear();
                }
            }

            _freeFaultBitmaps.Push((scan.Bitmap, scan.Contents));
        }
    }
}
