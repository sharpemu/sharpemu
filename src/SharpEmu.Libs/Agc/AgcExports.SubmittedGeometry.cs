// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial preserves submitted guest geometry until translated draws execute.

    // Keep submitted geometry stable after the guest reuses its memory.
    // Set either variable to 0 only for a comparison test.
    private static readonly bool _retainSubmittedIndexData = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_RETAIN_SUBMITTED_INDEX_DATA"),
        "0",
        StringComparison.Ordinal);
    private static readonly bool _retainSubmittedVertexData = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_RETAIN_SUBMITTED_VERTEX_DATA"),
        "0",
        StringComparison.Ordinal);

    // The state a snapshot was captured under; the draw validates it against the live banks.
    private sealed record GeometryCaptureFingerprint(
        uint IndexSize,
        ulong IndexAddress,
        uint IndexCount,
        uint DrawIndexOffset,
        uint InstanceCount,
        KeyValuePair<uint, uint>[] ShaderRegisters);

    private sealed record SubmittedIndexSnapshot(
        ulong SourceAddress,
        uint IndexCount,
        int IndexStride,
        byte[] Data,
        GeometryCaptureFingerprint Capture);

    private sealed record SubmittedVertexSnapshot(
        ulong ExportShaderAddress,
        SubmittedVertexData? Data,
        GeometryCaptureFingerprint Capture);

    private const uint ShaderRegisterWindowLength = 8;
    private const uint UserDataRegisterWindowLength = 32;

    // The shader registers the vertex fetch resolution can read: the stage program windows and its user data.
    private static KeyValuePair<uint, uint>[] RecordShaderRegisters(IReadOnlyDictionary<uint, uint> registers)
    {
        // The windows can overlap; each offset is recorded once, in offset order.
        var recorded = new SortedDictionary<uint, uint>();
        void RecordRegisterWindow(uint start, uint length)
        {
            for (var offset = start; offset < start + length; offset++)
            {
                if (registers.TryGetValue(offset, out var value))
                {
                    recorded[offset] = value;
                }
            }
        }

        RecordRegisterWindow(SpiShaderPgmLoVs, ShaderRegisterWindowLength);
        RecordRegisterWindow(SpiShaderPgmLoGs, ShaderRegisterWindowLength);
        RecordRegisterWindow(SpiShaderPgmLoEs, ShaderRegisterWindowLength);
        RecordRegisterWindow(SelectExportUserDataRegister(registers), UserDataRegisterWindowLength);
        return recorded.ToArray();
    }

    private static GeometryCaptureFingerprint CreateCaptureFingerprint(
        SubmittedDcbState captureState,
        ulong indexAddress,
        uint count) =>
        new(
            captureState.IndexSize,
            indexAddress,
            count,
            captureState.DrawIndexOffset,
            captureState.InstanceCount,
            RecordShaderRegisters(captureState.ShRegisters));



    private const long MaximumRetainedIndexBytesPerSubmission = 64L * 1024 * 1024;
    private const long MaximumRetainedVertexBytesPerSubmission = 64L * 1024 * 1024;

    // The prepass owns this shadow; it inherits the state of every earlier accepted submission.
    private static SubmittedGeometrySnapshots? CaptureSubmittedGeometry(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong commandAddress,
        uint dwordCount)
    {
        var indexSnapshots = CaptureSubmittedIndexPackets(
            ctx,
            gpuState.GeometryCapture,
            commandAddress,
            dwordCount,
            out var vertexSnapshots);
        return indexSnapshots is null && vertexSnapshots is null
            ? null
            : new SubmittedGeometrySnapshots(indexSnapshots, vertexSnapshots);
    }

    private static Dictionary<ulong, SubmittedIndexSnapshot>? CaptureSubmittedIndexPackets(
        CpuContext ctx,
        SubmittedDcbState captureState,
        ulong commandAddress,
        uint dwordCount,
        out Dictionary<ulong, SubmittedVertexSnapshot>? vertexSnapshots)
    {
        vertexSnapshots = null;
        if (!_retainSubmittedIndexData &&
            !_retainSubmittedVertexData)
        {
            return null;
        }

        var visited = new HashSet<(ulong Address, uint Dwords)>();
        var indexSnapshots = _retainSubmittedIndexData
            ? new Dictionary<ulong, SubmittedIndexSnapshot>()
            : null;
        vertexSnapshots = _retainSubmittedVertexData
            ? new Dictionary<ulong, SubmittedVertexSnapshot>()
            : null;
        var retainedIndexBytes = 0L;
        var retainedVertexBytes = 0L;
        CaptureSubmittedIndexPacketsCore(
            ctx,
            commandAddress,
            dwordCount,
            visited,
            indexSnapshots,
            vertexSnapshots,
            captureState,
            ref retainedIndexBytes,
            ref retainedVertexBytes,
            depth: 0);
        if (vertexSnapshots is { Count: 0 })
        {
            vertexSnapshots = null;
        }

        return indexSnapshots is { Count: > 0 } ? indexSnapshots : null;
    }

    private static void CaptureSubmittedIndexPacketsCore(
        CpuContext ctx,
        ulong commandAddress,
        uint dwordCount,
        HashSet<(ulong Address, uint Dwords)> visited,
        Dictionary<ulong, SubmittedIndexSnapshot>? indexSnapshots,
        Dictionary<ulong, SubmittedVertexSnapshot>? vertexSnapshots,
        SubmittedDcbState captureState,
        ref long retainedIndexBytes,
        ref long retainedVertexBytes,
        int depth)
    {
        if (commandAddress == 0 || dwordCount == 0 || depth > 8 ||
            !visited.Add((commandAddress, dwordCount)))
        {
            return;
        }

        var offset = 0u;
        while (offset < dwordCount)
        {
            var packetAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, packetAddress, out var header))
            {
                return;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                offset++;
                continue;
            }

            if (packetType != 3)
            {
                return;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                return;
            }

            var opcode = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (opcode == ItIndexType && length >= 2 &&
                TryReadUInt32(ctx, packetAddress + 4, out var packetIndexSize))
            {
                captureState.IndexSize = packetIndexSize & 0x3u;
            }

            ApplyGeometryStatePacket(ctx, captureState, packetAddress, length, opcode, register);

            if (NeedsGeometryRegisterUpdate(opcode, register))
            {
                using var registerProfile = new DcbSubmissionProfile.SnapshotScope(DcbSubmissionProfile.SnapshotPhase.Registers);
                ApplySubmittedRegisters(
                    ctx,
                    captureState,
                    packetAddress,
                    length,
                    opcode,
                    register);
            }

            if (opcode == ItDrawIndex2 && length >= 6 &&
                TryReadUInt32(ctx, packetAddress + 4, out var maximumIndexCount) &&
                TryReadUInt32(ctx, packetAddress + 8, out var indexBaseLo) &&
                TryReadUInt32(ctx, packetAddress + 12, out var indexBaseHi) &&
                TryReadUInt32(ctx, packetAddress + 16, out var indexCount))
            {
                var indexAddress = indexBaseLo | ((ulong)indexBaseHi << 32);
                var indexStride = AgcIndexHelpers.GetGuestStrideBytes(
                    AgcIndexHelpers.Decode(captureState.IndexSize));
                var byteCount64 = (ulong)indexCount * (uint)indexStride;
                captureState.DrawIndexOffset = 0;
                var capture = CreateCaptureFingerprint(captureState, indexAddress, indexCount);
                SubmittedIndexSnapshot? indexSnapshot = null;
                if (indexSnapshots is not null &&
                    byteCount64 != 0 &&
                    byteCount64 <= int.MaxValue &&
                    retainedIndexBytes + (long)byteCount64 <=
                        MaximumRetainedIndexBytesPerSubmission)
                {
                    using var captureProfile = new DcbSubmissionProfile.SnapshotScope(DcbSubmissionProfile.SnapshotPhase.IndexCapture);
                    var data = new byte[(int)byteCount64];
                    if (ctx.Memory.TryRead(indexAddress, data) ||
                        KernelMemoryCompatExports.TryReadTrackedLibcHeap(indexAddress, data))
                    {
                        indexSnapshot = new SubmittedIndexSnapshot(
                            indexAddress,
                            indexCount,
                            indexStride,
                            data,
                            capture);
                        indexSnapshots[packetAddress] = indexSnapshot;
                        retainedIndexBytes += data.Length;
                    }
                }

                captureState.IndexBufferAddress = indexAddress;
                captureState.IndexBufferCount = maximumIndexCount;
                captureState.CurrentIndexSnapshot = indexSnapshot;
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    captureState,
                    packetAddress,
                    indexCount,
                    capture,
                    vertexSnapshots,
                    ref retainedVertexBytes);
                captureState.CurrentIndexSnapshot = null;
            }
            else if (opcode == ItDrawIndexAuto && length >= 3 &&
                     TryReadUInt32(ctx, packetAddress + 4, out var vertexCount))
            {
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    captureState,
                    packetAddress,
                    vertexCount,
                    CreateCaptureFingerprint(captureState, 0, vertexCount),
                    vertexSnapshots,
                    ref retainedVertexBytes);
            }

            if (opcode == ItIndirectBuffer && length >= 4 &&
                TryReadUInt32(ctx, packetAddress + 4, out var chainLow) &&
                TryReadUInt32(ctx, packetAddress + 8, out var chainHigh) &&
                TryReadUInt32(ctx, packetAddress + 12, out var chainDwords))
            {
                var chainAddress = ((ulong)(chainHigh & 0xFFFFu) << 32) | chainLow;
                var chainLength = chainDwords & 0xFFFFFu;
                CaptureSubmittedIndexPacketsCore(
                    ctx,
                    chainAddress,
                    chainLength,
                    visited,
                    indexSnapshots,
                    vertexSnapshots,
                    captureState,
                    ref retainedIndexBytes,
                    ref retainedVertexBytes,
                    depth + 1);
            }

            offset += length;
        }
    }

    // The index, instance and reset packets the prepass must follow to stay self-sufficient.
    private static void ApplyGeometryStatePacket(
        CpuContext ctx,
        SubmittedDcbState captureState,
        ulong packetAddress,
        uint length,
        uint opcode,
        uint register)
    {
        if (opcode == ItNop && register is RDrawReset or RAcbReset or PacketCustomCode.DispatchReset && length >= 2)
        {
            captureState.CxRegisters.Clear();
            captureState.ShRegisters.Clear();
            captureState.UcRegisters.Clear();
            captureState.IndexBufferAddress = 0;
            captureState.IndexBufferCount = 0;
            captureState.IndexSize = 0;
            captureState.InstanceCount = 1;
            captureState.DrawIndexOffset = 0;
            return;
        }

        if (opcode == ItIndexBase && length >= 3 &&
            TryReadUInt32(ctx, packetAddress + 4, out var indexBaseLo) &&
            TryReadUInt32(ctx, packetAddress + 8, out var indexBaseHi))
        {
            captureState.IndexBufferAddress = indexBaseLo | ((ulong)indexBaseHi << 32);
        }
        else if (opcode == ItIndexBufferSize && length >= 2 &&
            TryReadUInt32(ctx, packetAddress + 4, out var indexBufferCount))
        {
            captureState.IndexBufferCount = indexBufferCount;
        }
        else if (opcode == ItNop && register == RIndexCount && length >= 2 &&
            TryReadUInt32(ctx, packetAddress + 4, out var customIndexCount))
        {
            captureState.IndexBufferCount = customIndexCount;
        }
        else if (opcode == ItNumInstances && length >= 2 &&
            TryReadUInt32(ctx, packetAddress + 4, out var instanceCount))
        {
            captureState.InstanceCount = Math.Max(instanceCount, 1);
        }
        else if (opcode == ItDrawIndexOffset2 && length >= 5 &&
            TryReadUInt32(ctx, packetAddress + 8, out var indexOffset))
        {
            captureState.DrawIndexOffset = indexOffset;
        }
    }

    // Geometry capture does not consume context state. The main parser still applies it.
    internal static bool NeedsGeometryRegisterUpdate(uint opcode, uint register) =>
        opcode is not (ItSetContextReg or ItSetContextRegIndirect) &&
        !(opcode == ItNop && register == RCxRegsIndirect);

    private static void TryCaptureSubmittedVertexSnapshot(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint drawCount,
        GeometryCaptureFingerprint capture,
        Dictionary<ulong, SubmittedVertexSnapshot>? snapshots,
        ref long retainedBytes)
    {
        if (GuestGpu.Current is not IGuestImageSnapshotBackend ||
            snapshots is null || drawCount == 0 ||
            !TryGetShaderAddress(state.ShRegisters, SpiShaderPgmLoEs, SpiShaderPgmHiEs, out var exportShaderAddress))
        {
            return;
        }

        if (GetShaderHeaderAddress(exportShaderAddress) == 0) return;
        var shader = CreateShaderHeaderRegistry(ctx).Require(exportShaderAddress, "vertex snapshot");
        state.ShRegisters.TryGetValue(GsUserDataRegister - 1, out var resourceWord);
        var declaredCount = GeometryResource2.Decode(resourceWord).UserScalarCount;
        var writtenCount = 0;
        var userData = new uint[UserScalarRegisters.Capacity];
        // The executor resolves export fetches from the geometry user-data bank.
        for (var index = 0; index < userData.Length; index++)
        {
            if (state.ShRegisters.TryGetValue(GsUserDataRegister + (uint)index, out userData[index]))
            {
                writtenCount = index + 1;
            }
        }

        var userDataCount = declaredCount == 0 ? writtenCount : declaredCount;
        if (userDataCount > userData.Length)
        {
            throw Gpu.Scheduling.SubmissionScheduler.Fatal($"The vertex program declares too many user registers: shader=0x{exportShaderAddress:X16} count={userDataCount}.");
        }

        VertexInputInfo input;
        using (new DcbSubmissionProfile.SnapshotScope(DcbSubmissionProfile.SnapshotPhase.Evaluation))
        {
            input = VertexInputResolver.ResolveVertexInputs(ctx, shader, userData.AsSpan(0, userDataCount));
        }

        using var copyProfile = new DcbSubmissionProfile.SnapshotScope(DcbSubmissionProfile.SnapshotPhase.RetainedCopy);
        if (!SubmittedVertexData.TryCapture(ctx.Memory, input,
                MaximumRetainedVertexBytesPerSubmission - retainedBytes, out var data))
        {
            return;
        }

        snapshots[packetAddress] = new SubmittedVertexSnapshot(exportShaderAddress, data, capture);
        retainedBytes += data!.ByteCount;
        DcbSubmissionProfile.RecordRetainedVertexBytes(data.ByteCount);
    }
}
