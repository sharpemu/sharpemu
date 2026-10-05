// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

public struct GeometryEngineControlRegisters
{
    public ushort PrimitiveGroupSize;
    public ushort VertexGroupSize;
}

public struct GeometryEngineUserVectorEnableRegisters
{
    public bool VectorRegister1;
    public bool VectorRegister2;
    public bool VectorRegister3;
}

public struct OrderedAppendCounter
{
    public uint Counter;
    public uint Address;

    public readonly uint SpaceAvailable => Counter;

    public readonly uint AddressBytes => Address & 0xFFFFu;

    public readonly uint CrawlerType => (Address >> 16) & 0x3u;

    public readonly uint CrawlerId => (Address >> 20) & 0xFu;

    public readonly bool IsCounterEnabled => (Address & 0x8000_0000u) != 0;

    public readonly bool IsAllocationCrawlerDisabled => (Address & 0x4000_0000u) != 0;
}

// The control register selects the counter the next counter and address writes land in.
public sealed class OrderedAppendRegisters
{
    public const int CounterCount = 8;

    public uint Control;
    public OrderedAppendCounter[] Counters = new OrderedAppendCounter[CounterCount];

    public uint Index => Control & (CounterCount - 1u);

    public OrderedAppendRegisters Copy() => new() { Control = Control, Counters = (OrderedAppendCounter[])Counters.Clone() };
}

// The user-config bank: primitive assembly and the ordered-append counters.
public sealed class UserConfigRegisters
{
    public uint PrimitiveType;
    public uint IndexOffset;
    public uint ObjectId;
    public uint PrimitiveResetControl;
    public GeometryEngineControlRegisters GeometryEngineControl;
    public GeometryEngineUserVectorEnableRegisters GeometryEngineUserVectorEnable;
    public OrderedAppendRegisters OrderedAppend = new();

    public UserConfigRegisters Copy()
    {
        var copy = (UserConfigRegisters)MemberwiseClone();
        copy.OrderedAppend = OrderedAppend.Copy();
        return copy;
    }
}
