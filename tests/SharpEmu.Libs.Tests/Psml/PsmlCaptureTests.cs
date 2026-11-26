// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Psml;
using Xunit;

namespace SharpEmu.Libs.Tests.Psml;

[CollectionDefinition("PSML capture", DisableParallelization = true)]
public sealed class PsmlCaptureCollection;

[Collection("PSML capture")]
public sealed class PsmlCaptureTests : IDisposable
{
    private const string CaptureVariable = "SHARPEMU_PSSR_CAPTURE";
    private const ulong MemoryBase = 0x1000;
    private readonly bool _previousPro = ConsoleHardware.IsPro;
    private readonly string? _previousCapture = Environment.GetEnvironmentVariable(CaptureVariable);
    private readonly CpuContext _cpu = new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    public PsmlCaptureTests()
    {
        ConsoleHardware.Configure(pro: true);
        Environment.SetEnvironmentVariable(CaptureVariable, "1");
    }

    public void Dispose()
    {
        ConsoleHardware.Configure(_previousPro);
        Environment.SetEnvironmentVariable(CaptureVariable, _previousCapture);
    }

    [Fact]
    public void GuestCaptureTracksItsContextUntilTheLastReadbackCompletes()
    {
        var (handle, id) = CreateContext();
        var (otherHandle, otherId) = CreateContext();
        try
        {
            Assert.Equal(unchecked((int)0x8A810005), Request(0, 1));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Request(handle, 0));
            Assert.Equal(0, Request(handle, 2));
            Assert.Equal(1, InProgress(handle));
            Assert.Equal(0, InProgress(otherHandle));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Request(otherHandle, 1));
            Assert.Null(PsmlExports.BeginCaptureFrame(otherId));

            var first = PsmlExports.BeginCaptureFrame(id);
            var second = PsmlExports.BeginCaptureFrame(id);
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Null(PsmlExports.BeginCaptureFrame(id));
            first.Value.Complete();
            Assert.Equal(1, InProgress(handle));
            second.Value.Complete();
            Assert.Equal(0, InProgress(handle));

            Assert.Equal(0, Request(handle, 2));
            var pending = PsmlExports.BeginCaptureFrame(id);
            Assert.NotNull(pending);
            Release(handle);
            Assert.Null(PsmlExports.BeginCaptureFrame(id));
            Assert.Equal(0, InProgress(handle));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Request(otherHandle, 1));
            pending.Value.Complete();
            Assert.Equal(0, Request(otherHandle, 1));
        }
        finally
        {
            Release(handle);
            Release(otherHandle);
        }
    }

    [Theory]
    [InlineData(false, "1")]
    [InlineData(true, null)]
    public void GuestCaptureRequiresProAndTheDiagnosticSetting(bool pro, string? setting)
    {
        ConsoleHardware.Configure(pro);
        Environment.SetEnvironmentVariable(CaptureVariable, setting);
        var (handle, id) = CreateContext();
        try
        {
            PsmlExports.RequestCaptureFromWindow();
            Assert.Equal(0, Request(handle, 1));
            Assert.Equal(0, InProgress(handle));
            Assert.Null(PsmlExports.BeginCaptureFrame(id));
        }
        finally
        {
            Release(handle);
        }
    }

    private (ulong Handle, uint Id) CreateContext()
    {
        _cpu[CpuRegister.Rdi] = MemoryBase;
        _cpu[CpuRegister.Rsi] = MemoryBase + 0x100;
        Assert.Equal(0, PsmlExports.PsmlMfsrCreateContext1100(_cpu));
        Assert.True(_cpu.TryReadUInt64(MemoryBase, out var handle));
        Assert.True(_cpu.TryReadUInt32(handle + sizeof(uint), out var id));
        return (handle, id);
    }

    private int Request(ulong handle, byte frames)
    {
        _cpu[CpuRegister.Rdi] = handle;
        _cpu[CpuRegister.Rsi] = frames;
        return PsmlExports.PsmlMfsrRequestCapture(_cpu);
    }

    private int InProgress(ulong handle)
    {
        _cpu[CpuRegister.Rdi] = handle;
        return PsmlExports.PsmlMfsrIsCaptureInProgress(_cpu);
    }

    private void Release(ulong handle)
    {
        _cpu[CpuRegister.Rdi] = handle;
        PsmlExports.PsmlMfsrReleaseContext(_cpu);
    }
}
