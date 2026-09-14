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
    [InlineData(new byte[] { 0x48, 0xB8, 0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0, 0xC0, 0xC3 })]
    [InlineData(new byte[] { 0x48, 0xB8, 0x64, 0x8B, 0x04, 0x25, 0x28, 0, 0, 0, 0xC3 })]
    [InlineData(new byte[] { 0x48, 0xB8, 0x64, 0xC7, 0x04, 0x25, 0, 0, 0, 0,
        0x48, 0xB8, 1, 2, 3, 4, 5, 6, 7, 8, 0xC3 })]
    public void ScanDoesNotPatchInstructionOperands(byte[] instructions)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        fixture.Patch(instructions);
        Assert.Equal(instructions, fixture.Read(instructions.Length));
    }

    [Fact]
    public void ScanPreservesOperandsAcrossExecutableProtectionBoundary()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        byte[] instructions = [0x48, 0xB8, 0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0, 0xC0, 0xC3];
        const int instructionOffset = 0xFFE;
        fixture.Write(instructions, instructionOffset);
        fixture.Protect(0x1000, 0x1000, 0x20);
        fixture.Scan(0, 0x2000);
        Assert.Equal(instructions, fixture.Read(instructions.Length, instructionOffset));
    }

    [Fact]
    public void ScanPatchesLoadAcrossExecutableProtectionBoundary()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        const int instructionOffset = 0xFFC;
        fixture.Write([0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0], instructionOffset);
        fixture.Protect(0x1000, 0x1000, 0x20);
        fixture.Scan(0, 0x2000);
        Assert.Equal(new byte[] { 0x48, 0xE8 }, fixture.Read(2, instructionOffset));
        Assert.Equal(0x40u, fixture.ProtectionAt(instructionOffset));
        Assert.Equal(0x20u, fixture.ProtectionAt(0x1000));
    }

    [Theory]
    [InlineData(0x01u)]
    [InlineData(0x104u)]
    [InlineData(0x04u)]
    public void ScanDoesNotReadAcrossUnavailableRegion(uint nextPageProtection)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        const int instructionOffset = 0xFFC;
        byte[] instructions = [0x64, 0x48, 0x8B, 0x04];
        fixture.Write(instructions, instructionOffset);
        fixture.Protect(0x1000, 0x1000, nextPageProtection);
        fixture.Scan(0, 0x2000);
        Assert.Equal(instructions, fixture.Read(instructions.Length, instructionOffset));
        Assert.Equal(nextPageProtection, fixture.ProtectionAt(0x1000));
    }

    [Theory]
    [InlineData(new byte[] { 0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x64, 0xC7, 0x04, 0x25, 0, 0, 0, 0, 1, 2, 3, 4 })]
    [InlineData(new byte[] { 0x64, 0x8B, 0x04, 0x25, 0x28, 0, 0, 0 })]
    [InlineData(new byte[] { 0x64, 0x48, 0x8B, 0x04, 0x25, 0x28, 0, 0, 0 })]
    public void ScanPatchesCompleteInstructionAtRangeEnd(byte[] instructions)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        fixture.Write(instructions);
        fixture.Scan(0, instructions.Length);
        Assert.NotEqual(instructions, fixture.Read(instructions.Length));
        Assert.Equal(0x90, fixture.Read(1, instructions.Length)[0]);
    }

    [Theory]
    [InlineData(new byte[] { 0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x64, 0xC7, 0x04, 0x25, 0, 0, 0, 0, 1, 2, 3, 4 })]
    [InlineData(new byte[] { 0x64, 0x8B, 0x04, 0x25, 0x28, 0, 0, 0 })]
    public void ScanLeavesTruncatedInstructionUnchanged(byte[] instructions)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        fixture.Write(instructions);
        fixture.Scan(0, instructions.Length - 1);
        Assert.Equal(instructions, fixture.Read(instructions.Length));
    }

    [Fact]
    public void ScanLeavesSse4aInstructionsUnchanged()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        byte[] instructions = [0x66, 0x0F, 0x79, 0xD5, 0x66, 0x41, 0x0F, 0x79, 0xE0, 0xC3];
        fixture.Patch(instructions);
        Assert.Equal(instructions, fixture.Read(instructions.Length));
    }

    [Fact]
    public void ScanContinuesAfterInvalidEncoding()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        byte[] instructions = [0x0F, 0x04, 0xC0, 0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0];
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(instructions));
        var invalid = decoder.Decode();
        Assert.Equal(Code.INVALID, invalid.Code);
        Assert.Equal(3, invalid.Length);
        fixture.Patch(instructions);
        Assert.Equal(new byte[] { 0x0F, 0x04, 0xC0, 0x48, 0xE8 }, fixture.Read(5));
    }

    [Theory]
    [InlineData(new byte[] { 0x66, 0x64, 0xC7, 0x04, 0x25, 0, 0, 0, 0, 0x34, 0x12 })]
    [InlineData(new byte[] { 0xF0, 0x64, 0x48, 0x8B, 0x04, 0x25, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x64, 0x4A, 0x8B, 0x04, 0x25, 0x28, 0, 0, 0 })]
    public void ScanDoesNotChangeUnsupportedOperandSemantics(byte[] instructions)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        fixture.Patch(instructions);
        Assert.Equal(instructions, fixture.Read(instructions.Length));
    }

    [Fact]
    public void RescanPatchesNewlyExecutableMemoryOnlyOnce()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        using var fixture = new PatchFixture();
        byte[] instructions = [0x64, 0xC7, 0x04, 0x25, 0, 0, 0, 0, 1, 2, 3, 4];
        fixture.Write(instructions);
        fixture.Protect(0, 0x1000, 0x04);
        fixture.Scan(0, 0x100);
        Assert.Equal(instructions, fixture.Read(instructions.Length));
        fixture.Protect(0, 0x1000, 0x40);
        fixture.RescanExecutable(0, 0x100);
        var patched = fixture.Read(instructions.Length);
        var stubOffset = fixture.StubOffset;
        Assert.Equal(0xE8, patched[0]);
        fixture.RescanExecutable(0, 0x100);
        Assert.Equal(patched, fixture.Read(instructions.Length));
        Assert.Equal(stubOffset, fixture.StubOffset);
    }

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
        public nint Address { get; } = VirtualAlloc(0, 0x4000, 0x3000, 0x40);
        public nint HandlerAddress => Address + 0x3000;
        public int StubOffset => (int)typeof(DirectExecutionBackend)
            .GetField("_tlsPatchStubOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_backend)!;
        private readonly DirectExecutionBackend _backend = new(new ModuleManager());

        public PatchFixture()
        {
            Assert.NotEqual(0, Address);
            new Span<byte>((void*)Address, 0x4000).Fill(0x90);
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
            Write(instructions);
            Scan(0, 0x100);
        }

        public void Write(byte[] instructions, int offset = 0) =>
            instructions.CopyTo(new Span<byte>((void*)(Address + offset), instructions.Length));

        public void Scan(int offset, int length)
        {
            typeof(DirectExecutionBackend).GetMethod("PatchTlsPatternsInRange",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_backend,
                [(ulong)(Address + offset), (ulong)(Address + offset + length)]);
        }

        public void RescanExecutable(int offset, int length) => typeof(DirectExecutionBackend)
            .GetMethod("RescanTlsPatternsIfExecutable", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_backend, [(ulong)(Address + offset), (ulong)length, 0x40u]);

        public byte[] Read(int length, int offset = 0) =>
            new ReadOnlySpan<byte>((void*)(Address + offset), length).ToArray();

        public void Protect(int offset, int length, uint protection) =>
            Assert.True(VirtualProtect(Address + offset, (nuint)length, protection, out _));

        public uint ProtectionAt(int offset)
        {
            byte* information = stackalloc byte[48];
            Assert.NotEqual((nuint)0, VirtualQuery(Address + offset, information, 48));
            return *(uint*)(information + 36);
        }

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
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previousProtection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint VirtualQuery(nint address, void* information, nuint length);
    }
}
