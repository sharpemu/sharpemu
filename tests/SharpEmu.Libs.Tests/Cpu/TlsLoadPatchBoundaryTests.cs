// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class TlsLoadPatchBoundaryTests
{
    [Theory]
    [InlineData(new byte[] { 0xEB, 0x66, 0x66 })]
    [InlineData(new byte[] { 0x75, 0xEB, 0x66, 0x66, 0x66 })]
    [InlineData(new byte[] { 0x4E, 0x8B, 0x34, 0xEB, 0x66, 0x66, 0x66 })]
    [InlineData(new byte[] { 0x90 })]
    public void ScanPreservesBytesBeforeSegmentPrefix(byte[] leadingBytes)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        var instructions = leadingBytes.Concat(new byte[]
            { 0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0 }).ToArray();
        fixture.Patch(instructions);
        var patched = fixture.Read(instructions.Length);
        Assert.Equal(leadingBytes, patched[..leadingBytes.Length]);
        Assert.Equal(0x48, patched[leadingBytes.Length]);
        Assert.Equal(0xE8, patched[leadingBytes.Length + 1]);
        foreach (var options in new[] { DecoderOptions.None, DecoderOptions.AMD })
        {
            var prefixStart = leadingBytes.Length;
            while (prefixStart > 0 && patched[prefixStart - 1] == 0x66) prefixStart--;
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(patched[prefixStart..]), options);
            decoder.IP = (ulong)fixture.Address + (ulong)prefixStart;
            var instruction = decoder.Decode();
            Assert.Equal(Code.Call_rel32_64, instruction.Code);
            Assert.Equal((ulong)fixture.HandlerAddress, instruction.NearBranch64);
        }
    }

    [Fact]
    public unsafe void NativeLoadPreservesLiveAccumulator()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        const ulong accumulator = 0x123456789ABCDEF0;
        byte[] instructions = [0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0,
            0x66, 0x66, 0x64, 0x4C, 0x8B, 0x0C, 0x25, 0, 0, 0, 0,
            0x4C, 0x31, 0xC8, 0xC3];
        BinaryPrimitives.WriteUInt64LittleEndian(instructions.AsSpan(2), accumulator);
        fixture.Patch(instructions);
        var execute = (delegate* unmanaged<ulong>)fixture.Address;
        Assert.Equal(accumulator ^ PatchFixture.ThreadPointer, execute());
    }

    private sealed unsafe class PatchFixture : IDisposable
    {
        public const ulong ThreadPointer = 0x00007FFE00000000;
        public nint Address { get; } = VirtualAlloc(0, 0x1000, 0x3000, 0x40);
        public nint HandlerAddress => Address + 0x800;
        private readonly DirectExecutionBackend _backend = new(new ModuleManager());

        public PatchFixture()
        {
            Assert.NotEqual(0, Address);
            new Span<byte>((void*)Address, 0x1000).Fill(0x90);
            var handler = new Span<byte>((void*)HandlerAddress, 11);
            handler[0] = 0x48;
            handler[1] = 0xB8;
            BinaryPrimitives.WriteUInt64LittleEndian(handler[2..], ThreadPointer);
            handler[10] = 0xC3;
            SetField("_tlsHandlerAddress", HandlerAddress);
            SetField("_tlsPatchStubOffset", 0x100);
        }

        private void SetField(string name, object value) => typeof(DirectExecutionBackend)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_backend, value);

        public void Patch(byte[] instructions)
        {
            instructions.CopyTo(new Span<byte>((void*)Address, instructions.Length));
            typeof(DirectExecutionBackend).GetMethod("PatchTlsPatternsInRange",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_backend,
                [(ulong)Address, (ulong)Address + 0x100]);
        }

        public byte[] Read(int length) => new ReadOnlySpan<byte>((void*)Address, length).ToArray();

        public void Dispose()
        {
            SetField("_tlsHandlerAddress", (nint)0);
            _backend.Dispose();
            Assert.True(VirtualFree(Address, 0, 0x8000));
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFree(nint address, nuint size, uint freeType);
    }
}
