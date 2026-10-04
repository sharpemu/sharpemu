// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Iced.Intel;
using SharpEmu.Core.Cpu.Emulation;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed unsafe class Sse4aExtractRewriteTests
{
    // Buffer layout in qwords: 0-3 destination YMM, 4-7 control YMM, 8-11 unrelated YMM,
    // 12 RAX, 13 RDX, then outputs from 16: destination, control, unrelated, RAX, RDX, RCX, flags before, flags after.
    private const int OutputQword = 16;

    private static readonly ulong[] Values =
    [
        0, ulong.MaxValue, 0x8000_0000_0000_0001, 0xA00E_8965_2C7A_6E65, 0x0123_4567_89AB_CDEF,
    ];

    [Fact]
    public void RegisterFormMatchesRecoveryForEveryControl()
    {
        if (!IsSupportedHost) return;
        using var harness = new Harness(Instruction.Create(Code.Extrq_xmm_xmm, Register.XMM2, Register.XMM5));
        foreach (var value in Values)
        for (var length = 0; length < 64; length++)
        for (var index = 0; index < 64; index++)
        {
            // Bits outside the control fields must not matter.
            var control = 0xFEDC_BA98_7654_C0C0UL | (uint)length | ((ulong)index << 8);
            harness.Run(value, control, aliased: false);
            harness.AssertResult(Sse4aBitFieldEmulator.ExtractBitField(value, length, index), $"value=0x{value:X16} length={length} index={index}");
        }
    }

    [Fact]
    public void RegisterFormReadsTheControlBeforeWritingAnAliasedDestination()
    {
        if (!IsSupportedHost) return;
        using var harness = new Harness(Instruction.Create(Code.Extrq_xmm_xmm, Register.XMM2, Register.XMM2));
        foreach (var value in Values)
        {
            harness.Run(value, 0, aliased: true);
            var length = (int)(value & 0x3F);
            var index = (int)((value >> 8) & 0x3F);
            harness.AssertResult(Sse4aBitFieldEmulator.ExtractBitField(value, length, index), $"aliased value=0x{value:X16}");
        }
    }

    [Fact]
    public void ImmediateFormMatchesRecoveryForEveryEncoding()
    {
        if (!IsSupportedHost) return;
        for (var length = 0; length < 64; length++)
        for (var index = 0; index < 64; index++)
        {
            using var harness = new Harness(Instruction.Create(Code.Extrq_xmm_imm8_imm8, Register.XMM2, length, index));
            const ulong value = 0xA00E_8965_2C7A_6E65;
            harness.Run(value, 0x1234, aliased: false);
            harness.AssertResult(Sse4aBitFieldEmulator.ExtractBitField(value, length, index), $"immediate length={length} index={index}");
        }
    }

    private static bool IsSupportedHost => Avx.IsSupported;

    private sealed class Harness : IDisposable
    {
        private readonly nint _code;
        private readonly ulong* _buffer;
        private ulong[] _input = new ulong[16];
        private bool _aliased;

        public Harness(Instruction extract)
        {
            Assert.True(Sse4aExtractRewrite.CanRewrite(extract));
            MemoryOperand Slot(int qword) => new(Register.RCX, qword * 8);
            var instructions = new List<Instruction>
            {
                Instruction.Create(Code.Mov_r64_rm64, Register.RCX,
                    OperatingSystem.IsWindows() ? Register.RCX : Register.RDI),
                Instruction.Create(Code.VEX_Vmovdqu_ymm_ymmm256, Register.YMM2, Slot(0)),
                Instruction.Create(Code.VEX_Vmovdqu_ymm_ymmm256, Register.YMM5, Slot(4)),
                Instruction.Create(Code.VEX_Vmovdqu_ymm_ymmm256, Register.YMM3, Slot(8)),
                Instruction.Create(Code.Mov_r64_rm64, Register.RAX, Slot(12)),
                Instruction.Create(Code.Mov_r64_rm64, Register.RDX, Slot(13)),
                Instruction.Create(Code.Cmp_rm64_r64, Register.RAX, Register.RDX),
                Instruction.Create(Code.Pushfq),
                Instruction.Create(Code.Pop_r64, Register.R9),
            };
            extract.IP = 0x1000;
            instructions.AddRange(Sse4aExtractRewrite.Expand([extract]));
            instructions.AddRange(
            [
                Instruction.Create(Code.Pushfq),
                Instruction.Create(Code.Pop_r64, Register.R10),
                Instruction.Create(Code.VEX_Vmovdqu_ymmm256_ymm, Slot(OutputQword), Register.YMM2),
                Instruction.Create(Code.VEX_Vmovdqu_ymmm256_ymm, Slot(OutputQword + 4), Register.YMM5),
                Instruction.Create(Code.VEX_Vmovdqu_ymmm256_ymm, Slot(OutputQword + 8), Register.YMM3),
                Instruction.Create(Code.Mov_rm64_r64, Slot(OutputQword + 12), Register.RAX),
                Instruction.Create(Code.Mov_rm64_r64, Slot(OutputQword + 13), Register.RDX),
                Instruction.Create(Code.Mov_rm64_r64, Slot(OutputQword + 14), Register.RCX),
                Instruction.Create(Code.Mov_rm64_r64, Slot(OutputQword + 15), Register.R9),
                Instruction.Create(Code.Mov_rm64_r64, Slot(OutputQword + 16), Register.R10),
                Instruction.Create(Code.VEX_Vzeroupper),
                Instruction.Create(Code.Retnq),
            ]);
            for (var i = 0; i < instructions.Count; i++)
            {
                var copy = instructions[i];
                if (copy.IP == 0) copy.IP = 0x10_0000UL + (ulong)i;
                instructions[i] = copy;
            }

            _code = (nint)HostMemory.Alloc(null, 0x1000,
                HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_EXECUTE_READWRITE);
            Assert.NotEqual(0, _code);
            var writer = new BufferWriter();
            Assert.True(BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, (ulong)_code),
                out var error, out _), error);
            Marshal.Copy(writer.Bytes.ToArray(), 0, _code, writer.Bytes.Count);
            _buffer = (ulong*)NativeMemory.AlignedAlloc(64 * 8, 64);
        }

        public void Run(ulong value, ulong control, bool aliased)
        {
            _aliased = aliased;
            _input = [value, 0x1111_2222_3333_4444, 0x5555_6666_7777_8888, 0x9999_AAAA_BBBB_CCCC,
                control, 0xDDDD_EEEE_FFFF_0000, 0x0F0F_0F0F_0F0F_0F0F, 0xF0F0_F0F0_F0F0_F0F0,
                0xCAFE_BABE_0000_0001, 0xCAFE_BABE_0000_0002, 0xCAFE_BABE_0000_0003, 0xCAFE_BABE_0000_0004,
                0x0102_0304_0506_0708, 0x0102_0304_0506_0709, 0, 0];
            for (var i = 0; i < 64; i++) _buffer[i] = 0;
            for (var i = 0; i < _input.Length; i++) _buffer[i] = _input[i];
            ((delegate* unmanaged<ulong*, void>)_code)(_buffer);
        }

        public void AssertResult(ulong expected, string label)
        {
            var output = _buffer + OutputQword;
            Assert.True(expected == output[0], $"{label}: expected 0x{expected:X16} actual 0x{output[0]:X16}");
            Assert.True(output[1] == 0, $"{label}: bits 127:64 not zero");
            Assert.True(output[2] == _input[2] && output[3] == _input[3], $"{label}: destination bits 255:128 changed");
            if (!_aliased)
            {
                for (var i = 0; i < 4; i++)
                    Assert.True(output[4 + i] == _input[4 + i], $"{label}: control register changed");
            }

            for (var i = 0; i < 4; i++)
                Assert.True(output[8 + i] == _input[8 + i], $"{label}: unrelated register changed");
            Assert.True(output[12] == _input[12] && output[13] == _input[13], $"{label}: RAX or RDX changed");
            Assert.True(output[14] == (ulong)_buffer, $"{label}: RCX changed");
            Assert.True(output[15] == output[16], $"{label}: flags changed");
        }

        public void Dispose()
        {
            HostMemory.Free((void*)_code, 0, HostMemory.MEM_RELEASE);
            NativeMemory.AlignedFree(_buffer);
        }
    }

    private sealed class BufferWriter : CodeWriter
    {
        public List<byte> Bytes { get; } = new();

        public override void WriteByte(byte value) => Bytes.Add(value);
    }

}
