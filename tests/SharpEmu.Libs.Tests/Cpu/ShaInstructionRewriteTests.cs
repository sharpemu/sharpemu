// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// The rewritten code must run on hosts without SHA, so these tests execute it with a small
// interpreter for the few instructions the expansion uses, and compare the result with
// ShaInstructionEmulator. They also check that nothing else in the guest state changes.
public sealed class ShaInstructionRewriteTests
{
    private const ulong StackTop = 0x10000;
    private const ulong DataAddress = 0x20000;

    public static TheoryData<Code, bool> Cases()
    {
        var data = new TheoryData<Code, bool>();
        foreach (var code in new[]
                 {
                     Code.Sha1rnds4_xmm_xmmm128_imm8, Code.Sha1nexte_xmm_xmmm128, Code.Sha1msg1_xmm_xmmm128,
                     Code.Sha1msg2_xmm_xmmm128, Code.Sha256rnds2_xmm_xmmm128, Code.Sha256msg1_xmm_xmmm128,
                     Code.Sha256msg2_xmm_xmmm128,
                 })
        {
            data.Add(code, false);
            data.Add(code, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Expansion_MatchesTheInstructionAndKeepsOtherState(Code code, bool memorySource)
    {
        var random = new System.Random((int)code * 2 + (memorySource ? 1 : 0));
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var machine = new Machine(random);
            var function = iteration & 3;
            var instruction = CreateInstruction(code, memorySource, function, machine);
            var expected = Evaluate(code, function, machine.Xmm[3], memorySource ? machine.ReadMemory128(DataAddress + 0x40) : machine.Xmm[7], machine.Xmm[0]);
            var before = machine.Clone();

            var program = new List<Instruction> { Instruction.Create(Code.Lea_r64_m, Register.RSP, new MemoryOperand(Register.RSP, -128)) };
            program.AddRange(ShaInstructionRewrite.Expand(new[] { instruction }));
            program.Add(Instruction.Create(Code.Lea_r64_m, Register.RSP, new MemoryOperand(Register.RSP, 128)));
            foreach (var step in program)
            {
                machine.Execute(step);
            }

            Assert.Equal(expected, machine.Xmm[3]);
            machine.Xmm[3] = before.Xmm[3];
            Assert.Equal(before.Gpr, machine.Gpr);
            Assert.Equal(before.Xmm, machine.Xmm);
            Assert.Equal(before.Flags, machine.Flags);
            // The 128-byte red zone below the guest stack pointer keeps its bytes.
            Assert.Equal(before.Stack.AsSpan((int)(StackTop - 0x1000 - 128), 128).ToArray(),
                machine.Stack.AsSpan((int)(StackTop - 0x1000 - 128), 128).ToArray());
        }
    }

    [Fact]
    public void Expand_LeavesOtherInstructionsUnchanged()
    {
        var other = Instruction.Create(Code.Add_r32_rm32, Register.EAX, Register.ECX);
        var expanded = ShaInstructionRewrite.Expand(new[] { other });

        Assert.Equal(new[] { other }, expanded);
    }

    private static Instruction CreateInstruction(Code code, bool memorySource, int function, Machine machine)
    {
        Instruction instruction;
        if (memorySource)
        {
            // Address the source through a base and a scaled index register.
            machine.Gpr[(int)(Register.RSI - Register.RAX)] = DataAddress;
            machine.Gpr[(int)(Register.RDI - Register.RAX)] = 4;
            var memory = new MemoryOperand(Register.RSI, Register.RDI, 8, 0x20, 1);
            instruction = code == Code.Sha1rnds4_xmm_xmmm128_imm8
                ? Instruction.Create(code, Register.XMM3, memory, function)
                : Instruction.Create(code, Register.XMM3, memory);
        }
        else
        {
            instruction = code == Code.Sha1rnds4_xmm_xmmm128_imm8
                ? Instruction.Create(code, Register.XMM3, Register.XMM7, function)
                : Instruction.Create(code, Register.XMM3, Register.XMM7);
        }

        instruction.IP = 0x1000;
        return instruction;
    }

    private static UInt128 Evaluate(Code code, int function, UInt128 source1, UInt128 source2, UInt128 xmm0) => code switch
    {
        Code.Sha1rnds4_xmm_xmmm128_imm8 => ShaInstructionEmulator.Sha1Rnds4(source1, source2, function),
        Code.Sha1nexte_xmm_xmmm128 => ShaInstructionEmulator.Sha1Nexte(source1, source2),
        Code.Sha1msg1_xmm_xmmm128 => ShaInstructionEmulator.Sha1Msg1(source1, source2),
        Code.Sha1msg2_xmm_xmmm128 => ShaInstructionEmulator.Sha1Msg2(source1, source2),
        Code.Sha256rnds2_xmm_xmmm128 => ShaInstructionEmulator.Sha256Rnds2(source1, source2, xmm0),
        Code.Sha256msg1_xmm_xmmm128 => ShaInstructionEmulator.Sha256Msg1(source1, source2),
        _ => ShaInstructionEmulator.Sha256Msg2(source1, source2),
    };

    // Interprets the subset of x86-64 the expansion emits. Flags are modelled only as the value
    // PUSHFQ and POPFQ save and restore; the expansion must not leak any other change.
    private sealed class Machine
    {
        public ulong[] Gpr = new ulong[16];
        public UInt128[] Xmm = new UInt128[16];
        public ulong Flags;
        public byte[] Stack = new byte[StackTop];
        public byte[] Data = new byte[0x100];

        public Machine(System.Random random)
        {
            for (var index = 0; index < 16; index++)
            {
                Gpr[index] = (ulong)random.NextInt64();
                Xmm[index] = new UInt128((ulong)random.NextInt64(), (ulong)random.NextInt64());
            }

            Gpr[(int)(Register.RSP - Register.RAX)] = StackTop - 0x1000;
            Flags = 0x246;
            random.NextBytes(Stack);
            random.NextBytes(Data);
        }

        private Machine()
        {
        }

        public Machine Clone() => new()
        {
            Gpr = (ulong[])Gpr.Clone(),
            Xmm = (UInt128[])Xmm.Clone(),
            Flags = Flags,
            Stack = (byte[])Stack.Clone(),
            Data = (byte[])Data.Clone(),
        };

        public UInt128 ReadMemory128(ulong address) => BinaryPrimitives.ReadUInt128LittleEndian(Span(address, 16));

        public void Execute(in Instruction instruction)
        {
            switch (instruction.Mnemonic)
            {
                case Mnemonic.Pushfq:
                    Push(Flags);
                    break;
                case Mnemonic.Popfq:
                    Flags = Pop();
                    break;
                case Mnemonic.Push:
                    Push(Gpr[GprIndex(instruction.Op0Register)]);
                    break;
                case Mnemonic.Pop:
                    Gpr[GprIndex(instruction.Op0Register)] = Pop();
                    break;
                case Mnemonic.Lea:
                    Gpr[GprIndex(instruction.Op0Register)] = Address(instruction);
                    break;
                case Mnemonic.Movdqu:
                    if (instruction.Op0Kind == OpKind.Memory)
                    {
                        BinaryPrimitives.WriteUInt128LittleEndian(Span(Address(instruction), 16), Xmm[instruction.Op1Register - Register.XMM0]);
                    }
                    else
                    {
                        Xmm[instruction.Op0Register - Register.XMM0] = ReadMemory128(Address(instruction));
                    }

                    break;
                case Mnemonic.Mov:
                    Write(instruction, 0, Read(instruction, 1));
                    break;
                case Mnemonic.Add:
                case Mnemonic.Sub:
                case Mnemonic.Xor:
                case Mnemonic.And:
                case Mnemonic.Or:
                case Mnemonic.Rol:
                case Mnemonic.Ror:
                case Mnemonic.Shr:
                    Write(instruction, 0, Alu(instruction, Read(instruction, 0), Read(instruction, 1)));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected instruction {instruction}");
            }
        }

        private static ulong Alu(in Instruction instruction, ulong left, ulong right)
        {
            var is32 = instruction.Op0Kind == OpKind.Register && instruction.Op0Register.IsGPR32();
            ulong result = instruction.Mnemonic switch
            {
                Mnemonic.Add => left + right,
                Mnemonic.Sub => left - right,
                Mnemonic.Xor => left ^ right,
                Mnemonic.And => left & right,
                Mnemonic.Or => left | right,
                Mnemonic.Rol => System.Numerics.BitOperations.RotateLeft((uint)left, (int)right),
                Mnemonic.Ror => System.Numerics.BitOperations.RotateRight((uint)left, (int)right),
                _ => (uint)left >> (int)right,
            };
            return is32 ? (uint)result : result;
        }

        private ulong Read(in Instruction instruction, int operand)
        {
            switch (instruction.GetOpKind(operand))
            {
                case OpKind.Register:
                    var register = instruction.GetOpRegister(operand);
                    var value = Gpr[GprIndex(register)];
                    return register.IsGPR32() ? (uint)value : value;
                case OpKind.Memory:
                    return instruction.MemorySize == MemorySize.UInt32 || instruction.MemorySize == MemorySize.Int32
                        ? BinaryPrimitives.ReadUInt32LittleEndian(Span(Address(instruction), 4))
                        : BinaryPrimitives.ReadUInt64LittleEndian(Span(Address(instruction), 8));
                default:
                    return instruction.GetImmediate(operand);
            }
        }

        private void Write(in Instruction instruction, int operand, ulong value)
        {
            if (instruction.GetOpKind(operand) == OpKind.Register)
            {
                var register = instruction.GetOpRegister(operand);
                // A 32-bit write clears the upper half, as on x86-64.
                Gpr[GprIndex(register)] = register.IsGPR32() ? (uint)value : value;
                return;
            }

            if (instruction.MemorySize == MemorySize.UInt32 || instruction.MemorySize == MemorySize.Int32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Span(Address(instruction), 4), (uint)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt64LittleEndian(Span(Address(instruction), 8), value);
            }
        }

        private ulong Address(in Instruction instruction)
        {
            var address = instruction.MemoryDisplacement64;
            if (instruction.MemoryBase != Register.None)
            {
                address += Gpr[GprIndex(instruction.MemoryBase)];
            }

            if (instruction.MemoryIndex != Register.None)
            {
                address += Gpr[GprIndex(instruction.MemoryIndex)] * (ulong)instruction.MemoryIndexScale;
            }

            return address;
        }

        private Span<byte> Span(ulong address, int length) => address >= DataAddress
            ? Data.AsSpan((int)(address - DataAddress), length)
            : Stack.AsSpan((int)address, length);

        private void Push(ulong value)
        {
            Gpr[4] -= 8;
            BinaryPrimitives.WriteUInt64LittleEndian(Span(Gpr[4], 8), value);
        }

        private ulong Pop()
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(Span(Gpr[4], 8));
            Gpr[4] += 8;
            return value;
        }

        private static int GprIndex(Register register) => register.GetFullRegister() - Register.RAX;
    }
}
