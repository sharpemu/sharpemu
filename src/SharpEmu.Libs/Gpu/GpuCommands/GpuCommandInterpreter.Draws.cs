// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The shader registers an indirect draw packet names for the CP to fill from the arguments as the draw executes:
// the base vertex (the start vertex of a non-indexed draw) in DW2[15:0], the start instance in DW3[15:0] and, when
// DW3 bit 27 is set, the draw's index in DW2[31:16]. Location 0 and ShaderRegisterNop name no register.
internal readonly record struct DrawRegisterLocations(uint BaseVertex, uint StartInstance, uint DrawIndex)
{
    public static DrawRegisterLocations FromPacket(uint vertexWord, uint instanceWord) =>
        new(vertexWord & 0xFFFFu, instanceWord & 0xFFFFu, (instanceWord & (1u << 27)) != 0 ? vertexWord >> 16 : 0);

    public static bool Names(uint location) => location != 0 && location != RegisterBankLayout.ShaderRegisterNop;
}

public sealed partial class GpuCommandInterpreter
{
    private const uint DrawIndirectArgumentsSize = 16;
    private const uint DrawIndexedIndirectArgumentsSize = 20;

    internal void SetIndexType(uint indexTypeAndSize) => IndexTypeAndSize = indexTypeAndSize & 0x3u;

    internal void SetIndexBaseAddress(ulong address) => IndexBaseAddress = address;

    internal void SetIndexBufferSize(uint size) => IndexBufferSize = size;

    internal void SetInstanceCount(uint instanceCount) => InstanceCount = instanceCount == 0 ? 1 : instanceCount;

    private ulong IndexElementSize => IndexTypeAndSize switch
    {
        0 => 2,
        1 => 4,
        2 => 1,
        _ => throw _host.Fatal($"The index type is unknown: type={IndexTypeAndSize}."),
    };

    internal void DrawIndexed(ulong packetAddress, uint opcode, uint indexCount, ulong indexAddress, uint instanceCount = 0, int baseVertex = 0, uint firstInstance = 0, DrawOffsetSource offsetSource = DrawOffsetSource.Packet,
        bool baseVertexInRegister = false, bool firstInstanceInRegister = false, int startInstanceUserData = -1, int baseVertexUserData = -1)
    {
        if (instanceCount == 0)
        {
            instanceCount = InstanceCount;
        }

        if (IndexedDrawTrace.Enabled)
        {
            var sample = IndexedDrawTrace.Capture(indexCount, IndexTypeAndSize, indexAddress, _host.Memory.TryRead);
            IndexedDrawTrace.Write(sample, "command", SubmitId, packetAddress);
        }
        if (Rendering.ScissorTrace.Instance is { Active: true } trace)
            trace.DrawIssued(TypedRegisters, SubmitId, QueueId, packetAddress);
        _host.DrawIndexed(SubmitId, new DrawIndexedArguments(packetAddress, opcode, indexCount, indexAddress, IndexTypeAndSize, instanceCount, baseVertex, firstInstance, offsetSource,
            StartInstanceUserData: startInstanceUserData, BaseVertexInRegister: baseVertexInRegister, FirstInstanceInRegister: firstInstanceInRegister,
            BaseVertexUserData: baseVertexUserData));
    }

    internal void DrawIndexedOffset(ulong packetAddress, uint indexOffset, uint indexCount)
    {
        DrawIndexOffset = indexOffset;
        DrawIndexed(packetAddress, PacketOpcode.DrawIndexOffset2, indexCount, IndexBaseAddress + (indexOffset * IndexElementSize));
    }

    internal void DrawAuto(ulong packetAddress, uint opcode, uint vertexCount, uint instanceCount = 0, uint firstVertex = 0, uint firstInstance = 0, DrawOffsetSource offsetSource = DrawOffsetSource.Packet,
        bool firstVertexInRegister = false, bool firstInstanceInRegister = false, int startInstanceUserData = -1, int baseVertexUserData = -1)
    {
        if (instanceCount == 0)
        {
            instanceCount = InstanceCount;
        }

        if (Rendering.ScissorTrace.Instance is { Active: true } trace)
            trace.DrawIssued(TypedRegisters, SubmitId, QueueId, packetAddress);
        _host.DrawAuto(SubmitId, new DrawAutoArguments(packetAddress, opcode, vertexCount, instanceCount, firstVertex, firstInstance, offsetSource, startInstanceUserData,
            FirstVertexInRegister: firstVertexInRegister, FirstInstanceInRegister: firstInstanceInRegister, BaseVertexUserData: baseVertexUserData));
    }

    // Which indirect draws leave their instance count in NUM_INSTANCES for later direct draws. Hardware CP indirect
    // draws write it (AMD PAL re-emits it after them), and a record read on the CPU passes it on by default. A record
    // resolved on the GPU is not read back later by default: read on the CPU at a later direct draw it can hold data
    // the GPU has not written yet (Ghost of Yotei: float data such as 0x3F800000, ~1e9 instances, a lost device).
    // SHARPEMU_INDIRECT_INSTANCE_INHERIT=1 reads that record too, =0 leaves NUM_INSTANCES alone for every indirect draw.
    internal IndirectInstanceInheritance IndirectInstanceInherit { get; set; } =
        ParseIndirectInstanceInheritance(Environment.GetEnvironmentVariable("SHARPEMU_INDIRECT_INSTANCE_INHERIT"));

    internal static IndirectInstanceInheritance ParseIndirectInstanceInheritance(string? mode) => mode switch
    {
        "0" => IndirectInstanceInheritance.None,
        "1" => IndirectInstanceInheritance.All,
        _ => IndirectInstanceInheritance.CpuReadRecords,
    };

    internal enum IndirectInstanceInheritance { None, CpuReadRecords, All }

    // Indirect arguments are read from guest memory now, as the packet executes.
    internal void DrawIndirect(ulong packetAddress, uint opcode, uint dataOffset, uint drawInitiator, bool indexed,
        DrawRegisterLocations locations = default)
    {
        if ((drawInitiator & ~0x20u) != 2u)
        {
            throw _host.Fatal($"The indirect draw initiator is not supported: initiator=0x{drawInitiator:X8} address=0x{packetAddress:X16}.");
        }

        if (DrawIndirectArgumentsBase == 0)
        {
            throw _host.Fatal($"The indirect draw arguments base is zero: address=0x{packetAddress:X16}.");
        }

        DrawIndirectArguments(packetAddress, opcode, DrawIndirectArgumentsBase + dataOffset, indexed, locations, 0);
    }

    internal void DrawIndirectMulti(ulong packetAddress, uint opcode, uint dataOffset, uint maxCountOrCount, ulong countAddress, uint strideInBytes, uint drawInitiator, bool indexed,
        DrawRegisterLocations locations = default)
    {
        if ((drawInitiator & ~0x20u) != 2u)
        {
            throw _host.Fatal($"The indirect draw initiator is not supported: initiator=0x{drawInitiator:X8} address=0x{packetAddress:X16}.");
        }

        if (DrawIndirectArgumentsBase == 0)
        {
            throw _host.Fatal($"The indirect draw arguments base is zero: address=0x{packetAddress:X16}.");
        }

        var drawCount = maxCountOrCount;
        if (countAddress != 0)
        {
            drawCount = Math.Min(ReadDword(countAddress), maxCountOrCount);
        }

        if (drawCount == 0)
        {
            return;
        }

        var argumentsSize = indexed ? DrawIndexedIndirectArgumentsSize : DrawIndirectArgumentsSize;
        if (strideInBytes < argumentsSize)
        {
            throw _host.Fatal($"The indirect draw stride is smaller than the arguments: stride={strideInBytes} size={argumentsSize} address=0x{packetAddress:X16}.");
        }

        for (var draw = 0u; draw < drawCount; draw++)
        {
            DrawIndirectArguments(packetAddress, opcode, DrawIndirectArgumentsBase + dataOffset + ((ulong)draw * strideInBytes), indexed, locations, draw);
        }
    }

    // A value the CP writes to a register reaches the shader there, which adds it to its vertex or instance id; the
    // draw then counts those from 0. A base vertex or start instance in the vertex program's live user data stays in
    // the arguments instead: the draw passes it as its base vertex or first instance and the program stores
    // gl_BaseVertex or gl_BaseInstance in that user data (BaseVertexUserData, StartInstanceUserData), so the host can
    // leave the arguments on the GPU, where a shader may have written them. One named in another register takes its
    // value here, so the draw reads its arguments now.
    private void DrawIndirectArguments(ulong packetAddress, uint opcode, ulong argumentsAddress, bool indexed, DrawRegisterLocations locations, uint drawIndex)
    {
        var startInstanceUserData = VertexUserDataIndex(locations.StartInstance);
        // Only a plain vertex program stores gl_BaseVertex in its user data; a tessellation or mesh stage reads the register.
        var baseVertexUserData = Rendering.RenderExecutor.RunsPlainVertexProgram(TypedRegisters.Context.ShaderStages)
            ? VertexUserDataIndex(locations.BaseVertex)
            : -1;
        var vertexInRegister = DrawRegisterLocations.Names(locations.BaseVertex) && baseVertexUserData < 0;
        var instanceInRegister = DrawRegisterLocations.Names(locations.StartInstance) && startInstanceUserData < 0;
        var registersNeedArguments = vertexInRegister || instanceInRegister;
        if (!indexed)
        {
            if (!registersNeedArguments && _host.ResolvesIndirectDrawOnGpu)
            {
                WriteDrawRegister(locations.DrawIndex, drawIndex);
                // The host reads the four arguments on the GPU, or itself when the draw needs the counts.
                if (IndirectInstanceInherit == IndirectInstanceInheritance.All) _deferredInstanceCountAddress = argumentsAddress + 4;
                if (Rendering.ScissorTrace.Instance is { Active: true } gpuTrace)
                    gpuTrace.DrawIssued(TypedRegisters, SubmitId, QueueId, packetAddress);
                _host.DrawAuto(SubmitId, new DrawAutoArguments(packetAddress, opcode, uint.MaxValue, 1, 0, 0,
                    DrawOffsetSource.IndirectArguments, startInstanceUserData, argumentsAddress, BaseVertexUserData: baseVertexUserData));
                return;
            }

            var vertexCount = ReadDword(argumentsAddress);
            var instanceCount = ReadDword(argumentsAddress + 4);
            var startVertex = ReadDword(argumentsAddress + 8);
            var startInstance = ReadDword(argumentsAddress + 12);
            if (IndirectInstanceInherit != IndirectInstanceInheritance.None) InstanceCount = instanceCount;
            WriteDrawRegisters(locations, startVertex, startInstance, drawIndex, vertexInRegister, instanceInRegister);
            // A record with no instances draws nothing; a zero passed on would mean "use NUM_INSTANCES".
            if (instanceCount == 0) return;
            DrawAuto(packetAddress, opcode, vertexCount, instanceCount, vertexInRegister ? 0 : startVertex,
                instanceInRegister ? 0 : startInstance, DrawOffsetSource.IndirectArguments, vertexInRegister, instanceInRegister,
                startInstanceUserData, baseVertexUserData);
            return;
        }

        if (!registersNeedArguments && _host.ResolvesIndirectDrawOnGpu && IndexTypeAndSize is 0 or 1 && TryGetGpuIndexRange(out var gpuIndexCount))
        {
            WriteDrawRegister(locations.DrawIndex, drawIndex);
            // The host reads the five arguments on the GPU. It draws from the whole index
            // range; the arguments pick the indices. The host falls back to reading them
            // itself when the draw needs the counts.
            if (IndirectInstanceInherit == IndirectInstanceInheritance.All) _deferredInstanceCountAddress = argumentsAddress + 4;
            if (Rendering.ScissorTrace.Instance is { Active: true } trace)
                trace.DrawIssued(TypedRegisters, SubmitId, QueueId, packetAddress);
            _host.DrawIndexed(SubmitId, new DrawIndexedArguments(
                packetAddress, opcode, gpuIndexCount, IndexBaseAddress, IndexTypeAndSize, 1, 0, 0,
                DrawOffsetSource.IndirectArguments, argumentsAddress, UnboundedIndexBuffer: IndexBufferSize == 0,
                StartInstanceUserData: startInstanceUserData, BaseVertexUserData: baseVertexUserData));
            return;
        }

        var indexCountPerInstance = ReadDword(argumentsAddress);
        var indexedInstanceCount = ReadDword(argumentsAddress + 4);
        var startIndex = ReadDword(argumentsAddress + 8);
        var baseVertex = ReadDword(argumentsAddress + 12);
        var indexedStartInstance = ReadDword(argumentsAddress + 16);
        var indexAddress = IndexBaseAddress + (startIndex * IndexElementSize);
        var indexCount = IndexBufferSize != 0 ? Math.Min(indexCountPerInstance, IndexBufferSize) : indexCountPerInstance;
        if (IndirectInstanceInherit != IndirectInstanceInheritance.None) InstanceCount = indexedInstanceCount;
        if (IndexBufferSize == 0 && IndexTypeAndSize is 0 or 1)
        {
            LearnIndexExtent(startIndex, indexCountPerInstance);
        }

        WriteDrawRegisters(locations, baseVertex, indexedStartInstance, drawIndex, vertexInRegister, instanceInRegister);
        if (indexedInstanceCount == 0) return;
        DrawIndexed(packetAddress, opcode, indexCount, indexAddress, indexedInstanceCount,
            vertexInRegister ? 0 : unchecked((int)baseVertex),
            instanceInRegister ? 0 : indexedStartInstance, DrawOffsetSource.IndirectArguments, vertexInRegister, instanceInRegister,
            startInstanceUserData, baseVertexUserData);
    }

    private void WriteDrawRegisters(DrawRegisterLocations locations, uint baseVertex, uint startInstance, uint drawIndex, bool vertexInRegister,
        bool instanceInRegister)
    {
        if (vertexInRegister)
        {
            WriteDrawRegister(locations.BaseVertex, baseVertex);
        }

        if (instanceInRegister)
        {
            WriteDrawRegister(locations.StartInstance, startInstance);
        }

        WriteDrawRegister(locations.DrawIndex, drawIndex);
    }

    // The vertex program's user data index of a base vertex or start instance register, or -1 when the register is not
    // one of the user data registers the program loads.
    private int VertexUserDataIndex(uint location)
    {
        var index = location - ShaderRegisterOffset.SpiShaderUserDataGs0;
        return index < TypedRegisters.Shader.Vertex.GeometryResource2.UserScalarCount ? (int)index : -1;
    }

    private void WriteDrawRegister(uint offset, uint value)
    {
        if (!DrawRegisterLocations.Names(offset))
        {
            return;
        }

        if (offset >= RegisterBankLayout.ShaderRegisterCount)
        {
            throw _host.Fatal($"The draw register is outside the bank: offset=0x{offset:X8}.");
        }

        Registers.Shader[offset] = value;
        RegisterWriteTable.WriteShaderEntry(TypedRegisters, offset, value, 0);
    }

    // An index buffer without a size (INDEX_BUFFER_SIZE never set) has no bound the GPU
    // path could bind. The extent the draws from an index base have used so far, rounded
    // up to the end of its 16 KiB page, stands in for it; every 1024th such draw reads its
    // arguments on the CPU again so the extent can grow.
    private const ulong IndexExtentPageSize = 0x4000;
    private const ulong MaxIndexExtentBytes = 64ul << 20;
    private const int MaxIndexExtents = 4096;
    private readonly Dictionary<ulong, ulong> _indexExtents = new();
    private uint _indexExtentDraws;

    private bool TryGetGpuIndexRange(out uint indexCount)
    {
        if (IndexBufferSize != 0)
        {
            indexCount = IndexBufferSize;
            return true;
        }

        indexCount = 0;
        if ((++_indexExtentDraws & 1023) == 0 || !_indexExtents.TryGetValue(IndexBaseAddress, out var extentBytes))
        {
            return false;
        }

        indexCount = (uint)(extentBytes / IndexElementSize);
        return indexCount != 0;
    }

    private void LearnIndexExtent(uint startIndex, uint indexCount)
    {
        if (indexCount == 0 || IndexBaseAddress == 0)
        {
            return;
        }

        var end = IndexBaseAddress + (((ulong)startIndex + indexCount) * IndexElementSize);
        var extentBytes = ((end + IndexExtentPageSize - 1) & ~(IndexExtentPageSize - 1)) - IndexBaseAddress;
        if (extentBytes > MaxIndexExtentBytes)
        {
            return;
        }

        if (_indexExtents.TryGetValue(IndexBaseAddress, out var known))
        {
            if (known >= extentBytes)
            {
                return;
            }
        }
        else if (_indexExtents.Count >= MaxIndexExtents)
        {
            _indexExtents.Clear();
        }

        _indexExtents[IndexBaseAddress] = extentBytes;
    }

    internal void DispatchDirect(uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0) =>
        _host.DispatchDirect(SubmitId, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress);

    internal void DispatchIndirect(uint dataOffset, uint dispatchInitiator)
    {
        if (DispatchIndirectArgumentsBase == 0)
        {
            throw _host.Fatal($"The indirect dispatch arguments base is zero: offset=0x{dataOffset:X}.");
        }

        DispatchIndirectAt(DispatchIndirectArgumentsBase + dataOffset, dispatchInitiator);
    }

    private const uint DispatchInitiatorUseThreadDimensions = 1u << 5;

    // Group counts written by an earlier shader are only in GPU memory; reading them
    // here waits for the GPU. When the host dispatches indirectly they stay there, and
    // thread counts too when the host converts them to groups on the GPU.
    private void DispatchIndirectAt(ulong argumentsAddress, uint dispatchInitiator)
    {
        if (_host.ResolvesIndirectDispatchOnGpu &&
            ((dispatchInitiator & DispatchInitiatorUseThreadDimensions) == 0 || _host.ResolvesIndirectThreadDispatchOnGpu))
        {
            DispatchDirect(0, 0, 0, dispatchInitiator, argumentsAddress);
            return;
        }

        DispatchDirect(ReadDword(argumentsAddress), ReadDword(argumentsAddress + 4), ReadDword(argumentsAddress + 8), dispatchInitiator, argumentsAddress);
    }

    internal uint DrawIndexPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header == PacketHeader.Make(9, PacketOpcode.DispatchDrawPreamble))
        {
            var indexCount = payload[0];
            var indexAddress = Address(payload[1], payload[2]);
            var maxInstanceCount = payload[3];
            var instanceCount = payload[6];
            var flags = payload[7];
            if (instanceCount > maxInstanceCount || (flags & ~0xA0u) != 0)
            {
                throw _host.Fatal($"The draw preamble is not supported: instances={instanceCount} max={maxInstanceCount} flags=0x{flags:X8} address=0x{packet.PacketAddress:X16}.");
            }

            DrawIndexed(packet.PacketAddress, packet.Opcode, indexCount, indexAddress, instanceCount);
            return 8;
        }

        if (packet.Header == PacketHeader.Make(6, PacketOpcode.DrawIndex2))
        {
            var maxIndexCount = payload[0];
            var indexAddress = Address(payload[1], payload[2]);
            var indexCount = payload[3];
            var flags = payload[4];
            if (indexCount > maxIndexCount || (flags & ~0x20u) != 0)
            {
                throw _host.Fatal($"The indexed draw is not supported: count={indexCount} max={maxIndexCount} flags=0x{flags:X8} address=0x{packet.PacketAddress:X16}.");
            }

            DrawIndexed(packet.PacketAddress, packet.Opcode, indexCount, indexAddress);
            return 5;
        }

        throw _host.Fatal($"The indexed draw header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
    }

    internal uint DrawIndirectPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var indexed = packet.Header == PacketHeader.Make(5, PacketOpcode.DrawIndexIndirect);
        if (!indexed && packet.Header != PacketHeader.Make(5, PacketOpcode.DrawIndirect))
        {
            throw _host.Fatal($"The indirect draw header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        DrawIndirect(packet.PacketAddress, packet.Opcode, payload[0], payload[3], indexed,
            DrawRegisterLocations.FromPacket(payload[1], payload[2]));
        return 4;
    }

    internal uint DrawIndirectMultiPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        var indexed = packet.Header == PacketHeader.Make(10, PacketOpcode.DrawIndexIndirectMulti);
        if (!indexed && packet.Header != PacketHeader.Make(10, PacketOpcode.DrawIndirectMulti))
        {
            throw _host.Fatal($"The multi indirect draw header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var countIndirect = (payload[3] >> 30) & 0x1u;
        var countAddress = countIndirect != 0 ? Address(payload[5], payload[6]) : 0;
        DrawIndirectMulti(packet.PacketAddress, packet.Opcode, payload[0], payload[4], countAddress, payload[7], payload[8], indexed,
            DrawRegisterLocations.FromPacket(payload[1], payload[2]));
        return 9;
    }

    internal uint DrawIndexOffsetPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(5, PacketOpcode.DrawIndexOffset2))
        {
            throw _host.Fatal($"The offset draw header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var maxIndexCount = payload[0];
        var indexOffset = payload[1];
        var indexCount = payload[2];
        var flags = payload[3];
        if (indexCount > maxIndexCount || (flags & ~0x20u) != 0)
        {
            throw _host.Fatal($"The offset draw is not supported: count={indexCount} max={maxIndexCount} flags=0x{flags:X8} address=0x{packet.PacketAddress:X16}.");
        }

        DrawIndexedOffset(packet.PacketAddress, indexOffset, indexCount);
        return 4;
    }

    internal uint DrawIndexAutoPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header != PacketHeader.Make(3, PacketOpcode.DrawIndexAuto))
        {
            throw _host.Fatal($"The auto draw header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var flags = payload[1];
        if ((flags & ~0x22u) != 0)
        {
            throw _host.Fatal($"The auto draw flags are not supported: flags=0x{flags:X8} address=0x{packet.PacketAddress:X16}.");
        }

        DrawAuto(packet.PacketAddress, packet.Opcode, payload[0]);
        return 2;
    }

    // The draw count of this packet sits in the control dword; the stream carries no index buffer.
    internal uint DrawIndexMultiAutoPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Length < 4)
        {
            throw _host.Fatal($"The multi auto draw packet is too short: length={packet.Length} address=0x{packet.PacketAddress:X16}.");
        }

        DrawAuto(packet.PacketAddress, packet.Opcode, (payload[2] >> 21) & 0x7FFu);
        return packet.Length - 1;
    }

    internal uint DispatchDirectPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Opcode != PacketOpcode.DispatchDirect || packet.Length != 5)
        {
            throw _host.Fatal($"The dispatch header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        // A member of a deferred dispatch run goes to the run's workers; any other dispatch closes an open run first (a
        // poisoned member rolls the packets back instead).
        if (_runs is { } runs)
        {
            var execution = RequireExecution();
            var depth = execution.Depth - 1;
            var site = new DeferredIssueSite(SubmitId, depth, execution.At(depth).Address, packet.Offset, _runUndo.Count, UserDataMarker);
            var issue = runs.TryIssue(in site, payload[0], payload[1], payload[2], payload[3]);
            if (issue == DeferredIssue.Issued)
            {
                return 4;
            }

            if ((issue == DeferredIssue.Rewind || runs.IsOpen) && CloseRunInHandler())
            {
                return 4;
            }
        }

        if (_host.ComputeTickets is { } tickets)
        {
            if (tickets.NeedsScan(this, packet.PacketAddress, out var requestedThrough))
            {
                ScanComputeTickets(tickets, in packet, requestedThrough);
            }

            tickets.BeginDispatch(this, packet.PacketAddress);
            try
            {
                DispatchDirect(payload[0], payload[1], payload[2], payload[3]);
            }
            finally
            {
                tickets.EndDispatch();
            }

            return 4;
        }

        DispatchDirect(payload[0], payload[1], payload[2], payload[3]);
        return 4;
    }

    internal uint DispatchIndirectPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Header == PacketHeader.Make(4, PacketOpcode.DispatchIndirect))
        {
            var argumentsAddress = Address(payload[0], payload[1]);
            if (argumentsAddress == 0)
            {
                throw _host.Fatal($"The indirect dispatch arguments address is zero: address=0x{packet.PacketAddress:X16}.");
            }

            DispatchIndirectAt(argumentsAddress, payload[2]);
            return 3;
        }

        if (packet.Header != PacketHeader.Make(3, PacketOpcode.DispatchIndirect))
        {
            throw _host.Fatal($"The indirect dispatch header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        DispatchIndirect(payload[0], payload[1]);
        return 2;
    }

    internal uint IndexTypePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(2, PacketOpcode.IndexType));
        SetIndexType(payload[0]);
        return 1;
    }

    internal uint IndexBufferSizePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(2, PacketOpcode.IndexBufferSize));
        SetIndexBufferSize(payload[0]);
        return 1;
    }

    internal uint IndexBasePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(3, PacketOpcode.IndexBase));
        SetIndexBaseAddress(Address(payload[0], payload[1]));
        return 2;
    }

    internal uint SetInstanceCountPacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        RequireHeader(packet, PacketHeader.Make(2, PacketOpcode.NumInstances));
        SetInstanceCount(payload[0]);
        return 1;
    }

    // Header bit 1 selects the draw (0) or dispatch (1) indirect arguments base.
    internal uint SetBasePacket(in PacketContext packet, ReadOnlySpan<uint> payload)
    {
        if (packet.Opcode != PacketOpcode.SetBase || packet.Length != 4)
        {
            throw _host.Fatal($"The set-base header is not supported: header=0x{packet.Header:X8} address=0x{packet.PacketAddress:X16}.");
        }

        var baseIndex = payload[0] & 0xFu;
        var baseAddress = (payload[1] & ~0x7UL) | ((ulong)(payload[2] & 0xFFFFu) << 32);
        var shaderType = (packet.Header >> 1) & 0x3u;
        if (baseIndex != 1 || shaderType > 1)
        {
            throw _host.Fatal($"The set-base packet is not supported: index={baseIndex} shaderType={shaderType} address=0x{packet.PacketAddress:X16}.");
        }

        if (shaderType == 0)
        {
            DrawIndirectArgumentsBase = baseAddress;
        }
        else
        {
            DispatchIndirectArgumentsBase = baseAddress;
        }

        return 3;
    }

    private void RequireHeader(in PacketContext packet, uint header)
    {
        if (packet.Header != header)
        {
            throw _host.Fatal($"The packet header is not supported: header=0x{packet.Header:X8} expected=0x{header:X8} address=0x{packet.PacketAddress:X16}.");
        }
    }
}
