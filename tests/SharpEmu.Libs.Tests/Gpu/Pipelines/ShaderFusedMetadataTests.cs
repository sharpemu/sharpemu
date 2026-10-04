// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderFusedMetadataTests : IDisposable
{
    private const ulong EntryCode = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong ContinuationCode = PipelineTestGuest.MemoryBase + 0x2000;
    private const ulong EntryHeader = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong ContinuationHeader = PipelineTestGuest.MemoryBase + 0x8100;
    private const ulong EntryUserData = PipelineTestGuest.MemoryBase + 0x9000;
    private const ulong ContinuationUserData = 0x1122_3344_5566_7788;

    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    [Fact]
    public void RegistryRetainsBothHalvesRuntimeMetadata()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(
            EntryCode,
            EntryHeader,
            PipelineTestGuest.EndProgram,
            userDataAddress: EntryUserData,
            scratchDwords: 7);
        guest.RegisterProgram(
            ContinuationCode,
            ContinuationHeader,
            PipelineTestGuest.EndProgram,
            userDataAddress: ContinuationUserData,
            scratchDwords: 19);
        var registry = new ShaderHeaderRegistry(
            guest.Context,
            code => code switch
            {
                EntryCode => EntryHeader,
                ContinuationCode => ContinuationHeader,
                _ => 0,
            },
            code => code == EntryCode
                ? new FusedProgramParts(ContinuationCode, ContinuationHeader)
                : null);

        var registered = registry.Require(EntryCode, "vertex");

        Assert.True(registered.IsFused);
        Assert.Equal(7u, registered.ScratchDwords);
        Assert.Equal(EntryUserData, registered.UserDataAddress);
        Assert.Equal(ContinuationCode, registered.ContinuationAddress);
        Assert.Equal(ContinuationHeader, registered.ContinuationHeaderAddress);
        Assert.Equal(19u, registered.ContinuationScratchDwords);
        Assert.Equal(ContinuationUserData, registered.ContinuationUserDataAddress);
        Assert.Equal(19u, registered.MaximumScratchDwords);
    }

    [Fact]
    public void FusedVertexUserDataFallsBackToTheHeaderAddress()
    {
        var registered = new RegisteredShader(
            EntryCode,
            EntryHeader,
            4,
            7,
            EntryUserData,
            0,
            0,
            ContinuationCode,
            4)
        {
            ContinuationHeaderAddress = ContinuationHeader,
            ContinuationScratchDwords = 19,
            ContinuationUserDataAddress = ContinuationUserData,
        };
        var vertex = new VertexStageRegisters
        {
            GeometryResource2 = new GeometryResource2 { UserScalarCount = 3 },
        };
        vertex.GeometryUserScalars.Set(0, 0xA0, UserScalarKind.Unknown);
        vertex.GeometryUserScalars.Set(1, 0xA1, UserScalarKind.Unknown);
        vertex.GeometryUserScalars.Set(2, 0xA2, UserScalarKind.Unknown);
        vertex.GeometryUserScalars.Set(3, 0xDEAD, UserScalarKind.Unknown);
        vertex.ExportUserScalars.Set(0, 0xBADC_0DE, UserScalarKind.Unknown);

        var (userData, userDataBase) = ShaderPipelineCache.PrepareVertexUserData(registered, vertex);

        Assert.Equal(0u, userDataBase);
        Assert.Equal(
            [
                0x5566_7788u,
                0x1122_3344u,
                0u, 0u, 0u, 0u, 0u, 0u,
                0xA0u, 0xA1u, 0xA2u,
            ],
            userData);
    }

    [Fact]
    public void FusedVertexUserDataPrefersTheLiveGeometryTableAddress()
    {
        const ulong liveAddress = 0x8877_6655_4433_2211;
        var registered = new RegisteredShader(
            EntryCode,
            EntryHeader,
            4,
            7,
            EntryUserData,
            0,
            0,
            ContinuationCode,
            4)
        {
            ContinuationHeaderAddress = ContinuationHeader,
            ContinuationScratchDwords = 19,
            ContinuationUserDataAddress = ContinuationUserData,
        };
        var vertex = new VertexStageRegisters
        {
            GeometryUserDataAddress = liveAddress,
        };

        var (userData, userDataBase) = ShaderPipelineCache.PrepareVertexUserData(registered, vertex);

        Assert.Equal(0u, userDataBase);
        Assert.Equal(0x4433_2211u, userData[0]);
        Assert.Equal(0x8877_6655u, userData[1]);
    }

    [Fact]
    public void NonFusedVertexUserDataKeepsTheExistingStageRelativeLayout()
    {
        var registered = new RegisteredShader(EntryCode, EntryHeader, 4, 7, EntryUserData, 0, 0, 0, 0);
        var vertex = new VertexStageRegisters
        {
            GeometryResource2 = new GeometryResource2 { UserScalarCount = 2 },
        };
        vertex.GeometryUserScalars.Set(0, 0xA0, UserScalarKind.Unknown);
        vertex.GeometryUserScalars.Set(1, 0xA1, UserScalarKind.Unknown);
        vertex.GeometryUserScalars.Set(2, 0xDEAD, UserScalarKind.Unknown);

        var (userData, userDataBase) = ShaderPipelineCache.PrepareVertexUserData(registered, vertex);

        Assert.Equal(8u, userDataBase);
        Assert.Equal([0xA0u, 0xA1u], userData);
    }
}
