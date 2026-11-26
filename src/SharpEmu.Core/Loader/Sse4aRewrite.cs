// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Intrinsics.X86;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Loader;

// Avoid EXTRQ and INSERTQ faults on hosts without SSE4a. Keep the fault-time recovery result.
// Run the expansion inside the trampoline, below the guest red zone.
internal static class Sse4aRewrite
{
    private static readonly Register[] SavedRegisters = { Register.RAX, Register.RCX, Register.RDX };

    internal static bool IsRequired { get; } = HostLacksSse4a() &&
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_EXTRQ_REWRITE"), "1", StringComparison.Ordinal);

    // Bytes the expansion of one instruction needs at most in the trampoline.
    internal const int MaximumExpansionBytes = 128;

    private static bool HostLacksSse4a()
    {
        if (!X86Base.IsSupported)
        {
            return false;
        }

        var (_, _, ecx, _) = X86Base.CpuId(unchecked((int)0x8000_0001), 0);
        return (ecx & (1 << 6)) == 0;
    }

    // The forms the fault-time recovery takes.
    internal static bool CanRewrite(in Instruction instruction) =>
        instruction.Code is Code.Extrq_xmm_xmm or Code.Extrq_xmm_imm8_imm8 or Code.Insertq_xmm_xmm_imm8_imm8;

    internal static IList<Instruction> Expand(IList<Instruction> instructions)
    {
        var expanded = new List<Instruction>(instructions.Count);
        foreach (var instruction in instructions)
        {
            if (!CanRewrite(instruction))
            {
                expanded.Add(instruction);
                continue;
            }

            var assembler = new Assembler(64);
            EmitExpansion(assembler, instruction);
            var first = true;
            ulong generatedIp = 0xFFFD_0000_0000_0000UL + ((instruction.IP & 0xFFFF_FFFF) << 12);
            foreach (var generated in assembler.Instructions)
            {
                var copy = generated;
                // Keep branch targets at the first generated instruction.
                copy.IP = first ? instruction.IP : generatedIp++;
                first = false;
                expanded.Add(copy);
            }
        }

        return expanded;
    }

    internal static void EmitExpansion(Assembler a, in Instruction instruction)
    {
        a.pushfq();
        foreach (var register in SavedRegisters)
        {
            a.push(new AssemblerRegister64(register));
        }

        if (instruction.Code == Code.Insertq_xmm_xmm_imm8_imm8)
        {
            EmitInsert(a, instruction);
        }
        else
        {
            EmitExtract(a, instruction);
        }

        for (var index = SavedRegisters.Length - 1; index >= 0; index--)
        {
            a.pop(new AssemblerRegister64(SavedRegisters[index]));
        }

        a.popfq();
    }

    // Matches the fault-time recovery and AMD hardware: (low >> index) masked to length bits,
    // length 0 means 64, and bits 127:64 become zero. Legacy MOVQ keeps bits 255:128.
    private static void EmitExtract(Assembler a, in Instruction instruction)
    {
        var destination = new AssemblerRegisterXMM(instruction.Op0Register);
        if (instruction.Code == Code.Extrq_xmm_xmm)
        {
            // Read the control first; it can be the destination register.
            a.movq(rcx, new AssemblerRegisterXMM(instruction.Op1Register));
            a.mov(edx, ecx);
            a.and(edx, 0x3F);
            a.shr(ecx, 8);
            a.and(ecx, 0x3F);
        }
        else
        {
            a.mov(edx, (int)(instruction.GetImmediate(1) & 0x3F));
            a.mov(ecx, (int)(instruction.GetImmediate(2) & 0x3F));
        }

        a.movq(rax, destination);
        a.shr(rax, cl);
        // A count of 64 - length; length 0 gives 64, which the shifts mask to 0 and keep all bits.
        a.mov(ecx, 64);
        a.sub(ecx, edx);
        a.shl(rax, cl);
        a.shr(rax, cl);
        a.movq(destination, rax);
    }

    // Matches the fault-time recovery (Sse4aBitFieldEmulator.InsertBitField): the low length bits of the
    // source replace the destination's bits from index up, length 0 means 64, a field past bit 63 stops there,
    // and bits 127:64 become zero. Length and index are immediates, so both masks are constants.
    private static void EmitInsert(Assembler a, in Instruction instruction)
    {
        var destination = new AssemblerRegisterXMM(instruction.Op0Register);
        var length = (int)(instruction.GetImmediate(2) & 0x3F);
        var index = (int)(instruction.GetImmediate(3) & 0x3F);
        var fieldLength = Math.Min(length == 0 ? 64 : length, 64 - index);
        var fieldMask = fieldLength == 64 ? ulong.MaxValue : (1UL << fieldLength) - 1;

        // Read the source first; it can be the destination register.
        a.movq(rcx, new AssemblerRegisterXMM(instruction.Op1Register));
        a.mov(rdx, fieldMask);
        a.and(rcx, rdx);
        a.shl(rcx, (byte)index);
        a.movq(rax, destination);
        a.mov(rdx, ~(fieldMask << index));
        a.and(rax, rdx);
        a.or(rax, rcx);
        a.movq(destination, rax);
    }
}
