// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

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

    internal void DrawIndexed(ulong packetAddress, uint opcode, uint indexCount, ulong indexAddress, uint instanceCount = 0, int baseVertex = 0, uint firstInstance = 0, DrawOffsetSource offsetSource = DrawOffsetSource.Packet)
    {
        if (instanceCount == 0)
        {
            instanceCount = InstanceCount;
        }

        _host.DrawIndexed(SubmitId, new DrawIndexedArguments(packetAddress, opcode, indexCount, indexAddress, IndexTypeAndSize, instanceCount, baseVertex, firstInstance, offsetSource));
    }

    internal void DrawIndexedOffset(ulong packetAddress, uint indexOffset, uint indexCount)
    {
        DrawIndexOffset = indexOffset;
        DrawIndexed(packetAddress, PacketOpcode.DrawIndexOffset2, indexCount, IndexBaseAddress + (indexOffset * IndexElementSize));
    }

    internal void DrawAuto(ulong packetAddress, uint opcode, uint vertexCount, uint instanceCount = 0, uint firstVertex = 0, uint firstInstance = 0, DrawOffsetSource offsetSource = DrawOffsetSource.Packet)
    {
        if (instanceCount == 0)
        {
            instanceCount = InstanceCount;
        }

        _host.DrawAuto(SubmitId, new DrawAutoArguments(packetAddress, opcode, vertexCount, instanceCount, firstVertex, firstInstance, offsetSource));
    }

    // Indirect arguments are read from guest memory now, as the packet executes.
    internal void DrawIndirect(ulong packetAddress, uint opcode, uint dataOffset, uint drawInitiator, bool indexed)
    {
        if ((drawInitiator & ~0x20u) != 2u)
        {
            throw _host.Fatal($"The indirect draw initiator is not supported: initiator=0x{drawInitiator:X8} address=0x{packetAddress:X16}.");
        }

        if (DrawIndirectArgumentsBase == 0)
        {
            throw _host.Fatal($"The indirect draw arguments base is zero: address=0x{packetAddress:X16}.");
        }

        DrawIndirectArguments(packetAddress, opcode, DrawIndirectArgumentsBase + dataOffset, indexed);
    }

    internal void DrawIndirectMulti(ulong packetAddress, uint opcode, uint dataOffset, uint maxCountOrCount, ulong countAddress, uint strideInBytes, uint drawInitiator, bool indexed)
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
            DrawIndirectArguments(packetAddress, opcode, DrawIndirectArgumentsBase + dataOffset + ((ulong)draw * strideInBytes), indexed);
        }
    }

    private void DrawIndirectArguments(ulong packetAddress, uint opcode, ulong argumentsAddress, bool indexed)
    {
        if (!indexed)
        {
            var vertexCount = ReadDword(argumentsAddress);
            var instanceCount = ReadDword(argumentsAddress + 4);
            var startVertex = ReadDword(argumentsAddress + 8);
            var startInstance = ReadDword(argumentsAddress + 12);
            InstanceCount = instanceCount;
            DrawAuto(packetAddress, opcode, vertexCount, instanceCount, startVertex, startInstance, DrawOffsetSource.IndirectArguments);
            return;
        }

        var indexCountPerInstance = ReadDword(argumentsAddress);
        var indexedInstanceCount = ReadDword(argumentsAddress + 4);
        var startIndex = ReadDword(argumentsAddress + 8);
        var baseVertex = ReadDword(argumentsAddress + 12);
        var indexedStartInstance = ReadDword(argumentsAddress + 16);
        var indexAddress = IndexBaseAddress + (startIndex * IndexElementSize);
        var indexCount = IndexBufferSize != 0 ? Math.Min(indexCountPerInstance, IndexBufferSize) : indexCountPerInstance;
        InstanceCount = indexedInstanceCount;
        DrawIndexed(packetAddress, opcode, indexCount, indexAddress, indexedInstanceCount, unchecked((int)baseVertex), indexedStartInstance, DrawOffsetSource.IndirectArguments);
    }

    internal void DispatchDirect(uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator) =>
        _host.DispatchDirect(SubmitId, groupsX, groupsY, groupsZ, dispatchInitiator);

    internal void DispatchIndirect(uint dataOffset, uint dispatchInitiator)
    {
        if (DispatchIndirectArgumentsBase == 0)
        {
            throw _host.Fatal($"The indirect dispatch arguments base is zero: offset=0x{dataOffset:X}.");
        }

        var argumentsAddress = DispatchIndirectArgumentsBase + dataOffset;
        DispatchDirect(ReadDword(argumentsAddress), ReadDword(argumentsAddress + 4), ReadDword(argumentsAddress + 8), dispatchInitiator);
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

        DrawIndirect(packet.PacketAddress, packet.Opcode, payload[0], payload[3], indexed);
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
        DrawIndirectMulti(packet.PacketAddress, packet.Opcode, payload[0], payload[4], countAddress, payload[7], payload[8], indexed);
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

            DispatchDirect(ReadDword(argumentsAddress), ReadDword(argumentsAddress + 4), ReadDword(argumentsAddress + 8), payload[2]);
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
