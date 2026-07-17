// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[CollectionDefinition("SaveDataMemoryState", DisableParallelization = true)]
public sealed class SaveDataMemoryStateCollection;

// The save data memory exports persist to a real backing file whose resolved
// path depends on process-wide environment and title configuration. The
// fixture pins both and the collection keeps other environment-mutating tests
// from running alongside.
[Collection("SaveDataMemoryState")]
public sealed class SaveDataMemoryExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong SetupParamAddress = Base + 0x100;
    private const ulong SetupResultAddress = Base + 0x180;
    private const ulong DataStructAddress = Base + 0x200;
    private const ulong RequestAddress = Base + 0x280;
    private const ulong SyncParamAddress = Base + 0x300;
    private const ulong PayloadAddress = Base + 0x400;
    private const ulong ReadbackAddress = Base + 0x800;
    private const ulong LargePayloadAddress = Base + 0x1000;
    private const ulong LargeReadbackAddress = Base + 0x40000;
    private const ulong MemorySize = 0x2000;
    private const ulong UnmappedAddress = Base + 0x100000;
    private const int MemoryNotReady = unchecked((int)0x809F0012);
    private const int ParameterError = unchecked((int)0x809F0000);
    private const int UserId = 0x1001;
    private const string TitleId = "SDMEMTEST";

    private readonly FakeCpuMemory _memory = new(Base, 0x80000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataMemoryExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-sdmemory-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        SaveDataExports.ConfigureApplicationInfo(TitleId);
    }

    private string MemoryPath =>
        Path.Combine(_root, UserId.ToString(), TitleId, "sce_sdmemory", "memory.dat");

    public void Dispose()
    {
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Setup_ReportsExistingSizeOnSecondCall()
    {
        Assert.Equal(0, Setup());
        Assert.True(_ctx.TryReadUInt64(SetupResultAddress, out var existedSize));
        Assert.Equal(0ul, existedSize);

        Assert.Equal(0, Setup());
        Assert.True(_ctx.TryReadUInt64(SetupResultAddress, out existedSize));
        Assert.Equal(MemorySize, existedSize);
    }

    [Fact]
    public void SetThenGet_RoundTripsThroughBackingFile()
    {
        Assert.Equal(0, Setup());

        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        Assert.True(_ctx.Memory.TryWrite(PayloadAddress, payload));
        WriteRequest(PayloadAddress, (ulong)payload.Length, offset: 0x40);
        Assert.Equal(0, SaveDataExports.SaveDataSetSaveDataMemory2(Invoke()));

        WriteRequest(ReadbackAddress, (ulong)payload.Length, offset: 0x40);
        Assert.Equal(0, SaveDataExports.SaveDataGetSaveDataMemory2(Invoke()));
        var readback = new byte[payload.Length];
        Assert.True(_ctx.Memory.TryRead(ReadbackAddress, readback));
        Assert.Equal(payload, readback);
    }

    [Fact]
    public void SetThenGet_LargePayload_RoundTrips()
    {
        const ulong size = 0x30000;
        Assert.Equal(0, Setup(0x40000));

        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }

        Assert.True(_ctx.Memory.TryWrite(LargePayloadAddress, payload));
        WriteRequest(LargePayloadAddress, size, offset: 0x100);
        Assert.Equal(0, SaveDataExports.SaveDataSetSaveDataMemory2(Invoke()));

        WriteRequest(LargeReadbackAddress, size, offset: 0x100);
        Assert.Equal(0, SaveDataExports.SaveDataGetSaveDataMemory2(Invoke()));
        var readback = new byte[size];
        Assert.True(_ctx.Memory.TryRead(LargeReadbackAddress, readback));
        Assert.Equal(payload, readback);
    }

    [Fact]
    public void Setup_AbsurdSize_ReturnsParameterError()
    {
        Assert.Equal(ParameterError, Setup(ulong.MaxValue));
    }

    [Fact]
    public void Setup_InvalidResultPointer_DoesNotCreateBackingFile()
    {
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Setup(resultAddress: UnmappedAddress));
        Assert.False(File.Exists(MemoryPath));
    }

    [Fact]
    public void Setup_InvalidResultPointer_DoesNotGrowExistingFile()
    {
        Assert.Equal(0, Setup(0x1000));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            Setup(MemorySize, UnmappedAddress));
        Assert.Equal(0x1000, new FileInfo(MemoryPath).Length);
    }

    [Fact]
    public void Setup_GrowingExistingMemory_ZeroExtendsAndPreservesContent()
    {
        Assert.Equal(0, Setup(0x1000));
        var payload = new byte[] { 0x11, 0x22 };
        Assert.True(_ctx.Memory.TryWrite(PayloadAddress, payload));
        WriteRequest(PayloadAddress, (ulong)payload.Length, offset: 0xFF0);
        Assert.Equal(0, SaveDataExports.SaveDataSetSaveDataMemory2(Invoke()));

        Assert.Equal(0, Setup(MemorySize));
        Assert.True(_ctx.TryReadUInt64(SetupResultAddress, out var existedSize));
        Assert.Equal(0x1000ul, existedSize);

        WriteRequest(ReadbackAddress, 0x20, offset: 0xFF0);
        Assert.Equal(0, SaveDataExports.SaveDataGetSaveDataMemory2(Invoke()));
        var readback = new byte[0x20];
        Assert.True(_ctx.Memory.TryRead(ReadbackAddress, readback));
        Assert.Equal(payload, readback[..2]);
        Assert.All(readback[2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void GetSetSync_BeforeSetup_ReturnMemoryNotReady()
    {
        WriteRequest(ReadbackAddress, 0x10, offset: 0);
        Assert.Equal(MemoryNotReady, SaveDataExports.SaveDataGetSaveDataMemory2(Invoke()));
        Assert.Equal(MemoryNotReady, SaveDataExports.SaveDataSetSaveDataMemory2(Invoke()));
        Assert.Equal(MemoryNotReady, Sync());
    }

    [Fact]
    public void GetAndSync_AfterSetup_Succeed()
    {
        Assert.Equal(0, Setup());
        WriteRequest(ReadbackAddress, 0x10, offset: 0);
        Assert.Equal(0, SaveDataExports.SaveDataGetSaveDataMemory2(Invoke()));
        Assert.Equal(0, Sync());
    }

    [Fact]
    public void Get_BackingFileRemovedAfterSetup_ReturnsMemoryNotReady()
    {
        Assert.Equal(0, Setup());
        Directory.Delete(_root, recursive: true);
        WriteRequest(ReadbackAddress, 0x10, offset: 0);
        Assert.Equal(MemoryNotReady, SaveDataExports.SaveDataGetSaveDataMemory2(Invoke()));
    }

    [Theory]
    [InlineData(MemorySize, 1ul)]
    [InlineData(0ul, MemorySize + 1)]
    [InlineData(ulong.MaxValue, 0x10ul)]
    public void Get_OutOfRange_ReturnsParameterError(ulong offset, ulong size)
    {
        Assert.Equal(0, Setup());
        WriteRequest(ReadbackAddress, size, offset);
        Assert.Equal(ParameterError, SaveDataExports.SaveDataGetSaveDataMemory2(Invoke()));
    }

    [Theory]
    [InlineData(MemorySize, 1ul)]
    [InlineData(0ul, MemorySize + 1)]
    [InlineData(ulong.MaxValue, 0x10ul)]
    public void Set_OutOfRange_ReturnsParameterError(ulong offset, ulong size)
    {
        Assert.Equal(0, Setup());
        WriteRequest(PayloadAddress, size, offset);
        Assert.Equal(ParameterError, SaveDataExports.SaveDataSetSaveDataMemory2(Invoke()));
    }

    private int Setup(ulong memorySize = MemorySize, ulong resultAddress = SetupResultAddress)
    {
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param[0x04..], UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(param[0x08..], memorySize);
        Assert.True(_ctx.Memory.TryWrite(SetupParamAddress, param));

        _ctx[CpuRegister.Rdi] = SetupParamAddress;
        _ctx[CpuRegister.Rsi] = resultAddress;
        return SaveDataExports.SaveDataSetupSaveDataMemory2(_ctx);
    }

    private int Sync()
    {
        Span<byte> param = stackalloc byte[0x28];
        param.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(param, UserId);
        Assert.True(_ctx.Memory.TryWrite(SyncParamAddress, param));

        _ctx[CpuRegister.Rdi] = SyncParamAddress;
        return SaveDataExports.SaveDataSyncSaveDataMemory(_ctx);
    }

    private void WriteRequest(ulong bufAddress, ulong bufSize, ulong offset)
    {
        Span<byte> data = stackalloc byte[0x18];
        BinaryPrimitives.WriteUInt64LittleEndian(data, bufAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(data[0x08..], bufSize);
        BinaryPrimitives.WriteUInt64LittleEndian(data[0x10..], offset);
        Assert.True(_ctx.Memory.TryWrite(DataStructAddress, data));

        Span<byte> request = stackalloc byte[0x10];
        request.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(request, UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(request[0x08..], DataStructAddress);
        Assert.True(_ctx.Memory.TryWrite(RequestAddress, request));
    }

    private CpuContext Invoke()
    {
        _ctx[CpuRegister.Rdi] = RequestAddress;
        return _ctx;
    }
}
