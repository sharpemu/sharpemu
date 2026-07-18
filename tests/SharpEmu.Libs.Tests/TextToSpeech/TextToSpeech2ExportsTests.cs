// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.TextToSpeech;
using Xunit;

namespace SharpEmu.Libs.Tests.TextToSpeech;

[CollectionDefinition("TextToSpeech2Exports", DisableParallelization = true)]
public sealed class TextToSpeech2ExportsCollectionDefinition;

[Collection("TextToSpeech2Exports")]
public sealed class TextToSpeech2ExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x0000_7FFF_6000_0000;
    private const ulong ConfigurationAddress = MemoryBase + 0x1000;
    private const ulong StatusAddress = MemoryBase + 0x2000;
    private const ulong SpeechParametersAddress = MemoryBase + 0x3000;
    private const ulong TextAddress = MemoryBase + 0x4000;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10_000);
    private readonly CpuContext _context;

    public TextToSpeech2ExportsTests()
    {
        TextToSpeech2Exports.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void Lifecycle_OpenReportsIdleThenClosesAndTerminates()
    {
        InitializeAndOpen();

        _context[CpuRegister.Rdi] = StatusAddress;
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2GetSpeechStatus(_context));
        Assert.True(_context.TryReadInt32(StatusAddress, out var status));
        Assert.Equal(0, status);

        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Close(_context));
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Terminate(_context));
    }

    [Fact]
    public void Open_RejectsNullAndUnreadableConfigurationPointers()
    {
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Initialize(_context));

        _context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            TextToSpeech2Exports.TextToSpeech2Open(_context));

        _context[CpuRegister.Rdi] = MemoryBase + 0x20_000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            TextToSpeech2Exports.TextToSpeech2Open(_context));
    }

    [Fact]
    public void GetSpeechStatus_RequiresAnOpenServiceAndWritableOutput()
    {
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Initialize(_context));
        _context[CpuRegister.Rdi] = StatusAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            TextToSpeech2Exports.TextToSpeech2GetSpeechStatus(_context));

        Open();
        _context[CpuRegister.Rdi] = MemoryBase + 0x20_000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            TextToSpeech2Exports.TextToSpeech2GetSpeechStatus(_context));
    }

    [Fact]
    public void Speak_AcceptsReadableTextAndCompletesSynchronously()
    {
        InitializeAndOpen();
        Assert.True(_context.TryWriteUInt64(SpeechParametersAddress, TextAddress));
        Assert.True(_memory.TryWrite(TextAddress, stackalloc byte[] { (byte)'H', 0, 0, 0 }));

        _context[CpuRegister.Rdi] = SpeechParametersAddress;
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Speak(_context));
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Cancel(_context));

        _context[CpuRegister.Rdi] = StatusAddress;
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2GetSpeechStatus(_context));
        Assert.True(_context.TryReadInt32(StatusAddress, out var status));
        Assert.Equal(0, status);
    }

    [Fact]
    public void PublicNids_RegisterAsTextToSpeech2Exports()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "UOjiprYwVNw", "sceTextToSpeech2Initialize");
        AssertExport(manager, "SoWHuVW0gpU", "sceTextToSpeech2Terminate");
        AssertExport(manager, "X0HZNbSiqyg", "sceTextToSpeech2Open");
        AssertExport(manager, "t4e879M-cSw", "sceTextToSpeech2Close");
        AssertExport(manager, "08JSg9p6bgQ", "sceTextToSpeech2GetSpeechStatus");
        AssertExport(manager, "8ntsRd07EQA", "sceTextToSpeech2Speak");
        AssertExport(manager, "2jiIxUmcsGo", "sceTextToSpeech2Cancel");
    }

    public void Dispose() => TextToSpeech2Exports.ResetForTests();

    private void InitializeAndOpen()
    {
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Initialize(_context));
        Open();
    }

    private void Open()
    {
        Assert.True(_context.TryWriteUInt64(ConfigurationAddress, 1));
        _context[CpuRegister.Rdi] = ConfigurationAddress;
        Assert.Equal(0, TextToSpeech2Exports.TextToSpeech2Open(_context));
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceTextToSpeech2", export.LibraryName);
    }
}
