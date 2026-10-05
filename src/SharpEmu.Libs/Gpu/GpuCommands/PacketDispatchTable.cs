// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

// Returns the payload dwords the packet consumed; the loop adds the header.
public delegate uint PacketHandler(GpuCommandInterpreter processor, in PacketContext packet, ReadOnlySpan<uint> payload);

public static class PacketDispatchTable
{
    public static readonly PacketHandler?[] Opcodes = BuildOpcodes();

    public static readonly PacketHandler?[] CustomCodes = BuildCustomCodes();

    private static PacketHandler?[] BuildOpcodes()
    {
        var table = new PacketHandler?[256];
        table[PacketOpcode.Nop] = static (interpreter, in packet, payload) => interpreter.NopPacket(in packet, payload);
        table[PacketOpcode.SetBase] = static (interpreter, in packet, payload) => interpreter.SetBasePacket(in packet, payload);
        table[PacketOpcode.ClearState] = static (interpreter, in packet, payload) => interpreter.ClearStatePacket(in packet, payload);
        table[PacketOpcode.IndexBase] = static (interpreter, in packet, payload) => interpreter.IndexBasePacket(in packet, payload);
        table[PacketOpcode.IndexBufferSize] = static (interpreter, in packet, payload) => interpreter.IndexBufferSizePacket(in packet, payload);
        table[PacketOpcode.DispatchDirect] = static (interpreter, in packet, payload) => interpreter.DispatchDirectPacket(in packet, payload);
        table[PacketOpcode.DispatchIndirect] = static (interpreter, in packet, payload) => interpreter.DispatchIndirectPacket(in packet, payload);
        table[PacketOpcode.AtomicMemory] = static (interpreter, in packet, payload) => interpreter.AtomicMemoryPacket(in packet, payload);
        table[PacketOpcode.DrawIndirect] = static (interpreter, in packet, payload) => interpreter.DrawIndirectPacket(in packet, payload);
        table[PacketOpcode.DrawIndexIndirect] = static (interpreter, in packet, payload) => interpreter.DrawIndirectPacket(in packet, payload);
        table[PacketOpcode.DrawIndirectMulti] = static (interpreter, in packet, payload) => interpreter.DrawIndirectMultiPacket(in packet, payload);
        table[PacketOpcode.DrawIndexIndirectMulti] = static (interpreter, in packet, payload) => interpreter.DrawIndirectMultiPacket(in packet, payload);
        table[PacketOpcode.DrawIndex2] = static (interpreter, in packet, payload) => interpreter.DrawIndexPacket(in packet, payload);
        table[PacketOpcode.DrawIndexOffset2] = static (interpreter, in packet, payload) => interpreter.DrawIndexOffsetPacket(in packet, payload);
        table[PacketOpcode.DispatchDrawPreamble] = static (interpreter, in packet, payload) => interpreter.DrawIndexPacket(in packet, payload);
        table[PacketOpcode.DrawIndexMultiAuto] = static (interpreter, in packet, payload) => interpreter.DrawIndexMultiAutoPacket(in packet, payload);
        table[PacketOpcode.PfpSyncMe] = static (interpreter, in packet, payload) => interpreter.PfpSyncMePacket(in packet, payload);
        table[PacketOpcode.IndexType] = static (interpreter, in packet, payload) => interpreter.IndexTypePacket(in packet, payload);
        table[PacketOpcode.NumInstances] = static (interpreter, in packet, payload) => interpreter.SetInstanceCountPacket(in packet, payload);
        table[PacketOpcode.DrawIndexAuto] = static (interpreter, in packet, payload) => interpreter.DrawIndexAutoPacket(in packet, payload);
        table[PacketOpcode.ConditionalExecute] = static (interpreter, in packet, payload) => interpreter.ConditionalExecutePacket(in packet, payload);
        table[PacketOpcode.SetPredication] = static (interpreter, in packet, payload) => interpreter.SetPredicationPacket(in packet, payload);
        table[PacketOpcode.WriteData] = static (interpreter, in packet, payload) => interpreter.WriteDataPacket(in packet, payload);
        table[PacketOpcode.MemorySemaphore] = static (interpreter, in packet, payload) => interpreter.MemorySemaphorePacket(in packet, payload);
        table[PacketOpcode.WaitRegisterMemory] = static (interpreter, in packet, payload) => interpreter.WaitRegisterMemory32Packet(in packet, payload);
        table[PacketOpcode.IndirectBuffer] = static (interpreter, in packet, payload) => interpreter.IndirectBufferPacket(in packet, payload);
        table[PacketOpcode.CopyData] = static (interpreter, in packet, payload) => interpreter.CopyDataPacketHandler(in packet, payload);
        table[PacketOpcode.ConditionalWrite] = static (interpreter, in packet, payload) => interpreter.ConditionalWritePacket(in packet, payload);
        table[PacketOpcode.EventWrite] = static (interpreter, in packet, payload) => interpreter.EventWritePacket(in packet, payload);
        table[PacketOpcode.EventWriteEndOfPipe] = static (interpreter, in packet, payload) => interpreter.EventWriteEndOfPipePacket(in packet, payload);
        table[PacketOpcode.EventWriteEndOfShader] = static (interpreter, in packet, payload) => interpreter.EventWriteEndOfShaderPacket(in packet, payload);
        table[PacketOpcode.ReleaseMemory] = static (interpreter, in packet, payload) => interpreter.ReleaseMemoryNativePacket(in packet, payload);
        table[PacketOpcode.DmaData] = static (interpreter, in packet, payload) => interpreter.DmaDataPacket(in packet, payload);
        table[PacketOpcode.AcquireMemory] = static (interpreter, in packet, payload) => interpreter.AcquireMemoryPacket(in packet, payload);
        table[PacketOpcode.Rewind] = static (interpreter, in packet, payload) => interpreter.RewindPacket(in packet, payload);
        table[PacketOpcode.SetContextRegister] = static (interpreter, in packet, payload) => interpreter.SetContextRegisterPacket(in packet, payload);
        table[PacketOpcode.SetShaderRegister] = static (interpreter, in packet, payload) => interpreter.SetShaderRegisterPacket(in packet, payload);
        table[PacketOpcode.SetUserConfigRegister] = static (interpreter, in packet, payload) => interpreter.SetUserConfigRegisterPacket(in packet, payload);
        table[PacketOpcode.SetUserConfigRegisterIndex] = static (interpreter, in packet, payload) => interpreter.SetUserConfigRegisterPacket(in packet, payload);
        table[PacketOpcode.SetContextRegisterIndirect] = static (interpreter, in packet, payload) => interpreter.SetContextRegisterTablePacket(in packet, payload);
        table[PacketOpcode.SetShaderRegisterIndirect] = static (interpreter, in packet, payload) => interpreter.SetShaderRegisterTablePacket(in packet, payload);
        table[PacketOpcode.SetUserConfigRegisterIndirect] = static (interpreter, in packet, payload) => interpreter.SetUserConfigRegisterTablePacket(in packet, payload);
        table[PacketOpcode.WriteConstantRam] = static (interpreter, in packet, payload) => interpreter.WriteConstantRamPacket(in packet, payload);
        table[PacketOpcode.DumpConstantRam] = static (interpreter, in packet, payload) => interpreter.DumpConstantRamPacket(in packet, payload);
        table[PacketOpcode.IncrementCeCounter] = static (interpreter, in packet, payload) => interpreter.IncrementCeCounterPacket(in packet, payload);
        table[PacketOpcode.IncrementDeCounter] = static (interpreter, in packet, payload) => interpreter.IncrementDeCounterPacket(in packet, payload);
        table[PacketOpcode.WaitOnCeCounter] = static (interpreter, in packet, payload) => interpreter.WaitOnCeCounterPacket(in packet, payload);
        table[PacketOpcode.WaitOnDeCounterDiff] = static (interpreter, in packet, payload) => interpreter.WaitOnDeCounterDiffPacket(in packet, payload);
        table[PacketOpcode.GetLodStats] = static (interpreter, in packet, payload) => interpreter.GetLodStatsPacket(in packet, payload);
        table[PacketOpcode.WaitRegisterMemory64] = static (interpreter, in packet, payload) => interpreter.WaitRegisterMemory64Packet(in packet, payload);
        return table;
    }

    private static PacketHandler?[] BuildCustomCodes()
    {
        var table = new PacketHandler?[PacketCustomCode.Count];
        table[PacketCustomCode.DrawIndexAuto] = static (interpreter, in packet, payload) => interpreter.WrappedDrawIndexAutoPacket(in packet, payload);
        table[PacketCustomCode.DrawReset] = static (interpreter, in packet, payload) => interpreter.DrawResetPacket(in packet, payload);
        table[PacketCustomCode.WaitFlipDone] = static (interpreter, in packet, payload) => interpreter.WaitFlipDonePacket(in packet, payload);
        table[PacketCustomCode.DispatchReset] = static (interpreter, in packet, payload) => interpreter.DispatchResetPacket(in packet, payload);
        table[PacketCustomCode.WaitMemory32] = static (interpreter, in packet, payload) => interpreter.WrappedWaitPacket(in packet, payload);
        table[PacketCustomCode.PushMarker] = static (interpreter, in packet, payload) => interpreter.PushMarkerPacket(in packet, payload);
        table[PacketCustomCode.PopMarker] = static (interpreter, in packet, payload) => interpreter.PopMarkerPacket(in packet, payload);
        table[PacketCustomCode.ShaderRegisterTable] = static (interpreter, in packet, payload) => interpreter.RegisterTableNopPacket(in packet, payload);
        table[PacketCustomCode.ContextRegisterTable] = static (interpreter, in packet, payload) => interpreter.RegisterTableNopPacket(in packet, payload);
        table[PacketCustomCode.UserConfigRegisterTable] = static (interpreter, in packet, payload) => interpreter.RegisterTableNopPacket(in packet, payload);
        table[PacketCustomCode.AcquireMemory] = static (interpreter, in packet, payload) => interpreter.AcquireMemoryPacket(in packet, payload);
        table[PacketCustomCode.WriteData] = static (interpreter, in packet, payload) => interpreter.WrappedWriteDataPacket(in packet, payload);
        table[PacketCustomCode.WaitMemory64] = static (interpreter, in packet, payload) => interpreter.WrappedWaitPacket(in packet, payload);
        table[PacketCustomCode.Flip] = static (interpreter, in packet, payload) => interpreter.FlipPacket(in packet, payload);
        table[PacketCustomCode.ReleaseMemory] = static (interpreter, in packet, payload) => interpreter.ReleaseMemoryWrappedPacket(in packet, payload);
        table[PacketCustomCode.DmaData] = static (interpreter, in packet, payload) => interpreter.WrappedDmaDataPacket(in packet, payload);
        table[PacketCustomCode.ContextState] = static (interpreter, in packet, payload) => interpreter.ContextStatePacket(in packet, payload);
        table[PacketCustomCode.IndexCount] = static (interpreter, in packet, payload) => interpreter.IndexCountPacket(in packet, payload);
        return table;
    }
}
