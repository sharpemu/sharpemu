// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcFusedHullEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedLocalProgram_KeepsBothHullBindingsAndOriginalInstructionAddresses(bool legacy)
    {
        var memory = new BindingMemory();
        var context = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
        Header(0x1100, 5, 0x2000, 8, 0, 0);
        Header(0x1200, 7, 0x3000, 12, 0x1500, 2);
        Header(0x1300, 7, 0x4000, 12, 0x1600, 2);
        Words(0x2000, 0xBF800000, 0xBE802000);
        Words(0x3000, 0xBE9403FF, 0x11111111, 0xBF810000);
        Words(0x4000, 0xBE9403FF, 0x22222222, 0xBF810000);
        Words(0x1500, 0x148, 0, 0x149, 0);
        Words(0x1600, 0x148, 0, 0x149, 0);

        var first = Fuse(0x1200);
        var second = Fuse(0x1300);
        Assert.NotEqual(first, second);
        Assert.Equal(first, Fuse(0x1200));
        Assert.Equal(2, memory.Allocations);
        Check(first, 0x3000, 0x11111111);
        Check(second, 0x4000, 0x22222222);

        // Reusing the original code address must not return an obsolete copy.
        Words(0x2000, 0xBF800001, 0xBE802000);
        var changed = Fuse(0x1200);
        Assert.NotEqual(first, changed);
        Check(first, 0x3000, 0x11111111);

        ulong Fuse(ulong back)
        {
            context[CpuRegister.Rdi] = 0x1700;
            context[CpuRegister.Rsi] = 0x1100;
            context[CpuRegister.Rdx] = back;
            context[CpuRegister.Rcx] = 0x1800;
            Assert.Equal(0, legacy ? AgcExports.FuseShaderHalvesLegacy(context) : AgcExports.FuseShaderHalves(context));
            return ((ulong)Read(0x1804) << 8) | ((ulong)(Read(0x180C) & 255) << 40);
        }
        void Check(ulong binding, ulong back, uint marker)
        {
            var renderContext = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
            Assert.True(Gen5ShaderTranslator.TryGetFusedProgramParts(renderContext, binding, out var continuation, out _));
            Assert.Equal(back, continuation);
            Assert.True(Gen5ShaderTranslator.TryDecodeProgram(renderContext, binding, out var program, out var error), error);
            Assert.Equal(0x2000ul, unchecked(program.Address + program.Instructions[0].ProgramOffset));
            var hull = program.Instructions.First(i => i.Pc == program.FusedContinuationPc);
            Assert.Equal(back, unchecked(program.Address + hull.ProgramOffset));
            Assert.Equal(marker, hull.Words[1]);
            var registered = AgcExports.CreateShaderHeaderRegistry(renderContext).Require(binding, "merged-hull");
            Assert.Equal(binding, registered.CodeAddress);
            Assert.Equal(back, registered.ContinuationAddress);
        }
        void Header(ulong address, byte type, ulong code, uint size, ulong registers, byte count)
        {
            U64(address + 0x10, code); U64(address + 0x20, registers);
            Words(address + 0x44, size);
            Assert.True(memory.TryWrite(address + 0x5A, [type]));
            Assert.True(memory.TryWrite(address + 0x5C, [count]));
        }
        void U64(ulong address, ulong value) => Words(address, (uint)value, (uint)(value >> 32));
        void Words(ulong address, params uint[] words)
        {
            var bytes = new byte[words.Length * 4];
            for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
            Assert.True(memory.TryWrite(address, bytes));
        }
        uint Read(ulong address)
        {
            Span<byte> bytes = stackalloc byte[4];
            Assert.True(memory.TryRead(address, bytes));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
    }

    private sealed class BindingMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private static long _nextArena;
        private readonly Dictionary<ulong, byte[]> _ranges = new() { [0x1000] = new byte[0x5000] };
        private ulong _next = 0xF000_0000_0000 + (ulong)Interlocked.Increment(ref _nextArena) * 0x100000;
        public int Allocations { get; private set; }
        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = (_next + alignment - 1) & ~(alignment - 1);
            _ranges[address] = new byte[(int)size]; _next = address + size;
            Allocations++; return true;
        }
        public bool TryFreeGuestMemory(ulong address) => _ranges.Remove(address);
        public bool TryRead(ulong address, Span<byte> destination)
        {
            foreach (var (start, bytes) in _ranges)
                if (address >= start && address - start + (ulong)destination.Length <= (ulong)bytes.Length)
                { bytes.AsSpan((int)(address - start), destination.Length).CopyTo(destination); return true; }
            return false;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            foreach (var (start, bytes) in _ranges)
                if (address >= start && address - start + (ulong)source.Length <= (ulong)bytes.Length)
                { source.CopyTo(bytes.AsSpan((int)(address - start))); return true; }
            return false;
        }
    }
}
