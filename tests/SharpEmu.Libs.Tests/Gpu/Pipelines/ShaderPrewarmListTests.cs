// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderPrewarmListTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong BufferAddress = PipelineTestGuest.MemoryBase + 0x4_0000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static (IShaderPipelineHost Host, byte[] Spirv) CompileAtRuntime(ShaderPrewarmList list, uint format, bool ieeeMode = false)
    {
        var guest = new PipelineTestGuest(request =>
        {
            Assert.Equal(ieeeMode, request.IeeeMode);
            return Compile(request);
        });
        guest.Host.ShaderPrewarm = list;
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var userData = PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, format);
        var cursor = 0u;
        var options = PipelineTestGuest.ComputeOptions(threadsX: 64);
        options.ComputeInfo!.IeeeMode = ieeeMode;
        guest.Programs.GetOrCompile(
            guest.Source(CodeAddress, ShaderStage.Compute, userData), options, ref cursor, out _);
        return (guest.Host, Assert.Single(guest.Compiler.Shaders).Spirv);
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARecordedComputeProgramCompilesToTheSameSpirvAfterReload(bool ieeeMode)
    {
        byte[] runtime;
        IShaderPipelineHost host;
        using (var list = Open())
        {
            (host, runtime) = CompileAtRuntime(list, BufferDescriptorWords.Format32UInt, ieeeMode);
        }

        using var reloaded = Open();
        var (record, code) = Assert.Single(reloaded.LoadedComputes());
        Assert.Equal(ieeeMode, record.Info.IeeeMode);
        var compiler = new FakeShaderCompiler(request =>
        {
            Assert.Equal(ieeeMode, request.IeeeMode);
            return Compile(request);
        });
        Assert.True(
            ShaderProgramCache.TryCompilePrewarm(
                record, code, compiler, host.SharedInt64AtomicsEnabled, host.ExecGuardElisionEnabled,
                out var compiled, out var layout, out var error),
            error);
        Assert.NotNull(layout);
        Assert.Equal(runtime, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
    }

    [Fact]
    public void OlderFormatRecordsAreRejectedBeforeAppending()
    {
        using (var list = Open())
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        var path = Path.Combine(_directory, ShaderPrewarmList.FileName);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = sizeof(uint);
            stream.Write(BitConverter.GetBytes(1u));
        }
        using (var list = Open())
        {
            Assert.Empty(list.LoadedComputes());
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt, true);
        }
        using var reloaded = Open();
        Assert.True(Assert.Single(reloaded.LoadedComputes()).Record.Info.IeeeMode);
    }

    [Fact]
    public void TheSameCompileIsRecordedOnceAndAnotherSpecializationAddsARecord()
    {
        using (var list = Open())
        {
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt, true);
        }

        using var reloaded = Open();
        var loaded = reloaded.LoadedComputes();
        Assert.Equal(3, loaded.Count);
        Assert.Same(loaded[0].Code.Ranges, loaded[1].Code.Ranges);
    }

    [Fact]
    public void ATruncatedLastRecordIsDroppedAndTheListKeepsAppending()
    {
        using (var list = Open())
        {
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        }

        var path = Path.Combine(_directory, ShaderPrewarmList.FileName);
        var intactLength = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write([0x40, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 2, 9, 9]);
        }

        using (var list = Open())
        {
            Assert.Equal(1, list.LoadedComputeCount);
            Assert.Equal(intactLength, new FileInfo(path).Length);
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
        }

        using var reloaded = Open();
        Assert.Equal(2, reloaded.LoadedComputeCount);
    }

    [Fact]
    public void ReloadKeepsSeparateContinuationsAtTheSameEntryAddress()
    {
        using (var list = Open())
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        using (var list = Open())
        {
            var (original, code) = Assert.Single(list.LoadedComputes());
            foreach (var offset in new ulong[] { 0x100, 0x1_0000_0000 })
            {
                var capture = new ShaderCodeCapture
                {
                    Hash = code.Hash, CodeSize = code.CodeSize, Address = code.Address,
                    Generation = code.Generation, Ranges = code.Ranges,
                    Fused = new FusedCodeParts(HeaderAddress, code.Address + offset, HeaderAddress + offset),
                };
                list.RecordCompute(capture, new ComputePrewarmRecord
                {
                    Hash = original.Hash, CodeSize = original.CodeSize, Address = original.Address,
                    ContinuationAddressOffset = offset, UserDataBase = original.UserDataBase,
                    UserDataCount = original.UserDataCount, PushDataCursor = original.PushDataCursor,
                    Info = original.Info, SystemRegisters = original.SystemRegisters,
                    Specialization = original.Specialization,
                });
            }
        }
        using var reloaded = Open();
        var records = reloaded.LoadedComputes();
        Assert.Equal(3, records.Count);
        Assert.Equal(new ulong[] { 0, 0x100, 0x1_0000_0000 },
            records.Select(entry => entry.Record.ContinuationAddressOffset));
        foreach (var (record, code) in records)
            Assert.Equal(record.ContinuationAddressOffset,
                code.Fused is { } fused ? unchecked(fused.ContinuationAddress - code.Address) : 0);
    }

    [Fact]
    public void TheStampMatchesOnlyTheTextLastWritten()
    {
        using var list = Open();
        Assert.False(list.IsStampCurrent("build-a"));
        list.WriteStamp("build-a");
        Assert.True(list.IsStampCurrent("build-a"));
        Assert.False(list.IsStampCurrent("build-b"));
    }

    [Fact]
    public void ProgressResumesUnderTheSameStampAndClears()
    {
        using (var list = Open())
        {
            Assert.Empty(list.ReadProgress("build-a"));
            list.AppendProgress("build-a", [1UL, 2UL]);
            list.AppendProgress("build-a", [0xFEDC_BA98_7654_3210UL]);
        }

        using (var reopened = Open())
        {
            Assert.Equal(new HashSet<ulong> { 1UL, 2UL, 0xFEDC_BA98_7654_3210UL }, reopened.ReadProgress("build-a"));
            reopened.ClearProgress();
            Assert.Empty(reopened.ReadProgress("build-a"));
        }
    }

    [Fact]
    public void ProgressOfAnotherStampIsDiscarded()
    {
        using var list = Open();
        list.AppendProgress("build-a", [7UL]);

        Assert.Empty(list.ReadProgress("build-b"));
        Assert.Empty(list.ReadProgress("build-a"));
        list.AppendProgress("build-b", [8UL]);
        Assert.Equal(new HashSet<ulong> { 8UL }, list.ReadProgress("build-b"));
    }

    [Fact]
    public void ARecordIdentityDependsOnItsSpecialization()
    {
        using (var list = Open())
        {
            _ = CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            _ = CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
        }

        using var reloaded = Open();
        var identities = reloaded.LoadedComputes().Select(item => ShaderPrewarmList.Identity(item.Record)).ToArray();
        Assert.Equal(2, identities.Length);
        Assert.NotEqual(identities[0], identities[1]);
        Assert.Equal(identities[0], ShaderPrewarmList.Identity(reloaded.LoadedComputes()[0].Record));
    }

    [Fact]
    public void RecordedReadsReplayAsMergedRanges()
    {
        var memory = new FakeCpuMemory(PipelineTestGuest.MemoryBase, 0x1000);
        Assert.True(memory.TryWrite(PipelineTestGuest.MemoryBase + 0x100, [1, 2, 3, 4, 5, 6, 7, 8]));
        var recording = new RecordingCpuMemory(memory);
        Span<byte> word = stackalloc byte[4];
        Assert.True(recording.TryRead(PipelineTestGuest.MemoryBase + 0x104, word));
        Assert.True(recording.TryRead(PipelineTestGuest.MemoryBase + 0x100, word));
        Assert.True(recording.TryRead(PipelineTestGuest.MemoryBase + 0x102, word));

        var range = Assert.Single(recording.TakeRanges());
        Assert.Equal(PipelineTestGuest.MemoryBase + 0x100, range.Address);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, range.Bytes);

        var replay = new ReplayCpuMemory([range]);
        Assert.True(replay.TryRead(PipelineTestGuest.MemoryBase + 0x102, word));
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, word.ToArray());
        Assert.False(replay.TryRead(PipelineTestGuest.MemoryBase + 0x106, word));
    }
}
