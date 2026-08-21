// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private static bool HandleSubmittedMemSemaphore(
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
        if (packetLength < 4 ||
            !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var addressLow) ||
            !TryReadUInt32(ctx, packetAddress + (2 * sizeof(uint)), out var addressHigh) ||
            !TryReadUInt32(ctx, packetAddress + (3 * sizeof(uint)), out var control))
        {
            StopMemSemaphoreQueue(
                state,
                packetAddress,
                "malformed MEM_SEMAPHORE packet");
            return true;
        }

        var packet = DecodeMemSemaphorePacket(addressLow, addressHigh, control);
        if (!packet.IsSupported ||
            packet.Address == 0 ||
            (packet.Address & (sizeof(ulong) - 1)) != 0)
        {
            StopMemSemaphoreQueue(
                state,
                packetAddress,
                $"unsupported MEM_SEMAPHORE selection={packet.Selection} " +
                $"address=0x{packet.Address:X16}");
            return true;
        }

        var queueName = state.QueueName;
        var submissionId = state.ActiveSubmissionId;
        if (packet.IsSignal)
        {
            SubmitOrderedGpuSideEffect(
                ctx,
                gpuState,
                state,
                () =>
                {
                    InvalidateDcbWindowIfOverlaps(packet.Address, sizeof(ulong));
                    var signaled = TrySignalMemSemaphoreAndAssignWaiter(
                        ctx.Memory,
                        packet.Address,
                        packet.WriteSignal,
                        out var storedValue,
                        out var waiterAssigned);
                    if (signaled)
                    {
                        GpuWaitRegistry.RecordProduced(
                            ctx.Memory,
                            packet.Address,
                            storedValue,
                            hasHighDword: true);
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][ERROR] agc.mem_semaphore_signal_failed " +
                            $"queue={queueName} packet=0x{packetAddress:X16} " +
                            $"address=0x{packet.Address:X16}");
                    }

                    if (tracePacket)
                    {
                        TraceAgc(
                            $"agc.mem_semaphore_signal queue={queueName} " +
                            $"submission={submissionId} address=0x{packet.Address:X16} " +
                            $"stored=0x{storedValue:X16} assigned={waiterAssigned} " +
                            $"mode={(packet.WriteSignal ? "write-one" : "increment")} " +
                            $"mailbox={packet.WaitForMailbox} success={signaled}");
                    }
                },
                $"mem_semaphore signal=0x{packet.Address:X16}",
                packetAddress,
                packet.Address,
                sizeof(ulong));
            return false;
        }

        if (!_gpuWaitSuspendEnabled)
        {
            StopMemSemaphoreQueue(
                state,
                packetAddress,
                "MEM_SEMAPHORE wait requires queue suspension");
            return true;
        }

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)packetLength * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + packetLength,
            WaitAddress = packet.Address,
            ReferenceValue = 1,
            Mask = ulong.MaxValue,
            CompareFunction = 3,
            ControlValue = 0,
            Is64Bit = true,
            IsStandard = true,
            IsMemSemaphore = true,
            Memory = ctx.Memory,
            QueueName = queueName,
            SubmissionId = submissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                var armed = TryArmMemSemaphoreWait(
                    ctx.Memory,
                    packet.Address,
                    waiter,
                    out var priorValue);
                if (!armed)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] agc.mem_semaphore_wait_failed " +
                        $"queue={queueName} packet=0x{packetAddress:X16} " +
                        $"address=0x{packet.Address:X16}");
                    return;
                }

                EnsureGpuWaitMonitor(ctx, gpuState);
                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.mem_semaphore_wait queue={queueName} " +
                        $"submission={submissionId} address=0x{packet.Address:X16} " +
                        $"prior=0x{priorValue:X16} mailbox={packet.WaitForMailbox} " +
                        $"suspended={priorValue == 0}");
                }
            },
            $"mem_semaphore wait=0x{packet.Address:X16}",
            packetAddress);
        return true;
    }

    private static void StopMemSemaphoreQueue(
        SubmittedDcbState state,
        ulong packetAddress,
        string reason)
    {
        Console.Error.WriteLine(
            $"[LOADER][ERROR] agc.queue_stopped queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
            $"op=0x{ItMemSemaphore:X2} reason='{reason}'");
    }
}
