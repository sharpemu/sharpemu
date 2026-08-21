// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private static bool HandleSubmittedAtomicMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint packetLength,
        uint dwordCount,
        bool tracePacket)
    {
        if (packetLength < 9 ||
            !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var control) ||
            !TryReadUInt32(ctx, packetAddress + (2 * sizeof(uint)), out var addressLow) ||
            !TryReadUInt32(ctx, packetAddress + (3 * sizeof(uint)), out var addressHigh) ||
            !TryReadUInt32(ctx, packetAddress + (4 * sizeof(uint)), out var sourceLow) ||
            !TryReadUInt32(ctx, packetAddress + (5 * sizeof(uint)), out var sourceHigh) ||
            !TryReadUInt32(ctx, packetAddress + (6 * sizeof(uint)), out var compareLow) ||
            !TryReadUInt32(ctx, packetAddress + (7 * sizeof(uint)), out var compareHigh) ||
            !TryReadUInt32(ctx, packetAddress + (8 * sizeof(uint)), out var loopControl))
        {
            StopSubmittedQueue(
                ctx,
                state,
                packetAddress,
                ItAtomicMem,
                "malformed ATOMIC_MEM packet");
            return true;
        }

        var packet = DecodeAtomicMemPacket(
            control,
            addressLow,
            addressHigh,
            sourceLow,
            sourceHigh,
            compareLow,
            compareHigh,
            loopControl);
        var usesAsyncEncoding = !ReferenceEquals(state, gpuState.Graphics);
        if (!packet.IsSupported ||
            !packet.HasValidEngine(usesAsyncEncoding) ||
            !TryValidateAtomicMemRange(ctx.Memory, packet.Address, packet.ByteCount))
        {
            StopSubmittedQueue(
                ctx,
                state,
                packetAddress,
                ItAtomicMem,
                $"unsupported ATOMIC_MEM op=0x{packet.RawOperation:X2} " +
                $"command={packet.Command} engine={packet.EngineSelection} " +
                $"addr=0x{packet.Address:X16}");
            return true;
        }

        var usesPfpReturn = !usesAsyncEncoding && packet.EngineSelection == 1;
        var returnSequence = packet.ReturnsData
            ? BeginAtomicReturn(state, usesPfpReturn)
            : 0;

        void ApplyAtomic()
        {
            InvalidateDcbWindowIfOverlaps(
                packet.Address,
                checked((ulong)packet.ByteCount));
            var applied = TryApplyAtomicMem(
                ctx.Memory,
                packet,
                out var priorValue,
                out var newValue,
                out var comparePassed);
            if (!applied)
            {
                StopSubmittedQueueFromOrderedAction(
                    ctx,
                    gpuState,
                    state,
                    packetAddress,
                    ItAtomicMem,
                    $"ATOMIC_MEM address is unreadable addr=0x{packet.Address:X16}");
                return;
            }

            lock (gpuState.Gate)
            {
                if (packet.ReturnsData)
                {
                    SetAtomicReturnValue(
                        state,
                        usesPfpReturn,
                        returnSequence,
                        priorValue);
                }

                if (packet.Command == 1 && !state.IsFaulted)
                {
                    RegisterAtomicLoopContinuation(
                        ctx,
                        state,
                        commandAddress,
                        packetAddress,
                        offset,
                        packetLength,
                        dwordCount,
                        packet,
                        comparePassed);
                }
            }

            GpuWaitRegistry.RecordProduced(
                ctx.Memory,
                packet.Address,
                newValue,
                hasHighDword: packet.Is64Bit);
            MirrorDmaWriteToGuestImage(
                ctx,
                packet.Address,
                checked((ulong)packet.ByteCount),
                fillValue: null);

            if (tracePacket)
            {
                TraceAgc(
                    $"agc.atomic_mem queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"op=0x{packet.RawOperation:X2} command={packet.Command} " +
                    $"engine={packet.EngineSelection} addr=0x{packet.Address:X16} " +
                    $"src=0x{packet.SourceData:X16} cmp=0x{packet.CompareData:X16} " +
                    $"prior=0x{priorValue:X16} new=0x{newValue:X16} " +
                    $"compare_passed={comparePassed} cache={packet.CachePolicy} " +
                    $"loop_cycles={packet.LoopIntervalCycles} " +
                    $"return_sequence={returnSequence}");
            }
        }

        if (packet.Command == 1)
        {
            if (!_gpuWaitSuspendEnabled)
            {
                StopSubmittedQueue(
                    ctx,
                    state,
                    packetAddress,
                    ItAtomicMem,
                    "ATOMIC_MEM loop requires queue suspension");
                return true;
            }

            state.IsSuspended = true;
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            ApplyAtomic,
            $"atomic_mem addr=0x{packet.Address:X16} op=0x{packet.RawOperation:X2}",
            packetAddress,
            packet.Address,
            checked((ulong)packet.ByteCount),
            deferLabelCompletion: true);
        return packet.Command == 1;
    }

    private static void RegisterAtomicLoopContinuation(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint packetLength,
        uint dwordCount,
        in AtomicMemPacket packet,
        bool comparePassed)
    {
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = comparePassed
                ? packetAddress + checked((ulong)packetLength * sizeof(uint))
                : packetAddress,
            TotalDwords = dwordCount,
            ResumeOffset = comparePassed ? offset + packetLength : offset,
            WaitAddress = packet.Address,
            ReferenceValue = GetAtomicLoopReferenceValue(packet),
            Mask = packet.Is64Bit ? ulong.MaxValue : uint.MaxValue,
            CompareFunction = comparePassed ? 0u : 3u,
            ControlValue = packet.CachePolicy << 25,
            Is64Bit = packet.Is64Bit,
            IsStandard = true,
            RequiresExactEquality = !comparePassed,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
            Latched = comparePassed,
        };
        // The host wait registry replaces the hardware polling interval. It
        // retries after a real producer changes the comparison address.
        GpuWaitRegistry.Register(packet.Address, waiter);
        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
    }

    private static ulong BeginAtomicReturn(
        SubmittedDcbState state,
        bool usesPfpReturn)
    {
        if (usesPfpReturn)
        {
            state.AtomicReturnPfpPending = true;
            return ++state.AtomicReturnPfpSequence;
        }

        state.AtomicReturnMePending = true;
        return ++state.AtomicReturnMeSequence;
    }

    private static void SetAtomicReturnValue(
        SubmittedDcbState state,
        bool usesPfpReturn,
        ulong sequence,
        ulong value)
    {
        if (usesPfpReturn)
        {
            state.AtomicReturnPfpData = value;
            state.AtomicReturnPfpValid = true;
            state.AtomicReturnPfpCompletedSequence = sequence;
            state.AtomicReturnPfpPending =
                sequence < state.AtomicReturnPfpSequence;
        }
        else
        {
            state.AtomicReturnMeData = value;
            state.AtomicReturnMeValid = true;
            state.AtomicReturnMeCompletedSequence = sequence;
            state.AtomicReturnMePending =
                sequence < state.AtomicReturnMeSequence;
        }
    }

    private static bool TryValidateAtomicMemRange(
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
