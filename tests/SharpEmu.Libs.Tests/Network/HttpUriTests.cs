// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class HttpUriTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong SourceAddress = MemoryBase + 0x100;
    private const ulong OutputAddress = MemoryBase + 0x5000;
    private const ulong RequiredSizeAddress = MemoryBase + 0x5100;
    private const ulong PoolAddress = MemoryBase + 0x5200;
    private const int MemorySize = 0xA000;
    private const int InvalidUrl = unchecked((int)0x80433060);
    private const int OutOfMemory = unchecked((int)0x80431022);
    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);

    [Fact]
    public void ComponentLayout_HasFixedGuestOffsetsAndSize()
    {
        Assert.Equal(80, Marshal.SizeOf<HttpExports.HttpUriComponents>());
        var fieldNames = new[]
        {
            "IsOpaque", "SchemeAddress", "UserNameAddress", "PasswordAddress", "HostNameAddress",
            "PathAddress", "QueryAddress", "FragmentAddress", "Port"
        };
        for (var fieldIndex = 0; fieldIndex < fieldNames.Length; fieldIndex++)
        {
            Assert.Equal(fieldIndex * 8, Marshal.OffsetOf<HttpExports.HttpUriComponents>(fieldNames[fieldIndex]).ToInt32());
        }
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void Export_IsRegisteredForBothGenerations(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
        Assert.True(manager.TryGetExport("IWalAn-guFs", out var export));
        Assert.Equal("sceHttpUriParse", export.Name);
        Assert.Equal("libSceHttp", export.LibraryName);
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void Parse_QueryThenFillPublishesCompleteGuestStructure(Generation generation)
    {
        var context = CreateContext("https://user:pass@foo.com:1443/var/test.cgi?test#hoge", generation);
        Fill(OutputAddress - 8, 96, 0xA5);
        Fill(RequiredSizeAddress - 8, 24, 0xA5);
        Fill(PoolAddress - 8, 256, 0xA5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(0, HttpExports.ParseHttpUri(context));
        var expectedComponents = new[] { "https", "user", "pass", "foo.com", "/var/test.cgi", "?test", "#hoge" };
        var expectedPool = Encoding.UTF8.GetBytes(string.Join('\0', expectedComponents) + '\0');
        Assert.Equal((ulong)expectedPool.Length, ReadSize());
        Assert.All(ReadBytes(OutputAddress, 80), value => Assert.Equal(0xA5, value));

        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rdx] = PoolAddress;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = ReadSize();
        Assert.Equal(0, HttpExports.ParseHttpUri(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        var output = ReadBytes(OutputAddress, 80);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(output));
        var poolOffset = 0;
        for (var componentIndex = 0; componentIndex < expectedComponents.Length; componentIndex++)
        {
            Assert.Equal(PoolAddress + (ulong)poolOffset, ReadComponentAddress(output, componentIndex));
            poolOffset += expectedComponents[componentIndex].Length + 1;
        }

        Assert.Equal(1443, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(64)));
        Assert.All(output[4..8], value => Assert.Equal(0, value));
        Assert.All(output[66..], value => Assert.Equal(0, value));
        Assert.Equal(expectedPool, ReadBytes(PoolAddress, expectedPool.Length));
        foreach (var guardAddress in new[]
                 {
                     OutputAddress - 8, OutputAddress + 80, RequiredSizeAddress - 8,
                     RequiredSizeAddress + 8, PoolAddress - 8, PoolAddress + (ulong)expectedPool.Length
                 })
        {
            Assert.All(ReadBytes(guardAddress, 8), value => Assert.Equal(0xA5, value));
        }
    }

    [Theory]
    [InlineData("https://Example.COM", false, 0, "https", null, null, "Example.COM", null, null, null)]
    [InlineData("https://[2001:db8::1]:65535/a?#", false, 65535, "https", null, null, "[2001:db8::1]", "/a", "?", "#")]
    [InlineData("http://user@host:0/", false, 0, "http", "user", null, "host", "/", null, null)]
    [InlineData("http://:@host/path", false, 0, "http", "", "", "host", "/path", null, null)]
    [InlineData("file:///folder/file", false, 0, "file", null, null, null, "/folder/file", null, null)]
    [InlineData("mailto:user@example.invalid?subject=test#part", true, 0, "mailto", null, null, null, "user@example.invalid", "?subject=test", "#part")]
    [InlineData("a+1.-:value", true, 0, "a+1.-", null, null, null, "value", null, null)]
    [InlineData("", true, 0, "", null, null, "", "", null, null)]
    [InlineData("HTTP://host/%2f/../café?q=a+b%20c#X", false, 0, "HTTP", null, null, "host", "/%2f/../café", "?q=a+b%20c", "#X")]
    public void Parse_PreservesComponentsWithoutNormalization(
        string source, bool isOpaque, int port, string? scheme, string? userName, string? password,
        string? hostName, string? path, string? query, string? fragment)
    {
        var context = CreateContext(source);
        Assert.Equal(0, HttpExports.ParseHttpUri(context));
        var output = ReadBytes(OutputAddress, 80);
        Assert.Equal(isOpaque ? 1 : 0, BinaryPrimitives.ReadInt32LittleEndian(output));
        Assert.Equal(port, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(64)));
        var expectedComponents = new[] { scheme, userName, password, hostName, path, query, fragment };
        var expectedSize = 0;
        for (var componentIndex = 0; componentIndex < expectedComponents.Length; componentIndex++)
        {
            var componentAddress = ReadComponentAddress(output, componentIndex);
            if (expectedComponents[componentIndex] is not { } expectedComponent)
            {
                Assert.Equal(0UL, componentAddress);
                continue;
            }

            var expectedBytes = Encoding.UTF8.GetBytes(expectedComponent + '\0');
            Assert.Equal(PoolAddress + (ulong)expectedSize, componentAddress);
            Assert.Equal(expectedBytes, ReadBytes(componentAddress, expectedBytes.Length));
            expectedSize += expectedBytes.Length;
        }

        Assert.Equal((ulong)expectedSize, ReadSize());
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("1http://host/")]
    [InlineData("ht_tp://host/")]
    [InlineData(":value")]
    [InlineData("http://host:")]
    [InlineData("http://host:-1/")]
    [InlineData("http://host:65536/")]
    [InlineData("http://host:99999999999999999999/")]
    [InlineData("http://host:12x/")]
    [InlineData("http://[::1/path")]
    [InlineData("http://[::1]extra/path")]
    public void Parse_RejectsInvalidInputWithoutWritingOutputs(string source)
    {
        var context = CreateContext(source);
        Fill(OutputAddress, 80, 0xA5);
        Fill(RequiredSizeAddress, 8, 0xA5);
        Fill(PoolAddress, 128, 0xA5);
        Assert.Equal(InvalidUrl, HttpExports.ParseHttpUri(context));
        Assert.Equal(unchecked((ulong)InvalidUrl), context[CpuRegister.Rax]);
        Assert.All(ReadBytes(OutputAddress, 80), value => Assert.Equal(0xA5, value));
        Assert.All(ReadBytes(RequiredSizeAddress, 8), value => Assert.Equal(0xA5, value));
        Assert.All(ReadBytes(PoolAddress, 128), value => Assert.Equal(0xA5, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    public void Parse_RejectsShortPoolAndReportsRequiredSize(int capacity)
    {
        var context = CreateContext("https://host/path");
        context[CpuRegister.R8] = (ulong)capacity;
        Fill(PoolAddress, 128, 0xA5);
        Fill(OutputAddress, 80, 0xA5);
        Assert.Equal(OutOfMemory, HttpExports.ParseHttpUri(context));
        Assert.Equal(17UL, ReadSize());
        Assert.All(ReadBytes(PoolAddress, 128), value => Assert.Equal(0xA5, value));
        Assert.All(ReadBytes(OutputAddress, 80), value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void Parse_WithoutPoolWritesOnlyMetadataAndNullPointers()
    {
        var context = CreateContext("https://host:443/path");
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.R8] = 0;
        Fill(OutputAddress, 80, 0xA5);
        Assert.Equal(0, HttpExports.ParseHttpUri(context));
        var output = ReadBytes(OutputAddress, 80);
        Assert.All(output[..64], value => Assert.Equal(0, value));
        Assert.Equal(443, BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(64)));
        Assert.All(output[66..], value => Assert.Equal(0, value));
    }

    [Fact]
    public void Parse_RejectsNullSourceAndMissingOutputs()
    {
        var context = CreateContext("https://host/");
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidUrl, HttpExports.ParseHttpUri(context));
        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdi] = context[CpuRegister.Rdx] = context[CpuRegister.Rcx] = 0;
        Assert.Equal(unchecked((int)0x804311FE), HttpExports.ParseHttpUri(context));
    }

    [Theory]
    [InlineData(CpuRegister.Rdi, 79)]
    [InlineData(CpuRegister.Rsi, 0)]
    [InlineData(CpuRegister.Rdx, 16)]
    [InlineData(CpuRegister.Rcx, 7)]
    public void Parse_ReportsGuestMemoryFaultForTruncatedRanges(CpuRegister register, int availableBytes)
    {
        var context = CreateContext("https://host/path");
        var truncatedAddress = MemoryBase + MemorySize - (ulong)availableBytes;
        Fill(truncatedAddress, availableBytes, 0xA5);
        context[register] = truncatedAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, HttpExports.ParseHttpUri(context));
        Assert.All(ReadBytes(truncatedAddress, availableBytes), value => Assert.Equal(0xA5, value));
    }

    [Theory]
    [InlineData(CpuRegister.Rdi)]
    [InlineData(CpuRegister.Rsi)]
    [InlineData(CpuRegister.Rdx)]
    [InlineData(CpuRegister.Rcx)]
    public void Parse_RejectsAddressOverflow(CpuRegister register)
    {
        var context = CreateContext("https://host/path");
        context[register] = ulong.MaxValue - 2;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, HttpExports.ParseHttpUri(context));
    }

    [Theory]
    [InlineData(16383, 0)]
    [InlineData(16384, InvalidUrl)]
    public void Parse_EnforcesMaximumSourceLength(int length, int expectedResult)
    {
        var context = CreateContext("a:" + new string('x', length - 2));
        context[CpuRegister.Rdi] = context[CpuRegister.Rdx] = 0;
        Assert.Equal(expectedResult, HttpExports.ParseHttpUri(context));
    }

    [Fact]
    public void Parse_SourceMayEndAtMappingBoundary()
    {
        var context = CreateContext("unused");
        var source = "https://host/path";
        var address = MemoryBase + MemorySize - (ulong)source.Length - 1;
        _memory.WriteCString(address, source);
        context[CpuRegister.Rsi] = address;
        Assert.Equal(0, HttpExports.ParseHttpUri(context));
    }

    private CpuContext CreateContext(string source, Generation generation = Generation.Gen5)
    {
        _memory.WriteCString(SourceAddress, source);
        var context = new CpuContext(_memory, generation);
        context[CpuRegister.Rdi] = OutputAddress;
        context[CpuRegister.Rsi] = SourceAddress;
        context[CpuRegister.Rdx] = PoolAddress;
        context[CpuRegister.Rcx] = RequiredSizeAddress;
        context[CpuRegister.R8] = 0x4000;
        return context;
    }

    private static ulong ReadComponentAddress(byte[] output, int componentIndex) =>
        BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(8 + componentIndex * 8));

    private ulong ReadSize() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(RequiredSizeAddress, 8));

    private byte[] ReadBytes(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private void Fill(ulong address, int length, byte value)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
