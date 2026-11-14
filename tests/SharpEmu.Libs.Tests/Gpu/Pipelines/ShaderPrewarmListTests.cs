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

    private static (IShaderPipelineHost Host, byte[] Spirv) CompileAtRuntime(ShaderPrewarmList list, uint format, bool ieeeMode = false,
        bool imageInt64Atomics = false)
    {
        var guest = new PipelineTestGuest(request =>
        {
            Assert.Equal(ieeeMode, request.IeeeMode);
            Assert.Equal(imageInt64Atomics, request.SupportsImageInt64Atomics);
            return Compile(request);
        });
        guest.Host.ShaderPrewarm = list;
        guest.Host.ImageInt64AtomicsEnabled = imageInt64Atomics;
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var userData = PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, format);
        var cursor = 0u;
        var options = PipelineTestGuest.ComputeOptions(threadsX: 64);
        options.ComputeInfo!.IeeeMode = ieeeMode;
        guest.Programs.GetOrCompile(
            guest.Source(CodeAddress, ShaderStage.Compute, userData), options, ref cursor, out _);
        return (guest.Host, Assert.Single(guest.Compiler.Shaders).Spirv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageBindingPolicyRoundTripsAndPrewarmRejectsTheOtherHostPolicy(bool stable)
    {
        var limits = stable ? ImageBindingTestProgram.Limits : default;
        byte[] runtime;
        using (var list = Open())
        {
            var guest = new PipelineTestGuest(Compile);
            guest.Host.ShaderPrewarm = list;
            guest.Host.ComputeImageDescriptorLimits = limits;
            guest.RegisterProgram(CodeAddress, HeaderAddress, ImageBindingTestProgram.Code());
            ImageBindingTestProgram.WriteDescriptors(guest, BufferAddress, ImageBindingTestProgram.Partitions[0]);
            var cursor = 0u;
            guest.Programs.GetOrCompile(guest.Source(CodeAddress, ShaderStage.Compute,
                    [unchecked((uint)BufferAddress), (uint)(BufferAddress >> 32)]),
                PipelineTestGuest.ComputeOptions(), ref cursor, out _);
            runtime = Assert.Single(guest.Compiler.Shaders).Spirv;
        }
        using var reloaded = Open();
        var (record, code) = Assert.Single(reloaded.LoadedComputes());
        Assert.Equal(stable, record.Specialization.StableImageBindings);
        Assert.Equal(stable, record.Specialization.Clone().StableImageBindings);
        var compiler = new FakeShaderCompiler(Compile);
        Assert.True(ShaderProgramCache.TryCompilePrewarm(record, code, compiler, false, true, false,
            out var compiled, out _, out var error, stableImageLimits: limits), error);
        Assert.Equal(runtime, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
        var rejected = new FakeShaderCompiler(Compile);
        Assert.False(ShaderProgramCache.TryCompilePrewarm(record, code, rejected, false, true, false,
            out _, out _, out error, stableImageLimits: stable ? default : ImageBindingTestProgram.Limits));
        Assert.Contains("image binding policy", error);
        Assert.Empty(rejected.Requests);
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    public void ARecordedComputeProgramCompilesToTheSameSpirvAfterReload(bool ieeeMode, bool imageInt64Atomics = false)
    {
        byte[] runtime;
        IShaderPipelineHost host;
        using (var list = Open())
        {
            (host, runtime) = CompileAtRuntime(list, BufferDescriptorWords.Format32UInt, ieeeMode, imageInt64Atomics);
        }

        using var reloaded = Open();
        var (record, code) = Assert.Single(reloaded.LoadedComputes());
        Assert.Equal(ieeeMode, record.Info.IeeeMode);
        var compiler = new FakeShaderCompiler(request =>
        {
            Assert.Equal(ieeeMode, request.IeeeMode);
            Assert.Equal(imageInt64Atomics, request.SupportsImageInt64Atomics);
            return Compile(request);
        });
        Assert.True(
            ShaderProgramCache.TryCompilePrewarm(
                record, code, compiler, host.SharedInt64AtomicsEnabled, host.ExecGuardElisionEnabled,
                host.ShaderSignedZeroInfNanPreserveFloat32Supported, out var compiled, out var layout, out var error,
                imageInt64Atomics: host.ImageInt64AtomicsEnabled),
            error);
        Assert.NotNull(layout);
        Assert.False(layout.UsesMeshDrawParameters);
        Assert.Equal(runtime, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    public void OlderFormatRecordsAreRejectedBeforeAppending(uint version)
    {
        using (var list = Open())
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        var path = Path.Combine(_directory, ShaderPrewarmList.FileName);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = sizeof(uint);
            stream.Write(BitConverter.GetBytes(version));
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

    // Compute records run by the earliest time into a session at which one used them, and a later session that uses a
    // record sooner moves it up.
    [Fact]
    public void ComputeRecordsRunByTheirEarliestUse()
    {
        using (var list = Open())
        {
            list.SessionClock = () => 10;
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            list.SessionClock = () => 50;
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
        }

        using (var list = Open())
        {
            Assert.Equal([BufferDescriptorWords.Format32UInt, BufferDescriptorWords.Format32x4UInt], Formats(list));
            list.SessionClock = () => 5;
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
            list.SessionClock = () => 30;
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        }

        using var reloaded = Open();
        Assert.Equal([BufferDescriptorWords.Format32x4UInt, BufferDescriptorWords.Format32UInt], Formats(reloaded));
    }

    // A main list without first uses keeps list order.
    [Fact]
    public void ComputeRecordsWithoutFirstUsesKeepListOrder()
    {
        using (var list = Open())
        {
            list.SessionClock = () => 50;
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            list.SessionClock = () => 10;
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
        }

        File.Delete(Path.Combine(_directory, ShaderPrewarmList.ExtensionFileName));
        using var reloaded = Open();
        Assert.Equal([BufferDescriptorWords.Format32UInt, BufferDescriptorWords.Format32x4UInt], Formats(reloaded));
    }

    // A record whose compile takes long runs by the time it must start, its first use minus its longest recorded
    // compile; a compile of under a second is not recorded, and a shorter one leaves the longer.
    [Fact]
    public void ALongCompileRunsByTheTimeItMustStart()
    {
        using (var list = Open())
        {
            list.SessionClock = () => 10;
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            list.SessionClock = () => 50;
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
        }

        using (var list = Open())
        {
            var (first, second) = (list.LoadedComputes()[0].Record, list.LoadedComputes()[1].Record);
            list.RecordComputeCost(second, 30);
            Assert.Equal([BufferDescriptorWords.Format32UInt, BufferDescriptorWords.Format32x4UInt], Formats(list));
            // The second must start by 9.8 s, before the first's 10 s; a recorded half second would move the first to 9.5 s.
            list.RecordComputeCost(first, 0.5f);
            list.RecordComputeCost(second, 40.2f);
            Assert.Equal([BufferDescriptorWords.Format32x4UInt, BufferDescriptorWords.Format32UInt], Formats(list));
            // A shorter compile leaves the longer: 50 - 20 = 30 s would put the second after the first again.
            list.RecordComputeCost(second, 20);
            Assert.Equal([BufferDescriptorWords.Format32x4UInt, BufferDescriptorWords.Format32UInt], Formats(list));
        }

        using var reloaded = Open();
        Assert.Equal([BufferDescriptorWords.Format32x4UInt, BufferDescriptorWords.Format32UInt], Formats(reloaded));
        // Scheduled by these starts: 50 - 40.2 for the second record, the first one's first use for the first.
        Assert.Equal(new[] { 50f - 40.2f, 10f }, reloaded.LoadedComputeStarts().Select(static item => item.StartBy));
    }

    // A schema fingerprint that changes discards every recorded main list and graphics list and every pixel and vertex
    // record, so it may only change with a deliberate format change. These are the values the recorded lists carry.
    [Fact]
    public void TheSchemaFingerprintsKeepTheValuesOfTheRecordedLists()
    {
        Assert.Equal(0x24DB2A98u, ShaderPrewarmList.SchemaFingerprint);
        Assert.Equal(0x31C0D60Du, ShaderPrewarmList.PixelSchemaFingerprint);
        Assert.Equal(0x31C5863Bu, ShaderPrewarmList.VertexSchemaFingerprint);
        Assert.Equal(0xD15F4A8Du, ShaderPrewarmList.GraphicsSchemaFingerprint);
    }

    private sealed class SchemaSample
    {
        public List<BufferSpecialization> Buffers { get; init; } = [];
        public uint? Optional { get; init; }
        public (ulong Address, uint SizeBytes)[] Ranges { get; init; } = [];
        public uint Plain { get; init; }
    }

    // Generic types are named without the assemblies of their arguments, whose versions change with every release and
    // runtime, so the fingerprints stay those of the recorded lists.
    [Fact]
    public void TheSchemaTextNamesGenericTypesWithoutAssemblies()
    {
        Assert.Equal(
            "SharpEmu.Libs.Tests.Gpu.Pipelines.ShaderPrewarmListTests+SchemaSample{" +
            "System.Collections.Generic.List`1[SharpEmu.ShaderCompiler.Resources.BufferSpecialization] Buffers;" +
            "System.Nullable`1[System.UInt32] Optional;" +
            "System.UInt32 Plain;" +
            "System.ValueTuple`2[System.UInt64,System.UInt32][] Ranges;}",
            ShaderPrewarmList.SchemaText(typeof(SchemaSample)));
    }

    // Records that differ only in a buffer's stride word, which the loader skips, load as one record.
    [Fact]
    public void RecordsThatDifferOnlyInAnOldBufferStrideLoadAsOne()
    {
        using (var list = Open())
        {
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        }

        var path = Path.Combine(_directory, ShaderPrewarmList.FileName);
        var compute = MainListBodies(File.ReadAllBytes(path)).Single(static body => body[0] == 2);
        // The buffer's stride field (written as 0), then its descriptor format.
        byte[] field = [0, 0, 0, 0, .. BitConverter.GetBytes(BufferDescriptorWords.Format32UInt)];
        var at = compute.AsSpan().IndexOf(field);
        Assert.True(at > 0);
        Assert.Equal(-1, compute.AsSpan(at + 1).IndexOf(field));
        using (var file = File.Open(path, FileMode.Append))
        {
            foreach (var stride in new uint[] { 16, 32 })
            {
                var old = (byte[])compute.Clone();
                BitConverter.TryWriteBytes(old.AsSpan(at), stride);
                file.Write(BitConverter.GetBytes((uint)old.Length));
                file.Write(BitConverter.GetBytes(System.IO.Hashing.XxHash3.HashToUInt64(old)));
                file.Write(old);
            }
        }

        using var reloaded = Open();
        Assert.Single(reloaded.LoadedComputes());
    }

    // Each record of a main list: its kind, then its payload.
    private static IEnumerable<byte[]> MainListBodies(byte[] data)
    {
        for (var offset = 12; data.Length - offset >= 13;)
        {
            var length = BitConverter.ToInt32(data, offset);
            yield return data.AsSpan(offset + 12, length).ToArray();
            offset += 12 + length;
        }
    }

    private static uint[] Formats(ShaderPrewarmList list) =>
        [.. list.LoadedComputes().Select(item => item.Record.Specialization.Buffers[0].DescriptorFormat)];

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
        // The order follows each record's first use; the continuations are what this checks.
        Assert.Equal(new ulong[] { 0, 0x100, 0x1_0000_0000 },
            records.Select(entry => entry.Record.ContinuationAddressOffset).Order());
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
    public void DeviceCountPlanRoundTripsBesideOrdinaryPrewarmRecords()
    {
        using (var list = Open()) CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        using (var list = Open())
        {
            var (ordinary, code) = Assert.Single(list.LoadedComputes());
            Assert.False(ordinary.DeviceRecordCounts);
            list.RecordCompute(code, new ComputePrewarmRecord
            {
                Hash = ordinary.Hash, CodeSize = ordinary.CodeSize, Address = ordinary.Address,
                UserDataBase = ordinary.UserDataBase, UserDataCount = ordinary.UserDataCount,
                PushDataCursor = ordinary.PushDataCursor, Info = ordinary.Info, SystemRegisters = ordinary.SystemRegisters,
                Specialization = ordinary.Specialization, DeviceRecordCounts = true,
            });
        }
        using var reloaded = Open();
        // The second session's record runs first by its earlier use; the two records are what this checks.
        Assert.Equal(new[] { false, true }, reloaded.LoadedComputes().Select(entry => entry.Record.DeviceRecordCounts).Order());
    }

    [Fact]
    public void Coverage_UsesLoadedRecordsAndDoesNotCertifyConcurrentAppends()
    {
        using (var list = Open())
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);

        string coverage;
        using (var list = Open())
        {
            coverage = list.CoverageStamp("build");
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
            Assert.Equal(coverage, list.CoverageStamp("build"));
        }
        using (var list = Open())
        {
            Assert.Equal(coverage, list.CoverageStamp("build"));
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
            Assert.Equal(coverage, list.CoverageStamp("build"));
            list.WriteStamp(ShaderPrewarmList.CacheStamp(coverage, new byte[] { 1, 2, 3 }));
        }
        using var reloaded = Open();
        Assert.NotEqual(coverage, reloaded.CoverageStamp("build"));
        Assert.False(reloaded.IsStampCurrent(ShaderPrewarmList.CacheStamp(
            reloaded.CoverageStamp("build"), new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public void Coverage_RejectsAReplacedRecordingWithTheSameRecordCount()
    {
        using (var list = Open())
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);
        string coverage;
        using (var list = Open())
            coverage = list.CoverageStamp("build");
        File.Delete(Path.Combine(_directory, ShaderPrewarmList.FileName));
        using (var list = Open())
            CompileAtRuntime(list, BufferDescriptorWords.Format32x4UInt);
        using var reloaded = Open();
        Assert.Equal(1, reloaded.LoadedComputeCount);
        Assert.NotEqual(coverage, reloaded.CoverageStamp("build"));
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RecordedSpecializationPreservesAliasesAndMipStatistics(int changedField)
    {
        using (var list = Open())
            CompileAtRuntime(list, BufferDescriptorWords.Format32UInt);

        ResourceSpecialization expected;
        ulong baselineIdentity;
        ulong changedIdentity;
        using (var list = Open())
        {
            var (seed, code) = Assert.Single(list.LoadedComputes());
            var baseline = seed.Specialization.Clone();
            var image = new ImageSpecialization(ImageNumericClass.Float, ImageDimension.Dim2D,
                1, 0, DescriptorConstants.IdentityDestinationSelect, DescriptorConstants.NoIndex, 0, 0, false);
            baseline.Images.Add(image);
            baseline.Images.Add(image);
            expected = changedField == 3 ? new ResourceSpecialization
            {
                BaseBufferCount = baseline.BaseBufferCount, Buffers = [.. baseline.Buffers],
                Images = [.. baseline.Images], StableImageBindings = true,
            } : baseline.Clone();
            switch (changedField)
            {
                case 0: expected.ImageDescriptorGroups.AddRange([0u, 0u]); break;
                case 1: expected.SamplerDescriptorGroups.AddRange([0u, 0u]); break;
                case 2: expected.Images[0] = expected.Images[0] with { MipStatistics = true }; break;
            }

            ComputePrewarmRecord WithSpecialization(ResourceSpecialization specialization) => new()
            {
                Hash = seed.Hash,
                CodeSize = seed.CodeSize,
                Address = seed.Address,
                ContinuationAddressOffset = seed.ContinuationAddressOffset,
                UserDataBase = seed.UserDataBase,
                UserDataCount = seed.UserDataCount,
                PushDataCursor = seed.PushDataCursor,
                Info = seed.Info,
                SystemRegisters = seed.SystemRegisters,
                Specialization = specialization,
            };

            var first = WithSpecialization(baseline);
            var second = WithSpecialization(expected);
            baselineIdentity = ShaderPrewarmList.Identity(first);
            changedIdentity = ShaderPrewarmList.Identity(second);
            Assert.NotEqual(baselineIdentity, changedIdentity);
            list.RecordCompute(code, first);
            list.RecordCompute(code, second);
            list.RecordCompute(code, second);
        }

        using var reloaded = Open();
        var loaded = reloaded.LoadedComputes();
        Assert.Equal(3, loaded.Count); // Seed plus the two distinct variants; duplicate remains deduplicated.
        Assert.Contains(loaded, item => ShaderPrewarmList.Identity(item.Record) == baselineIdentity);
        var restored = Assert.Single(loaded, item => ShaderPrewarmList.Identity(item.Record) == changedIdentity);
        Assert.True(expected.Equals(restored.Record.Specialization));
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
