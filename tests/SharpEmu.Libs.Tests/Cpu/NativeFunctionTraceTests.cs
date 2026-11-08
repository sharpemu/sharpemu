// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed unsafe class NativeFunctionTraceTests
{
    [Fact]
    public void FrameHistoryRetainsNewAndRefreshedFramesAfterCapacity()
    {
        var records = new OrderedDictionary<(int Thread, ulong Frame), string>();
        for (ulong frame = 0; frame < 128; frame++)
            NativeFunctionTrace.RememberFrame(records, (1, frame), "old");
        NativeFunctionTrace.RememberFrame(records, (1, 0UL), "refreshed");
        NativeFunctionTrace.RememberFrame(records, (2, 0UL), "new thread");
        Assert.Equal(128, records.Count);
        Assert.Equal("refreshed", records[(1, 0)]);
        Assert.Equal("new thread", records[(2, 0)]);
        Assert.False(records.ContainsKey((1, 1)));
        for (ulong frame = 128; frame < 512; frame++)
            NativeFunctionTrace.RememberFrame(records, (1, frame), "new");
        Assert.Equal(128, records.Count);
        Assert.True(records.ContainsKey((1, 511)));
        Assert.False(records.ContainsKey((1, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedProbesShareAnInstructionAndValidateBeforePatching(bool invalidLastSite)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var memory = HostPlatform.Current.Memory;
        var code = memory.Allocate(0, 4096, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, code);
        try
        {
            byte[][] instructions = [[0x0F, 0xB6, 0x8C, 0x24, 0x10, 0x01, 0, 0],
                [0x48, 0x8B, 0x45, 0xB0], [0x48, 0x8B, 0x43, 0x08]];
            for (var index = 0; index < instructions.Length; index++)
                instructions[index].CopyTo(new Span<byte>((void*)(code + (ulong)index * 64), instructions[index].Length));
            if (invalidLastSite) *(byte*)(code + 128) = 0x90;
            Assert.True(memory.Protect(code, 4096, HostPageProtection.ReadExecute, out _));
            using (var trace = new NativeFunctionTrace(
                new NativeFunctionTrace(memory, code, code + 64, serialization: true),
                new NativeFunctionTrace(memory, code, code + 128, schemaWalk: true)))
            {
                if (invalidLastSite)
                {
                    Assert.Throws<InvalidOperationException>(trace.Install);
                    Assert.Equal(instructions[0][0], *(byte*)code);
                    Assert.Equal(instructions[1][0], *(byte*)(code + 64));
                    return;
                }
                trace.Install();
                byte* context = stackalloc byte[256];
                new Span<byte>(context, 256).Clear();
                for (var index = 0; index < instructions.Length; index++)
                {
                    var address = code + (ulong)index * 64;
                    Assert.Equal(0xCC, *(byte*)address);
                    Assert.True(trace.TryHandle(address, context));
                    var resume = *(ulong*)(context + 248);
                    var instruction = instructions[index];
                    Assert.True(new ReadOnlySpan<byte>((void*)resume, instruction.Length).SequenceEqual(instruction));
                    Assert.Equal(address + (ulong)instruction.Length, *(ulong*)(resume + (ulong)instruction.Length + 6));
                }
            }
            for (var index = 0; index < instructions.Length; index++)
                Assert.True(new ReadOnlySpan<byte>((void*)(code + (ulong)index * 64), instructions[index].Length).SequenceEqual(instructions[index]));
        }
        finally { memory.Free(code); }
    }

    [Fact]
    public void AssetNameReadStopsBeforeUnreadablePage()
    {
        if (!OperatingSystem.IsWindows()) return;
        var memory = HostPlatform.Current.Memory;
        var allocation = memory.Allocate(0, 8192, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, allocation);
        try
        {
            const string expected = "MI_Pink.uexp";
            var address = allocation + 4096 - (ulong)(expected.Length + 1) * 2;
            var destination = new Span<char>((void*)address, expected.Length + 1);
            expected.AsSpan().CopyTo(destination);
            destination[^1] = '\0';
            Assert.True(memory.Protect(allocation + 4096, 4096, HostPageProtection.NoAccess, out _));
            Assert.True(NativeFunctionTrace.TryReadAssetName(address, out var actual));
            Assert.Equal(expected, actual);
            destination[^1] = 'x';
            Assert.False(NativeFunctionTrace.TryReadAssetName(address, out _));
            Assert.False(NativeFunctionTrace.TryReadAssetName(0, out _));
        }
        finally { memory.Free(allocation); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false, true)]
    public void TraceResumesVerifiedInstructionsAndRestoresCode(bool serialization, bool objectReference, bool assetRead, bool schemaWalk = false)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var memory = HostPlatform.Current.Memory;
        var code = memory.Allocate(0, 4096, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, code);
        try
        {
            ReadOnlySpan<byte> entry = [0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56, 0x41, 0x54];
            ReadOnlySpan<byte> exit = [0x48, 0x83, 0xC4, 0x30];
            if (serialization)
            {
                entry = [0x0F, 0xB6, 0x8C, 0x24, 0x10, 0x01, 0, 0];
                exit = [0x48, 0x8B, 0x45, 0xB0];
            }
            if (objectReference)
            {
                entry = [0x48, 0x8B, 0x10];
                exit = [0x8B, 0xB5, 0x38, 0xFE, 0xFF, 0xFF];
            }
            if (assetRead)
            {
                entry = [0x41, 0xF6, 0x44, 0x24, 0x08, 0x10];
                exit = [0x48, 0x8B, 0xB5, 0x60, 0xFD, 0xFF, 0xFF];
            }
            if (schemaWalk)
            {
                entry = [0x0F, 0xB6, 0x8C, 0x24, 0x10, 0x01, 0, 0];
                exit = [0x48, 0x8B, 0x43, 0x08];
            }
            entry.CopyTo(new Span<byte>((void*)code, entry.Length));
            exit.CopyTo(new Span<byte>((void*)(code + 64), exit.Length));
            Assert.True(memory.Protect(code, 4096, HostPageProtection.ReadExecute, out _));
            using (var trace = new NativeFunctionTrace(memory, code, code + 64, serialization, objectReference: objectReference, assetRead: assetRead, schemaWalk: schemaWalk))
            {
                trace.Install();
                Assert.Equal(0xCC, *(byte*)code);
                byte* context = stackalloc byte[256];
                new Span<byte>(context, 256).Clear();
                Assert.False(trace.TryHandle(code + 32, context));
                Assert.True(trace.TryHandle(code, context));
                var resume = *(ulong*)(context + 248);
                var length = schemaWalk ? 8 : assetRead ? 6 : objectReference ? 3 : serialization ? 8 : 1;
                Assert.True(new ReadOnlySpan<byte>((void*)resume, length).SequenceEqual(entry[..length]));
                Assert.Equal(0xFF, *(byte*)(resume + (ulong)length));
                Assert.Equal(0x25, *(byte*)(resume + (ulong)length + 1));
                Assert.Equal(code + (ulong)length, *(ulong*)(resume + (ulong)length + 6));
                Assert.True(trace.TryHandle(code + 64, context));
                resume = *(ulong*)(context + 248);
                Assert.True(new ReadOnlySpan<byte>((void*)resume, exit.Length).SequenceEqual(exit));
                Assert.Equal(code + 64 + (ulong)exit.Length, *(ulong*)(resume + (ulong)exit.Length + 6));
            }
            Assert.True(new ReadOnlySpan<byte>((void*)code, entry.Length).SequenceEqual(entry));
            Assert.True(new ReadOnlySpan<byte>((void*)(code + 64), exit.Length).SequenceEqual(exit));
            using var rejected = new NativeFunctionTrace(memory, code + 1, code + 64, serialization, objectReference: objectReference, assetRead: assetRead, schemaWalk: schemaWalk);
            Assert.Throws<InvalidOperationException>(rejected.Install);
            Assert.Equal(exit[0], *(byte*)(code + 64));
        }
        finally { memory.Free(code); }
    }
}
