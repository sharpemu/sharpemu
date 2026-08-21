// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private static bool HandleSubmittedCopyData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool tracePacket)
    {
        if (packetLength < 6 ||
            !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var control) ||
            !TryReadUInt32(ctx, packetAddress + (2 * sizeof(uint)), out var sourceLow) ||
            !TryReadUInt32(ctx, packetAddress + (3 * sizeof(uint)), out var sourceHigh) ||
            !TryReadUInt32(ctx, packetAddress + (4 * sizeof(uint)), out var destinationLow) ||
            !TryReadUInt32(ctx, packetAddress + (5 * sizeof(uint)), out var destinationHigh))
        {
            StopSubmittedQueue(
                ctx,
                state,
                packetAddress,
                ItCopyData,
                "malformed COPY_DATA packet");
            return true;
        }

        var packet = DecodeCopyDataPacket(
            control,
            sourceLow,
            sourceHigh,
            destinationLow,
            destinationHigh,
            usesAsyncEncoding: !ReferenceEquals(state, gpuState.Graphics));
        if (!packet.IsSupported || packet.SourceIsAtomicReturn)
        {
            StopSubmittedQueue(
                ctx,
                state,
                packetAddress,
                ItCopyData,
                $"unsupported COPY_DATA src_sel={packet.SourceSelection} " +
                $"dst_sel={packet.DestinationSelection} bits={(packet.Is64Bit ? 64 : 32)}");
            return true;
        }

        if (!TryValidateCopyDataRange(ctx.Memory, packet.DestinationAddress, packet.ByteCount) ||
            (packet.SourceIsMemory &&
             !TryValidateCopyDataRange(ctx.Memory, packet.SourceValue, packet.ByteCount)))
        {
            StopSubmittedQueue(
                ctx,
                state,
                packetAddress,
                ItCopyData,
                $"unmapped COPY_DATA src=0x{packet.SourceValue:X16} " +
                $"dst=0x{packet.DestinationAddress:X16} bytes={packet.ByteCount}");
            return true;
        }

        void ApplyCopy()
        {
            InvalidateDcbWindowIfOverlaps(
                packet.DestinationAddress,
                checked((ulong)packet.ByteCount));
            var copied = TryApplyCopyData(ctx.Memory, packet, out var value);
            if (copied)
            {
                GpuWaitRegistry.RecordProduced(
                    ctx.Memory,
                    packet.DestinationAddress,
                    value,
                    hasHighDword: packet.Is64Bit);
                MirrorDmaWriteToGuestImage(
                    ctx,
                    packet.DestinationAddress,
                    checked((ulong)packet.ByteCount),
                    fillValue: null);
            }
            else
            {
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] agc.copy_data_failed queue={state.QueueName} " +
                    $"packet=0x{packetAddress:X16} src=0x{packet.SourceValue:X16} " +
                    $"dst=0x{packet.DestinationAddress:X16} bytes={packet.ByteCount}");
            }

            if (tracePacket)
            {
                TraceAgc(
                    $"agc.copy_data queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"src_sel={packet.SourceSelection} dst_sel={packet.DestinationSelection} " +
                    $"src=0x{packet.SourceValue:X16} dst=0x{packet.DestinationAddress:X16} " +
                    $"bytes={packet.ByteCount} value=0x{value:X16} copied={copied} " +
                    $"src_cache={packet.SourceCachePolicy} " +
                    $"dst_cache={packet.DestinationCachePolicy} " +
                    $"confirm={packet.WriteConfirm}");
            }
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            ApplyCopy,
            $"copy_data dst=0x{packet.DestinationAddress:X16} bytes={packet.ByteCount}",
            packetAddress,
            packet.DestinationAddress,
            checked((ulong)packet.ByteCount),
            deferLabelCompletion: true);
        return false;
    }

    private static bool TryValidateCopyDataRange(
        ICpuMemory memory,
        ulong address,
        int byteCount)
    {
        if (address == 0 ||
            (address & (ulong)(byteCount - 1)) != 0)
        {
            return false;
        }

        Span<byte> probe = stackalloc byte[sizeof(ulong)];
        return memory.TryRead(address, probe[..byteCount]);
    }
}
