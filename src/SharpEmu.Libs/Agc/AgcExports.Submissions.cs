// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial submits command streams and handles frame boundaries.
public static partial class AgcExports
{
    [SysAbiExport(
        Nid = "UglJIZjGssM",
        ExportName = "sceAgcDriverSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    [SysAbiExport(
        Nid = "AhGvpITrf4M",
        ExportName = "sceAgcDriverAgrSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitDcb(CpuContext ctx)
    {
        if (HostSessionControl.IsShutdownRequested)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
        }

        var profileEnabled = DcbSubmissionProfile.Enabled;
        var callStartTicks = profileEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            TraceAgc($"agc.driver_submit_dcb_rejected packet=0x{packetAddress:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAgc(
            $"agc.driver_submit_dcb packet=0x{packetAddress:X16} addr=0x{commandAddress:X16} " +
            $"dwords={dwordCount} end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        (GuestGpu.Current as IGuestImageSnapshotBackend)?.AttachGuestMemory(ctx.Memory);
        SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.SubmitEntered, 0, 0, commandAddress, dwordCount);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        var setupEndTicks = profileEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        var lockAcquiredTicks = 0L;
        var snapshotEndTicks = 0L;
        var enqueueEndTicks = 0L;
        lock (gpuState.CommandSubmissionGate)
        lock (gpuState.Gate)
        {
            if (profileEnabled)
            {
                lockAcquiredTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            // The prepass shadows geometry state in submit order; the worker owns the interpreter's banks.
            SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.GeometryCaptureStarted,
                0, gpuState.SubmissionSequence + 1, commandAddress, dwordCount);
            var snapshots = CaptureSubmittedGeometry(ctx, gpuState, commandAddress, dwordCount);
            SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.GeometryCaptureFinished,
                0, gpuState.SubmissionSequence + 1, commandAddress, dwordCount);
            if (profileEnabled)
            {
                snapshotEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            if (!TrySubmitCommandStream(
                ctx.Memory,
                0,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                snapshots))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
            }
            if (profileEnabled)
            {
                enqueueEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        if (profileEnabled)
        {
            DcbSubmissionProfile.Record(
                dwordCount,
                setupEndTicks - callStartTicks,
                snapshotEndTicks - lockAcquiredTicks,
                lockAcquiredTicks - setupEndTicks,
                enqueueEndTicks - snapshotEndTicks,
                0,
                enqueueEndTicks - callStartTicks);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gSRnr79F8tQ",
        ExportName = "sceAgcDriverSubmitAcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitAcb(CpuContext ctx)
    {
        if (HostSessionControl.IsShutdownRequested)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
        }

        var ownerHandle = (uint)ctx[CpuRegister.Rdi];
        var packetAddress = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAgc(
            $"agc.driver_submit_acb owner={ownerHandle} packet=0x{packetAddress:X16} " +
            $"addr=0x{commandAddress:X16} dwords={dwordCount} " +
            $"end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.SubmitEntered,
            ownerHandle, 0, commandAddress, dwordCount);
        (GuestGpu.Current as IGuestImageSnapshotBackend)?.AttachGuestMemory(ctx.Memory);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.CommandSubmissionGate)
        lock (gpuState.Gate)
        {
            if (!TrySubmitCommandStream(
                ctx.Memory,
                ownerHandle,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                null))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "h9z6+0hEydk",
        ExportName = "sceAgcSuspendPoint",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SuspendPoint(CpuContext ctx)
    {
        TraceAgc("agc.suspend_point");
        // The frame boundary: off the worker it waits for every accepted submission first.
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.CommandSubmissionGate)
        {
            var outcome = GuestGpu.Current.SubmitDone(ctx.Memory);
            if (outcome != IdleOutcome.Completed)
            {
                TraceAgc($"agc.suspend_point_incomplete outcome={outcome}");
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
            }

            // Match the next interpreter reset. Keep the persistent index base and instance count.
            var capture = gpuState.GeometryCapture;
            capture.CxRegisters.Clear();
            capture.ShRegisters.Clear();
            capture.UcRegisters.Clear();
            capture.IndexSize = 0;
            capture.IndexBufferCount = 0;
            capture.IndirectArgsAddress = 0;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "qj7QZpgr9Uw",
        ExportName = "sceAgcUnknownQj7QZpgr9Uw",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownQj7QZpgr9Uw(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 1, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, 0x8000_0000))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.unknown_qj7 buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"arg1=0x{ctx[CpuRegister.Rsi]:X16} arg2=0x{ctx[CpuRegister.Rdx]:X16}");
        return ReturnPointer(ctx, commandAddress);
    }
    #pragma warning restore SHEM006

    // Requests a host-image refresh after guest memory changes.
    private static void SyncCpuWrittenGuestImages(
        CpuContext ctx,
        ulong scopeAddress = 0,
        ulong scopeByteCount = ulong.MaxValue)
    {
        // The backend performs the read, upload, and tracking reset.
        _ = ctx;
        if (GuestGpu.Current is not IGuestImageSnapshotBackend snapshots ||
            !SharpEmu.HLE.GuestImageWriteTracker.Enabled || scopeByteCount == 0)
        {
            return;
        }

        snapshots.RequestCpuWrittenGuestImageSync(scopeAddress, scopeByteCount);
    }

    // ABI: RDI points to addresses, RSI points to dword counts, and RDX contains the count.
    [SysAbiExport(
        Nid = "6UzEidRZwkg",
        ExportName = "sceAgcDriverSubmitMultiDcbs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    [SysAbiExport(
        Nid = "+T8Xo6LtFJI",
        ExportName = "sceAgcDriverAgrSubmitMultiDcbs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitMultiDcbs(CpuContext ctx)
    {
        if (HostSessionControl.IsShutdownRequested)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
        }

        var addressArray = ctx[CpuRegister.Rdi];
        var sizeArray = ctx[CpuRegister.Rsi];
        var bufferCount = (uint)ctx[CpuRegister.Rdx];
        if (addressArray == 0 || sizeArray == 0 || bufferCount == 0 || bufferCount > 4096)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        (GuestGpu.Current as IGuestImageSnapshotBackend)?.AttachGuestMemory(ctx.Memory);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.CommandSubmissionGate)
        lock (gpuState.Gate)
        {
            for (uint bufferIndex = 0; bufferIndex < bufferCount; bufferIndex++)
            {
                if (!ctx.TryReadUInt64(addressArray + bufferIndex * 8, out var commandAddress) ||
                    commandAddress == 0 ||
                    !ctx.TryReadUInt32(sizeArray + bufferIndex * 4, out var dwordCount) ||
                    dwordCount == 0)
                {
                    continue;
                }

                if (_traceAgc)
                {
                    TraceAgc(
                        $"agc.driver_submit_multi_dcbs index={bufferIndex}/{bufferCount} " +
                        $"addr=0x{commandAddress:X16} dwords={dwordCount}");
                }

                SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.GeometryCaptureStarted,
                    0, gpuState.SubmissionSequence + 1, commandAddress, dwordCount);
                var snapshots = CaptureSubmittedGeometry(ctx, gpuState, commandAddress, dwordCount);
                SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.GeometryCaptureFinished,
                    0, gpuState.SubmissionSequence + 1, commandAddress, dwordCount);
                if (!TrySubmitCommandStream(
                    ctx.Memory,
                    0,
                    commandAddress,
                    dwordCount,
                    ++gpuState.SubmissionSequence,
                    snapshots))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
                }
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TrySubmitCommandStream(
        ICpuMemory memory,
        uint queue,
        ulong address,
        uint dwordCount,
        ulong submissionId,
        object? geometrySnapshots)
    {
        if (HostSessionControl.IsShutdownRequested)
        {
            return false;
        }

        try
        {
            SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.BackendEntered,
                queue, submissionId, address, dwordCount);
            GuestGpu.Current.SubmitCommandStream(memory, queue, address, dwordCount, submissionId, geometrySnapshots);
            SubmissionFlowProfile.RecordGuest(SubmissionFlowProfile.EventKind.BackendReturned,
                queue, submissionId, address, dwordCount);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
