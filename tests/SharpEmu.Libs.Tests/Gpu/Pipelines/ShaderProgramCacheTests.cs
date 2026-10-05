// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Program identity is the content hash or the declared hash; entries key on static state, permutations on specialization.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderProgramCacheTests : IDisposable
{
    private const ulong CodeA = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong CodeB = PipelineTestGuest.MemoryBase + 0x2000;
    private const ulong HeaderA = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong HeaderB = PipelineTestGuest.MemoryBase + 0x8100;
    private const ulong DataBase = PipelineTestGuest.MemoryBase + 0x4_0000;
    private const uint Format32x4Uint = 75;
    private const uint Format32x4Float = 77;

    [Fact]
    public void EmbeddedFetchIsReplacedBeforeTheProgramRequestsResourceTables()
    {
        _guest.RegisterProgram(CodeA, HeaderA,
            [0xF4080303, 0xFA000000, 0x02020B08, 0xE00C2000, 0x80030401, 0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Vertex, new uint[8]);
        var descriptor = PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Float);
        var options = new StageCompileOptions
        {
            VertexInfo = new VertexInputInfo
            {
                FetchEmbedded = true,
                FetchAttributeRegister = 4,
                FetchBufferRegister = 6,
                Attributes = [new VertexAttributeResource(new(descriptor[0], descriptor[1], descriptor[2], descriptor[3]), 4, 4, 0, 0, 0, 0)],
                Buffers = [new VertexInputBuffer(DataBase, 16, 4)],
            },
        };
        var cursor = 0u;
        _ = _guest.Programs.GetOrCompile(source, options, ref cursor, out var stage);
        var request = Assert.Single(_guest.Compiler.Requests);
        Assert.Single(request.VertexInputs);
        Assert.Empty(request.Memory.Entries);
        Assert.Empty(stage.Resources.FlattenedResourceTable);
        Assert.False(stage.Program!.UsesDeviceAddresses);
        Assert.Equal("SNop", request.Program.Instructions[0].Opcode);
    }

    private readonly FatalScope _fatal = new();
    private readonly PipelineTestGuest _guest = new();

    public void Dispose() => _fatal.Dispose();

    private ShaderProgram Compile(ulong code, uint[] userData, ref uint cursor, out ShaderStageResources stage, uint threadsX = 64) =>
        _guest.Programs.GetOrCompile(_guest.Source(code, ShaderStage.Compute, userData), PipelineTestGuest.ComputeOptions(threadsX), ref cursor, out stage);

    private ShaderProgram Compile(ulong code, uint[]? userData = null, uint threadsX = 64)
    {
        var cursor = 0u;
        return Compile(code, userData ?? [], ref cursor, out _, threadsX);
    }

    // The marker, the block offset, the code, padding, then the binary information block with its hash.
    private static uint[] DeclaredHashProgram(uint hash0, uint hash1) =>
    [
        ShaderBinaryInfo.MarkerWord, 1, 0xBF810000, 0,
        0, 0, 0, 0, hash0, hash1, 0,
    ];

    [Fact]
    public void UndeclaredHash_IsTheContentHash()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var registered = _guest.Registry.Require(CodeA, "compute");

        var hash = ShaderIdentity.Compute(_guest.Memory, CodeA, registered.CodeRanges, "compute");

        Assert.Equal(System.IO.Hashing.XxHash3.HashToUInt64([0x00, 0x00, 0x81, 0xBF]), hash);
        Assert.NotEqual(0UL, hash);
    }

    [Fact]
    public void DeclaredHash_IsTakenFromTheBinaryInfoBlock()
    {
        _guest.RegisterProgram(CodeA, HeaderA, DeclaredHashProgram(0xC0DE0001, 0xBEEF0002));

        Assert.True(ShaderIdentity.TryReadDeclaredHash(_guest.Memory, CodeA, out var declared));
        Assert.Equal(0xBEEF0002_C0DE0001UL, declared);
        var source = _guest.Source(CodeA, ShaderStage.Compute, []);
        Assert.Equal(0xBEEF0002_C0DE0001UL, source.Hash);
    }

    [Fact]
    public void ZeroDeclaredHash_UsesContentHash()
    {
        _guest.RegisterProgram(CodeA, HeaderA, DeclaredHashProgram(0, 0));
        _guest.RegisterProgram(CodeB, HeaderB, [.. DeclaredHashProgram(0, 0)[..10], 1]);

        var first = Compile(CodeA);
        var second = Compile(CodeB);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _guest.Programs.ProgramCount);
        Assert.NotEqual(_guest.Source(CodeA, ShaderStage.Compute, []).Hash, _guest.Source(CodeB, ShaderStage.Compute, []).Hash);
    }

    [Fact]
    public void RewriteAtTheSameAddress_CompilesASecondProgram()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var first = Compile(CodeA);

        _guest.RegisterProgram(CodeA, HeaderA, [0xBF800000, 0xBF810000]);
        var second = Compile(CodeA);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _guest.Programs.ProgramCount);
        Assert.Equal(2, _guest.Host.Modules.Count);
    }

    [Fact]
    public void SameCodeAtTwoAddresses_SharesOneEntry()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        _guest.RegisterProgram(CodeB, HeaderB, PipelineTestGuest.EndProgram);

        var first = Compile(CodeA);
        var second = Compile(CodeB);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, _guest.Programs.ProgramCount);
        Assert.Single(_guest.Host.Modules);
        Assert.Equal(1, _guest.Compiler.Compilations);
    }

    [Fact]
    public void StaticStateChange_IsANewEntryNotAPermutation()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);

        var first = Compile(CodeA, threadsX: 64);
        var second = Compile(CodeA, threadsX: 32);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _guest.Programs.ProgramCount);
        Assert.All(_guest.Programs.Entries, entry => Assert.Single(entry.Permutations));
    }

    [Fact]
    public void SpecializationChange_IsANewPermutationOfTheSameEntry()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);

        var first = Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint));
        var same = Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase + 0x100, 16, 8, Format32x4Uint));
        var second = Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Float));

        Assert.Equal(first.Id, same.Id);
        Assert.NotEqual(first.Id, second.Id);
        var entry = Assert.Single(_guest.Programs.Entries);
        Assert.Equal(2, entry.Permutations.Count);
        Assert.Equal(2, _guest.Compiler.Compilations);
        Assert.Same(entry.Plan, entry.Plan);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(32u)]
    public void DispatchLimitsChangeWithoutRecompilingAndStayWithEachStage(uint startCursor)
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var source = _guest.Source(CodeA, ShaderStage.Compute, []);
        ShaderStageResources Obtain(uint count, out ShaderProgram program)
        {
            var options = new StageCompileOptions
            {
                ComputeInfo = new ComputeInputInfo
                {
                    ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, WaveSize = 32,
                    GroupIdX = true, ThreadIdCount = 1, DispatchThreadDimensions = true,
                    DispatchThreadsX = count, DispatchThreadsY = 1, DispatchThreadsZ = 1,
                },
            };
            var cursor = startCursor;
            program = _guest.Programs.GetOrCompile(source, options, ref cursor, out var stage);
            return stage;
        }

        var first = Obtain(100, out var firstProgram);
        var second = Obtain(4, out var secondProgram);
        Assert.Equal(firstProgram, secondProgram);
        Assert.Single(_guest.Compiler.Requests);
        Assert.Single(_guest.Host.Modules);
        var layout = first.Program!.Bindings!;
        Assert.True(layout.UsesDispatchThreadLimits);
        Assert.Equal(startCursor == 0, layout.UsesPushData);
        var firstData = new uint[layout.ShaderDataDwordCount];
        var secondData = new uint[layout.ShaderDataDwordCount];
        first.WriteDispatchThreadLimits(firstData);
        second.WriteDispatchThreadLimits(secondData);
        Assert.Equal(new uint[] { 100, 1, 1 }, firstData);
        Assert.Equal(new uint[] { 4, 1, 1 }, secondData);
        var missing = first with { ThreadLimits = null };
        Assert.Throws<SchedulerFatalException>(() => missing.WriteDispatchThreadLimits(firstData));
    }

    [Fact]
    public void UserDataChangesBetweenDraws_ChangeTheSnapshotWithoutRecompiling()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);
        var cursor = 0u;

        Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint), ref cursor, out var firstStage);
        cursor = 0;
        Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase + 0x200, 16, 9, Format32x4Uint), ref cursor, out var secondStage);

        Assert.Equal(1, _guest.Compiler.Compilations);
        Assert.Equal(unchecked((uint)DataBase), firstStage.Resources.Buffers[0][0]);
        Assert.Equal(unchecked((uint)(DataBase + 0x200)), secondStage.Resources.Buffers[0][0]);
        Assert.Equal(9u, secondStage.Resources.Buffers[0][2]);
        Assert.Same(firstStage.Program, secondStage.Program);
    }

    [Fact]
    public void PushCursorChange_IsANewPermutation()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);
        var descriptor = PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint);
        var cursor = 0u;
        var first = Compile(CodeA, descriptor, ref cursor, out var firstStage);
        var firstEnd = cursor;

        cursor = 3;
        var second = Compile(CodeA, descriptor, ref cursor, out var secondStage);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, Assert.Single(_guest.Programs.Entries).Permutations.Count);
        Assert.Equal(0u, firstStage.Program!.Bindings!.PushDataStartDword);
        Assert.Equal(3u, secondStage.Program!.Bindings!.PushDataStartDword);
        Assert.Equal(firstStage.Program.Bindings.ShaderDataDwordCount, firstEnd);
        Assert.Equal(3u + secondStage.Program.Bindings.ShaderDataDwordCount, cursor);
    }

    [Fact]
    public void SamePushCursor_ReusesThePermutation()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);
        var descriptor = PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint);
        var cursor = 2u;
        var first = Compile(CodeA, descriptor, ref cursor, out _);

        cursor = 2;
        var second = Compile(CodeA, descriptor, ref cursor, out _);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(Assert.Single(_guest.Programs.Entries).Permutations);
    }

    [Fact]
    public void MissingHeader_IsFatalWithStageAndAddress()
    {
        var fatal = Assert.Throws<SchedulerFatalException>(() => _guest.Registry.Require(CodeA, "pixel"));

        Assert.Contains("label=pixel", fatal.Message);
        Assert.Contains($"shader=0x{CodeA:X16}", fatal.Message);
    }

    [Fact]
    public void ZeroOrUnalignedCodeSize_IsFatal()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        _guest.Write(HeaderA + 0x44, [0x02, 0x00, 0x00, 0x00]);

        var fatal = Assert.Throws<SchedulerFatalException>(() => _guest.Registry.Require(CodeA, "compute"));

        Assert.Contains("invalid code size", fatal.Message);
        Assert.Contains("size=0x00000002", fatal.Message);
    }

    [Fact]
    public void ProgramIds_AreMonotonicAcrossEntries()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        _guest.RegisterProgram(CodeB, HeaderB, [0xBF800000, 0xBF810000]);

        var first = Compile(CodeA);
        var second = Compile(CodeB);

        Assert.Equal(1UL, first.Id);
        Assert.Equal(2UL, second.Id);
        Assert.Equal([(ShaderStage.Compute, _guest.Source(CodeA, ShaderStage.Compute, []).Hash, 1UL), (ShaderStage.Compute, _guest.Source(CodeB, ShaderStage.Compute, []).Hash, 2UL)], _guest.Host.Modules);
    }
}
