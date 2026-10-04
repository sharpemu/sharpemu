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

    private static (FakePipelineHost Host, byte[] Spirv) CompileAtRuntime(
        ShaderPrewarmList list,
        uint format,
        Action<FakePipelineHost>? configureHost = null)
    {
        var guest = new PipelineTestGuest(Compile);
        configureHost?.Invoke(guest.Host);
        guest.Host.ShaderPrewarm = list;
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var userData = PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, format);
        var cursor = 0u;
        guest.Programs.GetOrCompile(
            guest.Source(CodeAddress, ShaderStage.Compute, userData),
            PipelineTestGuest.ComputeOptions(
                threadsX: 64,
                hostSubgroupSize: guest.Host.ComputeSubgroupSize,
                workgroupAxisMapping: guest.Host.WorkgroupAxisMapping),
            ref cursor,
            out _);
        return (guest.Host, Assert.Single(guest.Compiler.Shaders).Spirv);
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    [Fact]
    public void ARecordedComputeProgramPreservesHostInputsAndCompilesToTheSameSpirvAfterReload()
    {
        var workgroupAxisMapping = new ComputeWorkgroupAxisMapping(1, 2, 0);
        byte[] runtime;
        FakePipelineHost host;
        using (var list = Open())
        {
            (host, runtime) = CompileAtRuntime(list, BufferDescriptorWords.Format32UInt, candidate =>
            {
                candidate.ComputeSubgroupSize = 32;
                candidate.WorkgroupAxisMapping = workgroupAxisMapping;
                candidate.BufferInt64AtomicsSupported = true;
                candidate.ShaderFloat64Supported = true;
                candidate.ShaderSignedZeroInfNanPreserveFloat32Supported = true;
                candidate.ShaderDeviceClockSupported = true;
                candidate.ShaderDeviceClockShift = 7;
            });
        }

        using var reloaded = Open();
        var (record, code) = Assert.Single(reloaded.LoadedComputes());
        Assert.True(record.HasHostComputeInfo);
        Assert.Equal(32u, record.Info.HostSubgroupSize);
        Assert.Equal(workgroupAxisMapping, record.Info.WorkgroupAxisMapping);
        var compiler = new FakeShaderCompiler(Compile);
        Assert.True(
            ShaderProgramCache.TryCompilePrewarm(
                record, code, compiler, host, out var compiled, out var layout, out var error),
            error);
        Assert.NotNull(layout);
        Assert.Equal(runtime, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
        var request = Assert.Single(compiler.Requests);
        Assert.Equal(32u, request.HostSubgroupSize);
        Assert.True(request.BufferInt64AtomicsSupported);
        Assert.True(request.ShaderFloat64Supported);
        Assert.True(request.ShaderSignedZeroInfNanPreserveFloat32Supported);
        Assert.True(request.ShaderDeviceClockSupported);
        Assert.Equal(7u, request.ShaderDeviceClockShift);
    }

    [Fact]
    public void TheSameCompileIsRecordedOnceAndAnotherSpecializationAddsARecord()
    {
        using (var list = Open())
        {
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
        }

        using var reloaded = Open();
        var loaded = reloaded.LoadedComputes();
        Assert.Equal(2, loaded.Count);
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
