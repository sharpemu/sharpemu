// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;

namespace SharpEmu.GnmMode.Libraries.Gnm;

public static partial class GnmDriverExports
{
    public static int IsUserPaEnabled(CpuContext context) => context.SetReturn(0);

    public static int AreSubmitsAllowed(CpuContext context) => context.SetReturn(1);

    public static int InsertWaitFlipDone(CpuContext context)
    {
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 7 ||
            (uint)context[CpuRegister.Rcx] >= 16)
            return context.SetReturn(-1);
        Span<uint> commands = stackalloc uint[7];
        commands.Clear();
        commands[0] = PacketHeader.Make(7, PacketOpcode.Nop, PacketCustomCode.WaitFlipDone);
        commands[1] = (uint)context[CpuRegister.Rdx];
        commands[2] = (uint)context[CpuRegister.Rcx];
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }

    public static int DispatchInitDefaultHardwareState(CpuContext context) => WriteDefaultState(context, compute: true);
    public static int DrawInitDefaultHardwareState200(CpuContext context) => WriteDefaultState(context, compute: false);
    public static int DrawInitDefaultHardwareState350(CpuContext context) => WriteDefaultState(context, compute: false);
    public static int DrawInitToDefaultContextState400(CpuContext context) => (uint)context[CpuRegister.Rsi] == 0x100
        ? WriteDefaultState(context, compute: false, contextOnly: true) : context.SetReturn(0);

    private static int WriteDefaultState(CpuContext context, bool compute, bool contextOnly = false)
    {
        const uint dwords = 0x100;
        if ((uint)context[CpuRegister.Rsi] < dwords || context[CpuRegister.Rdi] == 0)
            return context.SetReturn(0);
        Span<byte> commands = stackalloc byte[(int)dwords * sizeof(uint)];
        commands.Clear();
        // Use the same queue defaults as AGC instead of maintaining a second hardware state table.
        if (contextOnly)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(commands, PacketHeader.Make(2, PacketOpcode.ClearState));
            BinaryPrimitives.WriteUInt32LittleEndian(commands[8..], PacketHeader.Make(2, PacketOpcode.NumInstances));
            BinaryPrimitives.WriteUInt32LittleEndian(commands[12..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(commands[16..], PacketHeader.Make(dwords - 4, PacketOpcode.Nop));
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(commands,
                PacketHeader.Make(2, PacketOpcode.Nop, compute ? PacketCustomCode.DispatchReset : PacketCustomCode.DrawReset));
            BinaryPrimitives.WriteUInt32LittleEndian(commands[8..], PacketHeader.Make(dwords - 2, PacketOpcode.Nop));
        }
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], commands) ? (int)dwords : 0);
    }

    public static int SetCsShader(CpuContext context) => SetCsShader(context, 0);

    public static int SetCsShaderWithModifier(CpuContext context) => SetCsShader(context, (uint)context[CpuRegister.Rcx]);

    private static int SetCsShader(CpuContext context, uint modifier)
    {
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] < 25 || context[CpuRegister.Rdx] == 0 ||
            (modifier & 0xFFFFFC3F) != 0)
            return context.SetReturn(-1);
        Span<byte> registers = stackalloc byte[7 * sizeof(uint)];
        if (!KernelMemoryCompatExports.TryReadGuestStructure(context, context[CpuRegister.Rdx], registers) ||
            BinaryPrimitives.ReadUInt32LittleEndian(registers[4..]) != 0)
            return context.SetReturn(-1);
        if (modifier != 0)
            BinaryPrimitives.WriteUInt32LittleEndian(registers[8..],
                (BinaryPrimitives.ReadUInt32LittleEndian(registers[8..]) & 0xFFFFFC3F) | modifier);
        Span<byte> commands = stackalloc byte[25 * sizeof(uint)];
        commands.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(commands, PacketHeader.Make(4, PacketOpcode.SetShaderRegister));
        BinaryPrimitives.WriteUInt32LittleEndian(commands[4..], ShaderRegisterOffset.ComputePgmLo);
        registers[..8].CopyTo(commands[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(commands[16..], PacketHeader.Make(4, PacketOpcode.SetShaderRegister));
        BinaryPrimitives.WriteUInt32LittleEndian(commands[20..], ShaderRegisterOffset.ComputePgmRsrc1);
        registers.Slice(8, 8).CopyTo(commands[24..]);
        BinaryPrimitives.WriteUInt32LittleEndian(commands[32..], PacketHeader.Make(5, PacketOpcode.SetShaderRegister));
        BinaryPrimitives.WriteUInt32LittleEndian(commands[36..], ShaderRegisterOffset.ComputeNumThreadX);
        registers[16..].CopyTo(commands[40..]);
        BinaryPrimitives.WriteUInt32LittleEndian(commands[52..], PacketHeader.Make(12, PacketOpcode.Nop));
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], commands) ? 0 : -1);
    }

    public static int DispatchDirect(CpuContext context)
    {
        var x = (uint)context[CpuRegister.Rdx];
        var y = (uint)context[CpuRegister.Rcx];
        var z = (uint)context[CpuRegister.R8];
        var flags = (uint)context[CpuRegister.R9];
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 9 || ((x | y | z) & 0x80000000) != 0)
            return context.SetReturn(-1);
        Span<uint> commands = stackalloc uint[9];
        commands.Clear();
        commands[0] = PacketHeader.Make(5, PacketOpcode.DispatchDirect) | 2 | (flags & 1);
        commands[1] = x;
        commands[2] = y;
        commands[3] = z;
        commands[4] = (flags & 0x18) | 1;
        commands[5] = PacketHeader.Make(4, PacketOpcode.Nop);
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }

    public static int DispatchIndirect(CpuContext context)
    {
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 7)
            return context.SetReturn(-1);
        var flags = (uint)context[CpuRegister.Rcx];
        Span<uint> commands = stackalloc uint[7];
        commands.Clear();
        commands[0] = PacketHeader.Make(3, PacketOpcode.DispatchIndirect) | 2 | (flags & 1);
        commands[1] = (uint)context[CpuRegister.Rdx];
        commands[2] = (flags & 0x18) | 1;
        commands[3] = PacketHeader.Make(4, PacketOpcode.Nop);
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }

    public static int DrawIndirect(CpuContext context)
    {
        var stage = (uint)context[CpuRegister.Rcx];
        var vertexOffset = (uint)context[CpuRegister.R8];
        var instanceOffset = (uint)context[CpuRegister.R9];
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 9 || stage >= 7 ||
            vertexOffset >= 16 || instanceOffset >= 16 ||
            !context.TryReadUInt32(context[CpuRegister.Rsp] + 8, out var flags))
            return context.SetReturn(-1);
        var scalarBase = stage switch { 2 => 0x4Cu, 4 => 0xCCu, 6 => 0x14Cu, _ => 0u };
        Span<uint> commands = stackalloc uint[9];
        commands.Clear();
        commands[0] = PacketHeader.Make(5, PacketOpcode.DrawIndirect) | (flags & 1);
        commands[1] = (uint)context[CpuRegister.Rdx];
        commands[2] = vertexOffset == 0 ? 0 : scalarBase + vertexOffset;
        commands[3] = instanceOffset == 0 ? 0 : scalarBase + instanceOffset;
        commands[4] = 2;
        commands[5] = PacketHeader.Make(4, PacketOpcode.Nop);
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }

    public static int DrawIndexAuto(CpuContext context)
    {
        var flags = (uint)context[CpuRegister.Rcx];
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 7 || (flags & 0x1FFFFFFE) != 0)
            return context.SetReturn(-1);
        Span<uint> commands = stackalloc uint[7];
        commands.Clear();
        commands[0] = PacketHeader.Make(3, PacketOpcode.DrawIndexAuto);
        commands[1] = (uint)context[CpuRegister.Rdx];
        commands[2] = 2;
        commands[3] = PacketHeader.Make(4, PacketOpcode.Nop);
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }

    public static int DrawIndexOffset(CpuContext context)
    {
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 9)
            return context.SetReturn(-1);
        var count = (uint)context[CpuRegister.Rcx];
        Span<uint> commands = stackalloc uint[9];
        commands.Clear();
        commands[0] = PacketHeader.Make(5, PacketOpcode.DrawIndexOffset2) | ((uint)context[CpuRegister.R8] & 1);
        commands[1] = count;
        commands[2] = (uint)context[CpuRegister.Rdx];
        commands[3] = count;
        commands[5] = PacketHeader.Make(4, PacketOpcode.Nop);
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }

    public static int DrawIndex(CpuContext context)
    {
        var address = context[CpuRegister.Rcx];
        var flags = (uint)context[CpuRegister.R8];
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 10 || address == 0 ||
            (address & 1) != 0 || (flags & 0x1FFFFFFE) != 0)
            return context.SetReturn(-1);
        var count = (uint)context[CpuRegister.Rdx];
        Span<uint> commands = stackalloc uint[10];
        commands.Clear();
        commands[0] = PacketHeader.Make(6, PacketOpcode.DrawIndex2);
        commands[1] = count;
        commands[2] = (uint)address;
        commands[3] = (uint)(address >> 32);
        commands[4] = count;
        commands[6] = PacketHeader.Make(4, PacketOpcode.Nop);
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }

    public static int SetVgtControl(CpuContext context)
    {
        var groupSizeMinusOne = (uint)context[CpuRegister.Rdx];
        var partialWave = (uint)context[CpuRegister.Rcx];
        var switchOnEop = (uint)context[CpuRegister.R8];
        if (context[CpuRegister.Rdi] == 0 || (uint)context[CpuRegister.Rsi] != 3 || groupSizeMinusOne >= 0x100 ||
            (partialWave | switchOnEop) >= 2)
            return context.SetReturn(-1);
        Span<uint> commands = [PacketHeader.Make(3, PacketOpcode.SetContextRegister), 0x2AA, groupSizeMinusOne | (partialWave << 16)];
        return context.SetReturn(context.Memory.TryWrite(context[CpuRegister.Rdi], MemoryMarshal.AsBytes(commands)) ? 0 : -1);
    }
}
