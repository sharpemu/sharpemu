// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Intrinsics.X86;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace SharpEmu.Core.Loader;

// Rewrite SHA extension instructions into general-register code when the host CPU lacks them.
// Rosetta 2 and some x86 hosts raise #UD for them, and a signal per instruction is far too slow
// for titles that hash whole files. The expansion keeps every register except the destination
// XMM register and keeps the flags. It runs inside the trampoline, after the red zone is skipped.
internal static class ShaInstructionRewrite
{
    // pushfq plus the saved general registers.
    private const int SavedBytes = 8 * 13;

    // Scratch layout: source 1 (and result), source 2, XMM0 (SHA256RNDS2 round inputs).
    private const int Source1 = 0;
    private const int Source2 = 16;
    private const int Xmm0Copy = 32;
    private const int ScratchBytes = 48;

    private static readonly Register[] SavedRegisters =
    {
        Register.RAX, Register.RCX, Register.RDX, Register.RBX, Register.RSI, Register.RDI,
        Register.R8, Register.R9, Register.R10, Register.R11, Register.R12, Register.R13,
    };

    internal static bool IsRequired { get; } = HostLacksSha() &&
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_SHA_REWRITE"), "1", StringComparison.Ordinal);

    // Bytes the expansion of one instruction needs at most in the trampoline.
    internal const int MaximumExpansionBytes = 1024;

    private static bool HostLacksSha()
    {
        if (!X86Base.IsSupported)
        {
            return false;
        }

        var (_, ebx, _, _) = X86Base.CpuId(7, 0);
        return (ebx & (1 << 29)) == 0;
    }

    internal static bool IsShaInstruction(in Instruction instruction) => instruction.Mnemonic is
        Mnemonic.Sha1rnds4 or Mnemonic.Sha1nexte or Mnemonic.Sha1msg1 or Mnemonic.Sha1msg2 or
        Mnemonic.Sha256rnds2 or Mnemonic.Sha256msg1 or Mnemonic.Sha256msg2;

    // A memory source with a segment prefix or an RIP-relative address cannot be rebuilt here.
    internal static bool CanRewrite(in Instruction instruction)
    {
        if (!IsShaInstruction(instruction) || instruction.Op0Kind != OpKind.Register)
        {
            return false;
        }

        return instruction.Op1Kind == OpKind.Register ||
            (instruction.Op1Kind == OpKind.Memory &&
             instruction.SegmentPrefix == Register.None &&
             !instruction.IsIPRelativeMemoryOperand);
    }

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
            ulong generatedIp = 0xFFFF_0000_0000_0000UL + ((instruction.IP & 0xFFFF_FFFF) << 12);
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
        var destination = ToXmm(instruction.Op0Register);
        a.pushfq();
        foreach (var register in SavedRegisters)
        {
            a.push(ToGpr64(register));
        }

        a.sub(rsp, ScratchBytes);
        if (instruction.Op1Kind == OpKind.Memory)
        {
            // LEA reads the guest registers before any of them change.
            a.lea(rax, RebaseMemory(instruction));
            a.mov(rcx, __qword_ptr[rax]);
            a.mov(__qword_ptr[rsp + Source2], rcx);
            a.mov(rcx, __qword_ptr[rax + 8]);
            a.mov(__qword_ptr[rsp + Source2 + 8], rcx);
        }
        else
        {
            a.movdqu(__xmmword_ptr[rsp + Source2], ToXmm(instruction.Op1Register));
        }

        a.movdqu(__xmmword_ptr[rsp + Source1], destination);
        if (instruction.Mnemonic == Mnemonic.Sha256rnds2)
        {
            a.movdqu(__xmmword_ptr[rsp + Xmm0Copy], xmm0);
        }

        switch (instruction.Mnemonic)
        {
            case Mnemonic.Sha1rnds4: EmitSha1Rnds4(a, instruction.Immediate8 & 3); break;
            case Mnemonic.Sha1nexte: EmitSha1Nexte(a); break;
            case Mnemonic.Sha1msg1: EmitSha1Msg1(a); break;
            case Mnemonic.Sha1msg2: EmitSha1Msg2(a); break;
            case Mnemonic.Sha256rnds2: EmitSha256Rnds2(a); break;
            case Mnemonic.Sha256msg1: EmitSha256Msg1(a); break;
            default: EmitSha256Msg2(a); break;
        }

        a.movdqu(destination, __xmmword_ptr[rsp + Source1]);
        a.add(rsp, ScratchBytes);
        for (var index = SavedRegisters.Length - 1; index >= 0; index--)
        {
            a.pop(ToGpr64(SavedRegisters[index]));
        }

        a.popfq();
    }

    // The guest stack pointer is above the scratch area, the saved registers and the red zone.
    private static AssemblerMemoryOperand RebaseMemory(in Instruction instruction)
    {
        var displacement = unchecked((long)instruction.MemoryDisplacement64);
        if (instruction.MemoryBase == Register.RSP)
        {
            displacement += ScratchBytes + SavedBytes + 128;
        }

        var baseRegister = instruction.MemoryBase == Register.None ? default : ToGpr64(instruction.MemoryBase);
        var memory = instruction.MemoryBase == Register.None ? __[displacement] : __[baseRegister + displacement];
        if (instruction.MemoryIndex != Register.None)
        {
            memory = instruction.MemoryBase == Register.None
                ? __[ToGpr64(instruction.MemoryIndex) * instruction.MemoryIndexScale + displacement]
                : __[baseRegister + ToGpr64(instruction.MemoryIndex) * instruction.MemoryIndexScale + displacement];
        }

        return memory;
    }

    private static AssemblerMemoryOperand Dword(int slot, int index) => __dword_ptr[rsp + slot + 4 * index];

    private static void EmitSha1Rnds4(Assembler a, int function)
    {
        var k = function switch
        {
            0 => 0x5A827999,
            1 => 0x6ED9EBA1,
            2 => unchecked((int)0x8F1BBCDC),
            _ => unchecked((int)0xCA62C1D6),
        };

        a.mov(eax, Dword(Source1, 3));
        a.mov(ebx, Dword(Source1, 2));
        a.mov(ecx, Dword(Source1, 1));
        a.mov(edx, Dword(Source1, 0));
        a.xor(esi, esi);
        for (var round = 0; round < 4; round++)
        {
            switch (function)
            {
                case 0:
                    // (b & c) ^ (~b & d) == d ^ (b & (c ^ d))
                    a.mov(edi, ecx);
                    a.xor(edi, edx);
                    a.and(edi, ebx);
                    a.xor(edi, edx);
                    break;
                case 2:
                    // (b & c) | (d & (b | c)) is the majority of b, c and d.
                    a.mov(edi, ebx);
                    a.or(edi, ecx);
                    a.and(edi, edx);
                    a.mov(r8d, ebx);
                    a.and(r8d, ecx);
                    a.or(edi, r8d);
                    break;
                default:
                    a.mov(edi, ebx);
                    a.xor(edi, ecx);
                    a.xor(edi, edx);
                    break;
            }

            a.add(edi, Dword(Source2, 3 - round));
            a.add(edi, esi);
            a.add(edi, k);
            a.mov(r8d, eax);
            a.rol(r8d, (byte)5);
            a.add(edi, r8d);
            a.mov(esi, edx);
            a.mov(edx, ecx);
            a.mov(ecx, ebx);
            a.rol(ecx, (byte)30);
            a.mov(ebx, eax);
            a.mov(eax, edi);
        }

        a.mov(Dword(Source1, 3), eax);
        a.mov(Dword(Source1, 2), ebx);
        a.mov(Dword(Source1, 1), ecx);
        a.mov(Dword(Source1, 0), edx);
    }

    private static void EmitSha1Nexte(Assembler a)
    {
        a.mov(eax, Dword(Source1, 3));
        a.rol(eax, (byte)30);
        a.add(eax, Dword(Source2, 3));
        a.mov(Dword(Source1, 3), eax);
        for (var index = 0; index < 3; index++)
        {
            a.mov(ecx, Dword(Source2, index));
            a.mov(Dword(Source1, index), ecx);
        }
    }

    private static void EmitSha1Msg1(Assembler a)
    {
        // w0..w3 from source 1 (w0 in dword 3), w4 and w5 from source 2.
        a.mov(eax, Dword(Source1, 3));
        a.mov(ebx, Dword(Source1, 2));
        a.mov(ecx, Dword(Source1, 1));
        a.mov(edx, Dword(Source1, 0));
        a.mov(esi, Dword(Source2, 3));
        a.mov(edi, Dword(Source2, 2));
        a.xor(eax, ecx);
        a.xor(ebx, edx);
        a.xor(ecx, esi);
        a.xor(edx, edi);
        a.mov(Dword(Source1, 3), eax);
        a.mov(Dword(Source1, 2), ebx);
        a.mov(Dword(Source1, 1), ecx);
        a.mov(Dword(Source1, 0), edx);
    }

    private static void EmitSha1Msg2(Assembler a)
    {
        a.mov(eax, Dword(Source1, 3));
        a.xor(eax, Dword(Source2, 2));
        a.rol(eax, (byte)1);
        a.mov(ebx, Dword(Source1, 2));
        a.xor(ebx, Dword(Source2, 1));
        a.rol(ebx, (byte)1);
        a.mov(ecx, Dword(Source1, 1));
        a.xor(ecx, Dword(Source2, 0));
        a.rol(ecx, (byte)1);
        a.mov(edx, Dword(Source1, 0));
        a.xor(edx, eax);
        a.rol(edx, (byte)1);
        a.mov(Dword(Source1, 3), eax);
        a.mov(Dword(Source1, 2), ebx);
        a.mov(Dword(Source1, 1), ecx);
        a.mov(Dword(Source1, 0), edx);
    }

    private static void EmitSha256Rnds2(Assembler a)
    {
        // a..h in eax, ebx, ecx, edx, esi, edi, r8d, r9d.
        a.mov(eax, Dword(Source2, 3));
        a.mov(ebx, Dword(Source2, 2));
        a.mov(ecx, Dword(Source1, 3));
        a.mov(edx, Dword(Source1, 2));
        a.mov(esi, Dword(Source2, 1));
        a.mov(edi, Dword(Source2, 0));
        a.mov(r8d, Dword(Source1, 1));
        a.mov(r9d, Dword(Source1, 0));
        for (var round = 0; round < 2; round++)
        {
            // r10d = h + Sigma1(e) + Ch(e, f, g) + wk
            EmitRotateXor(a, r10d, esi, r11d, 6, 11, 25);
            a.add(r10d, r9d);
            a.add(r10d, Dword(Xmm0Copy, round));
            a.mov(r11d, edi);
            a.xor(r11d, r8d);
            a.and(r11d, esi);
            a.xor(r11d, r8d);
            a.add(r10d, r11d);

            // r11d = Sigma0(a) + Maj(a, b, c)
            EmitRotateXor(a, r11d, eax, r12d, 2, 13, 22);
            a.mov(r12d, eax);
            a.or(r12d, ebx);
            a.and(r12d, ecx);
            a.mov(r13d, eax);
            a.and(r13d, ebx);
            a.or(r12d, r13d);
            a.add(r11d, r12d);

            a.mov(r9d, r8d);
            a.mov(r8d, edi);
            a.mov(edi, esi);
            a.mov(esi, edx);
            a.add(esi, r10d);
            a.mov(edx, ecx);
            a.mov(ecx, ebx);
            a.mov(ebx, eax);
            a.mov(eax, r10d);
            a.add(eax, r11d);
        }

        a.mov(Dword(Source1, 3), eax);
        a.mov(Dword(Source1, 2), ebx);
        a.mov(Dword(Source1, 1), esi);
        a.mov(Dword(Source1, 0), edi);
    }

    private static void EmitSha256Msg1(Assembler a)
    {
        // w0..w3 in edx, ecx, ebx, eax; w4 in esi. Results go to r9d..r12d.
        a.mov(eax, Dword(Source1, 3));
        a.mov(ebx, Dword(Source1, 2));
        a.mov(ecx, Dword(Source1, 1));
        a.mov(edx, Dword(Source1, 0));
        a.mov(esi, Dword(Source2, 0));
        EmitSmallSigma(a, r9d, esi, r8d, 7, 18, 3);
        a.add(r9d, eax);
        EmitSmallSigma(a, r10d, eax, r8d, 7, 18, 3);
        a.add(r10d, ebx);
        EmitSmallSigma(a, r11d, ebx, r8d, 7, 18, 3);
        a.add(r11d, ecx);
        EmitSmallSigma(a, r12d, ecx, r8d, 7, 18, 3);
        a.add(r12d, edx);
        a.mov(Dword(Source1, 3), r9d);
        a.mov(Dword(Source1, 2), r10d);
        a.mov(Dword(Source1, 1), r11d);
        a.mov(Dword(Source1, 0), r12d);
    }

    private static void EmitSha256Msg2(Assembler a)
    {
        // w16 = s1[0] + sigma1(w14); w17 = s1[1] + sigma1(w15); w18 and w19 use w16 and w17.
        a.mov(esi, Dword(Source2, 2));
        EmitSmallSigma(a, eax, esi, r8d, 17, 19, 10);
        a.add(eax, Dword(Source1, 0));
        a.mov(esi, Dword(Source2, 3));
        EmitSmallSigma(a, ebx, esi, r8d, 17, 19, 10);
        a.add(ebx, Dword(Source1, 1));
        EmitSmallSigma(a, ecx, eax, r8d, 17, 19, 10);
        a.add(ecx, Dword(Source1, 2));
        EmitSmallSigma(a, edx, ebx, r8d, 17, 19, 10);
        a.add(edx, Dword(Source1, 3));
        a.mov(Dword(Source1, 0), eax);
        a.mov(Dword(Source1, 1), ebx);
        a.mov(Dword(Source1, 2), ecx);
        a.mov(Dword(Source1, 3), edx);
    }

    // result = ror(x, r1) ^ ror(x, r2) ^ ror(x, r3)
    private static void EmitRotateXor(
        Assembler a, AssemblerRegister32 result, AssemblerRegister32 x, AssemblerRegister32 temp, int r1, int r2, int r3)
    {
        a.mov(result, x);
        a.ror(result, (byte)r1);
        a.mov(temp, x);
        a.ror(temp, (byte)r2);
        a.xor(result, temp);
        a.mov(temp, x);
        a.ror(temp, (byte)r3);
        a.xor(result, temp);
    }

    // result = ror(x, r1) ^ ror(x, r2) ^ (x >> shift)
    private static void EmitSmallSigma(
        Assembler a, AssemblerRegister32 result, AssemblerRegister32 x, AssemblerRegister32 temp, int r1, int r2, int shift)
    {
        a.mov(result, x);
        a.ror(result, (byte)r1);
        a.mov(temp, x);
        a.ror(temp, (byte)r2);
        a.xor(result, temp);
        a.mov(temp, x);
        a.shr(temp, (byte)shift);
        a.xor(result, temp);
    }

    private static AssemblerRegisterXMM ToXmm(Register register) => new(register);

    private static AssemblerRegister64 ToGpr64(Register register) => new(register.GetFullRegister());
}
