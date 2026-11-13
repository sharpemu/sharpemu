// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Pipelines;

// The command stream as a dispatch sees it at its own stream position.
internal interface IKernelWriteStream
{
    bool OnStreamThread { get; }

    bool InCrossQueueOrderWindow { get; }

    bool HasPendingPublication(ulong address, ulong size);

    bool HasPendingRange(ulong address, ulong size);

    bool HasPendingGpuWrite(ulong address, ulong size);

    bool HasImageBytes(ulong address, ulong size);

    bool HasSurfaceMetadata(ulong address, ulong size);

    void PostWrite(ulong address, ReadOnlySpan<byte> data);
}

// The store is queued behind every earlier item and applied in stream order, so earlier readers have
// already taken their bytes and later ones see the store. A write by earlier work could still land after
// it, which keeps the dispatch on the GPU. Bytes the GPU owns when the store applies take it on the GPU
// as well, so they need no download.
internal static class KernelWritePolicy
{
    public static KernelWriteRefusal Check(IKernelWriteStream stream, ulong address, ulong size) =>
        !stream.OnStreamThread ? KernelWriteRefusal.Thread
        : stream.InCrossQueueOrderWindow ? KernelWriteRefusal.CrossQueue
        : stream.HasPendingPublication(address, size) ? KernelWriteRefusal.Publication
        : stream.HasPendingRange(address, size) ? KernelWriteRefusal.PendingRange
        : stream.HasPendingGpuWrite(address, size) ? KernelWriteRefusal.PendingWrite
        : stream.HasImageBytes(address, size) ? KernelWriteRefusal.Image
        : stream.HasSurfaceMetadata(address, size) ? KernelWriteRefusal.Metadata
        : KernelWriteRefusal.None;

    public static KernelWriteRefusal TryPost(IKernelWriteStream stream, ulong address, ReadOnlySpan<uint> words)
    {
        var refusal = Check(stream, address, (ulong)words.Length * sizeof(uint));
        if (refusal == KernelWriteRefusal.None)
        {
            stream.PostWrite(address, MemoryMarshal.AsBytes(words));
        }

        return refusal;
    }
}
