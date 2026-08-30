// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial preserves submitted guest geometry until translated draws execute.

    private static readonly bool _traceVertexRanges = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_RANGES"),
        "1",
        StringComparison.Ordinal);

    private static int _tracedVertexRangeCount;

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

    private sealed record SubmittedIndexSnapshot(
        ulong SourceAddress,
        uint IndexCount,
        int IndexStride,
        byte[] Data);

    private sealed record SubmittedVertexSnapshot(
        ulong ExportShaderAddress,
        IReadOnlyList<Gen5VertexInputBinding> Bindings);

    private static AgcIndexHelpers.ProsperoIndexType GetProsperoIndexType(SubmittedDcbState state) =>
        // IndexSize is latched from ItIndexType and from UC VGT_INDEX_TYPE
        // writes. Do not fall back to a stale UC value when IndexSize is 0 —
        // that mis-classified 16-bit draws as index8 and blanked meshes.
        AgcIndexHelpers.Decode(state.IndexSize);

    /// <summary>
    /// ResolveVertexOffset for the common UC path: GE_INDX_OFFSET is the
    /// DrawIndexed vertexOffset / DrawAuto firstVertex. Embedded-fetch SGPR
    /// fallback is not required when the game latches this register (GTA UI).
    /// </summary>
    private static int GetBaseVertex(SubmittedDcbState state) =>
        state.UcRegisters.TryGetValue(GeIndxOffset, out var indexOffset)
            ? unchecked((int)indexOffset)
            : 0;

    private static GuestIndexBuffer? CreateGuestIndexBuffer(
        CpuContext ctx,
        SubmittedDcbState state,
        uint indexCount)
    {
        if (state.IndexBufferAddress == 0 || indexCount == 0)
        {
            return null;
        }

        var indexType = GetProsperoIndexType(state);
        var guestBytesPerIndex = AgcIndexHelpers.GetGuestStrideBytes(indexType);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)guestBytesPerIndex);
        var guestByteCount = checked((int)(indexCount * (uint)guestBytesPerIndex));
        var address = state.IndexBufferAddress + byteOffset;
        var retained = state.CurrentIndexSnapshot;
        if (retained is not null &&
            retained.SourceAddress == address &&
            retained.IndexCount == indexCount &&
            retained.IndexStride == guestBytesPerIndex &&
            retained.Data.Length >= guestByteCount)
        {
            if (indexType == AgcIndexHelpers.ProsperoIndexType.Index8)
            {
                var expanded = new byte[checked((int)(indexCount * sizeof(ushort)))];
                AgcIndexHelpers.ExpandIndex8ToU16(
                    retained.Data.AsSpan(0, guestByteCount),
                    expanded);
                return new GuestIndexBuffer(
                    expanded,
                    expanded.Length,
                    Is32Bit: false,
                    Pooled: false);
            }

            return new GuestIndexBuffer(
                retained.Data,
                guestByteCount,
                indexType == AgcIndexHelpers.ProsperoIndexType.Index32,
                Pooled: false);
        }

        // Host backends only bind u16/u32. Expand kIndex8 -> u16.
        if (indexType == AgcIndexHelpers.ProsperoIndexType.Index8)
        {
            var guestData = GuestDataPool.Shared.Rent(guestByteCount);
            var guestSpan = guestData.AsSpan(0, guestByteCount);
            if (!ctx.Memory.TryRead(address, guestSpan) &&
                !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, guestSpan))
            {
                GuestDataPool.Shared.Return(guestData);
                return null;
            }

            var hostByteCount = checked((int)(indexCount * sizeof(ushort)));
            var hostData = GuestDataPool.Shared.Rent(hostByteCount);
            AgcIndexHelpers.ExpandIndex8ToU16(
                guestSpan,
                hostData.AsSpan(0, hostByteCount));
            GuestDataPool.Shared.Return(guestData);
            return CreatePooledGuestIndexBuffer(
                hostData,
                hostByteCount,
                is32Bit: false);
        }

        var is32Bit = indexType == AgcIndexHelpers.ProsperoIndexType.Index32;
        var data = GuestDataPool.Shared.Rent(guestByteCount);
        var span = data.AsSpan(0, guestByteCount);
        if (ctx.Memory.TryRead(address, span) ||
            KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
        {
            return CreatePooledGuestIndexBuffer(data, guestByteCount, is32Bit);
        }

        GuestDataPool.Shared.Return(data);
        return null;
    }

    private static GuestIndexBuffer CreatePooledGuestIndexBuffer(
        byte[] data,
        int length,
        bool is32Bit) =>
        new(
            data,
            length,
            is32Bit,
            Pooled: true,
            new GuestIndexBufferLease(data));

    private static bool TryGetRequiredVertexRecordCount(
        CpuContext ctx,
        SubmittedDcbState state,
        uint drawCount,
        bool indexed,
        out uint recordCount)
    {
        var baseVertex = (uint)Math.Max(GetBaseVertex(state), 0);
        recordCount = Math.Max(
            baseVertex + drawCount,
            Math.Max(state.InstanceCount, 1u));
        if (!indexed)
        {
            return true;
        }

        if (state.IndexBufferAddress == 0 || drawCount == 0)
        {
            return false;
        }

        var indexType = GetProsperoIndexType(state);
        var bytesPerIndex = AgcIndexHelpers.GetGuestStrideBytes(indexType);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)bytesPerIndex);
        var address = state.IndexBufferAddress + byteOffset;
        var retained = state.CurrentIndexSnapshot;
        var retainedByteCount = checked((int)(drawCount * (uint)bytesPerIndex));
        if (retained is not null &&
            retained.SourceAddress == address &&
            retained.IndexCount == drawCount &&
            retained.IndexStride == bytesPerIndex &&
            retained.Data.Length >= retainedByteCount)
        {
            var retainedMaxIndex = 0u;
            var retainedSawIndex = false;
            var retainedSpan = retained.Data.AsSpan(0, retainedByteCount);
            for (var index = 0; index < drawCount; index++)
            {
                var offset = checked((int)index * bytesPerIndex);
                uint value = indexType switch
                {
                    AgcIndexHelpers.ProsperoIndexType.Index32 =>
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            retainedSpan.Slice(offset, sizeof(uint))),
                    AgcIndexHelpers.ProsperoIndexType.Index8 => retainedSpan[offset],
                    _ => BinaryPrimitives.ReadUInt16LittleEndian(
                        retainedSpan.Slice(offset, sizeof(ushort))),
                };
                var restart = indexType switch
                {
                    AgcIndexHelpers.ProsperoIndexType.Index32 => uint.MaxValue,
                    AgcIndexHelpers.ProsperoIndexType.Index8 => 0xFFu,
                    _ => ushort.MaxValue,
                };
                if (value == restart)
                {
                    continue;
                }

                retainedMaxIndex = Math.Max(retainedMaxIndex, value);
                retainedSawIndex = true;
            }

            var retainedRecords = retainedSawIndex
                ? baseVertex + retainedMaxIndex + 1
                : Math.Max(baseVertex + 1, 1u);
            recordCount = Math.Max(retainedRecords, Math.Max(state.InstanceCount, 1u));
            return true;
        }

        const int chunkBytes = 64 * 1024;
        var scratch = GuestDataPool.Shared.Rent(chunkBytes);
        var remaining = drawCount;
        var maxIndex = 0u;
        var sawIndex = false;
        try
        {
            while (remaining != 0)
            {
                var chunkIndices = (int)Math.Min(
                    remaining,
                    (uint)(chunkBytes / bytesPerIndex));
                var bytes = chunkIndices * bytesPerIndex;
                var span = scratch.AsSpan(0, bytes);
                if (!ctx.Memory.TryRead(address, span) &&
                    !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
                {
                    return false;
                }

                for (var index = 0; index < chunkIndices; index++)
                {
                    uint value = indexType switch
                    {
                        AgcIndexHelpers.ProsperoIndexType.Index32 =>
                            BinaryPrimitives.ReadUInt32LittleEndian(
                                span.Slice(index * sizeof(uint), sizeof(uint))),
                        AgcIndexHelpers.ProsperoIndexType.Index8 => span[index],
                        _ => BinaryPrimitives.ReadUInt16LittleEndian(
                            span.Slice(index * sizeof(ushort), sizeof(ushort))),
                    };
                    var restart = indexType switch
                    {
                        AgcIndexHelpers.ProsperoIndexType.Index32 => uint.MaxValue,
                        AgcIndexHelpers.ProsperoIndexType.Index8 => 0xFFu,
                        _ => ushort.MaxValue,
                    };
                    if (value == restart)
                    {
                        // Primitive-restart markers do not address vertex data.
                        continue;
                    }

                    maxIndex = Math.Max(maxIndex, value);
                    sawIndex = true;
                }

                address += (uint)bytes;
                remaining -= (uint)chunkIndices;
            }
        }
        finally
        {
            GuestDataPool.Shared.Return(scratch);
        }

        var indexedRecords = sawIndex && maxIndex != uint.MaxValue
            ? baseVertex + maxIndex + 1
            : Math.Max(baseVertex + 1, 1u);
        recordCount = Math.Max(indexedRecords, Math.Max(state.InstanceCount, 1u));
        if (_traceVertexRanges &&
            Interlocked.Increment(ref _tracedVertexRangeCount) <= 512)
        {
            var indexBits = indexType switch
            {
                AgcIndexHelpers.ProsperoIndexType.Index32 => 32,
                AgcIndexHelpers.ProsperoIndexType.Index8 => 8,
                _ => 16,
            };
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.vertex_range indexed=1 draw_count={drawCount} " +
                $"max_index={(sawIndex ? maxIndex : 0)} base_vertex={baseVertex} " +
                $"records={recordCount} instances={state.InstanceCount} " +
                $"index_size={indexBits} index_addr=0x{state.IndexBufferAddress:X16} " +
                $"offset={state.DrawIndexOffset}");
        }
        return true;
    }

    private static IReadOnlyList<GuestVertexBuffer> CreateGuestVertexBuffers(
        IReadOnlyList<Gen5VertexInputBinding> bindings)
    {
        var buffers = new GuestVertexBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            buffers[index] = new GuestVertexBuffer(
                binding.Location,
                binding.ComponentCount,
                binding.DataFormat,
                binding.NumberFormat,
                binding.BaseAddress,
                binding.Stride,
                binding.OffsetBytes,
                binding.Data,
                binding.DataLength,
                binding.DataPooled,
                binding.PerInstance);
        }

        return buffers;
    }

    private static IReadOnlyList<GuestVertexBuffer>
        CreateVertexBufferOwnershipView(
            IReadOnlyList<GuestVertexBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestVertexBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    private static GuestIndexBuffer? CreateIndexBufferOwnershipView(
        GuestIndexBuffer? buffer,
        bool ownsPooledData) =>
        buffer is null
            ? null
            : buffer with { Pooled = ownsPooledData && buffer.Pooled };

    private const long MaximumRetainedIndexBytesPerSubmission = 64L * 1024 * 1024;
    private const long MaximumRetainedVertexBytesPerSubmission = 64L * 1024 * 1024;

    private static Dictionary<ulong, SubmittedIndexSnapshot>? CaptureSubmittedIndexPackets(
        CpuContext ctx,
        ulong commandAddress,
        uint dwordCount,
        uint initialIndexSize,
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
        var captureState = new SubmittedDcbState
        {
            IndexSize = initialIndexSize,
        };
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

            ApplySubmittedRegisters(
                ctx,
                captureState,
                packetAddress,
                length,
                opcode,
                register);

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
                SubmittedIndexSnapshot? indexSnapshot = null;
                if (indexSnapshots is not null &&
                    byteCount64 != 0 &&
                    byteCount64 <= int.MaxValue &&
                    retainedIndexBytes + (long)byteCount64 <=
                        MaximumRetainedIndexBytesPerSubmission)
                {
                    var data = new byte[(int)byteCount64];
                    if (ctx.Memory.TryRead(indexAddress, data) ||
                        KernelMemoryCompatExports.TryReadTrackedLibcHeap(indexAddress, data))
                    {
                        indexSnapshot = new SubmittedIndexSnapshot(
                            indexAddress,
                            indexCount,
                            indexStride,
                            data);
                        indexSnapshots[packetAddress] = indexSnapshot;
                        retainedIndexBytes += data.Length;
                    }
                }

                captureState.IndexBufferAddress = indexAddress;
                captureState.IndexBufferCount = maximumIndexCount;
                captureState.DrawIndexOffset = 0;
                captureState.CurrentIndexSnapshot = indexSnapshot;
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    captureState,
                    packetAddress,
                    indexCount,
                    indexed: true,
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
                    indexed: false,
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

    private static void TryCaptureSubmittedVertexSnapshot(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint drawCount,
        bool indexed,
        Dictionary<ulong, SubmittedVertexSnapshot>? snapshots,
        ref long retainedBytes)
    {
        if (snapshots is null ||
            drawCount == 0 ||
            !TryGetShaderAddress(
                state.ShRegisters,
                SpiShaderPgmLoEs,
                SpiShaderPgmHiEs,
                out var exportShaderAddress))
        {
            return;
        }

        ulong exportShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
        }
        state.ShRegisters.TryGetValue(SpiShaderPgmChksumGs, out var exportShaderChecksum);

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out _,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase,
                shaderChecksum: exportShaderChecksum) ||
            !TryGetRequiredVertexRecordCount(
                ctx,
                state,
                drawCount,
                indexed,
                out var recordCount) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var evaluation,
                out _,
                resolveVertexInputs: true,
                requiredVertexRecordCount: recordCount,
                captureVertexInputsOnly: true))
        {
            return;
        }

        try
        {
            if (evaluation.VertexInputs is not { Count: > 0 } inputs)
            {
                return;
            }

            if (!TryCopySubmittedVertexInputs(
                    inputs,
                    MaximumRetainedVertexBytesPerSubmission - retainedBytes,
                    out var retainedInputs,
                    out var snapshotBytes))
            {
                return;
            }

            snapshots[packetAddress] = new SubmittedVertexSnapshot(
                exportShaderAddress,
                retainedInputs);
            retainedBytes += snapshotBytes;
        }
        finally
        {
            ReturnPooledEvaluationArrays(evaluation);
        }
    }

    internal static bool TryCopySubmittedVertexInputs(
        IReadOnlyList<Gen5VertexInputBinding> inputs,
        long maximumBytes,
        out Gen5VertexInputBinding[] retainedInputs,
        out long retainedBytes)
    {
        retainedInputs = [];
        retainedBytes = 0;
        if (inputs.Count == 0 || maximumBytes <= 0)
        {
            return false;
        }

        var uniqueLengths = new Dictionary<byte[], int>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var input in inputs)
        {
            var length = Math.Clamp(input.DataLength, 0, input.Data.Length);
            if (!uniqueLengths.TryGetValue(input.Data, out var existing) ||
                length > existing)
            {
                uniqueLengths[input.Data] = length;
            }
        }

        retainedBytes = uniqueLengths.Values.Sum(static length => (long)length);
        if (retainedBytes == 0 || retainedBytes > maximumBytes)
        {
            retainedBytes = 0;
            return false;
        }

        var copies = new Dictionary<byte[], byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var (source, length) in uniqueLengths)
        {
            var copy = new byte[length];
            source.AsSpan(0, length).CopyTo(copy);
            copies.Add(source, copy);
        }

        retainedInputs = new Gen5VertexInputBinding[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var copy = copies[input.Data];
            retainedInputs[index] = input with
            {
                Data = copy,
                DataLength = Math.Clamp(input.DataLength, 0, copy.Length),
                DataPooled = false,
            };
        }

        return true;
    }

    private static void ApplySubmittedVertexSnapshot(
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ref Gen5ShaderEvaluation evaluation)
    {
        var snapshot = state.CurrentVertexSnapshot;
        var current = evaluation.VertexInputs;
        if (snapshot is null ||
            snapshot.ExportShaderAddress != exportShaderAddress ||
            current is null ||
            current.Count != snapshot.Bindings.Count)
        {
            return;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var live = current[index];
            var retained = snapshot.Bindings[index];
            if (live.Pc != retained.Pc ||
                live.Location != retained.Location ||
                live.BaseAddress != retained.BaseAddress ||
                live.Stride != retained.Stride ||
                live.OffsetBytes != retained.OffsetBytes)
            {
                return;
            }
        }

        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var binding in current)
        {
            if (binding.DataPooled && returned.Add(binding.Data))
            {
                GuestDataPool.Shared.Return(binding.Data);
            }
        }
        evaluation = evaluation with { VertexInputs = snapshot.Bindings };
    }
}
