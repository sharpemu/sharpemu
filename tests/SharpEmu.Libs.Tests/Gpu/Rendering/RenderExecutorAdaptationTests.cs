// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// The draw paths kept beside the executor: solid clears, metadata-only quads, requested clears, retained draws.
[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorAdaptationTests : IDisposable
{
    private const uint FastClearBit = 1u << 13;
    private const uint DccEnabledBit = 1u << 28;
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorAdaptationTests() => _executor = new RenderExecutor(_host, _pipelines);

    public void Dispose() => _fatal.Dispose();

    private static BlendRegisters PremultipliedFill() => new() { Enable = true, ColorSourceFactor = 1, ColorDestinationFactor = 5, ColorFunction = 0 };

    private void WriteQuad(float left, float top, float right, float bottom)
    {
        var bytes = new byte[4 * 12];
        Span<(float X, float Y)> corners = [(left, top), (right, top), (left, bottom), (right, bottom)];
        for (var index = 0; index < 4; index++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(index * 12), corners[index].X);
            BitConverter.TryWriteBytes(bytes.AsSpan((index * 12) + 4), corners[index].Y);
        }

        _host.WriteGuest(VertexBase, bytes);
    }

    [Fact]
    public void UnavailablePrograms_SkipTheDrawAfterTheTargetsAreBound()
    {
        _pipelines.Graphics = new GraphicsPrograms { Available = false };
        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.Contains("bind_target 1", _host.Calls);
        Assert.Contains("reset_bindings", _host.Calls);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("begin_rendering", StringComparison.Ordinal));
        Assert.Empty(_pipelines.PipelineRequests);
    }

    [Fact]
    public void SolidClearPrograms_ClearTheBoundTargetsInsteadOfDrawing()
    {
        _pipelines.Graphics = Programs(solidClear: new SolidColorClear(0.25f, 0.5f, 0.75f, 1f));
        var banks = Banks();
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.RenderTargetMask = 0xFF;
        _executor.DrawAuto(1, banks, Auto(3));

        Assert.Contains("clear_targets 0,1 0.25,0.5,0.75,1", _host.Calls);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("acquire_color", StringComparison.Ordinal));
        Assert.Contains("reset_bindings", _host.Calls);
    }

    [Fact]
    public void SolidClearPrograms_WithoutColorTargets_DrawNormally()
    {
        _pipelines.Graphics = Programs(solidClear: new SolidColorClear(0f, 0f, 0f, 1f));
        var banks = Banks(withDepth: true);
        banks.Context.RenderTargetMask = 0;
        banks.Context.ColorTargets[0] = default;
        _executor.DrawAuto(1, banks, Auto(3));

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("clear_targets", StringComparison.Ordinal));
        Assert.Contains(_host.Calls, call => call.StartsWith("draw 3", StringComparison.Ordinal));
    }

    [Fact]
    public void FastClearEnabled_DoesNotRequestAClearForEitherTargetAcrossDraws()
    {
        var banks = Banks();
        var first = banks.Context.ColorTargets[0];
        banks.Context.ColorTargets[0] = first with { Info = first.Info | FastClearBit };
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.RenderTargetMask = 0xFF;
        _executor.DrawAuto(1, banks, Auto(3));
        _host.EndRendering();
        _executor.DrawAuto(2, banks, Auto(3));

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("request_clear", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("clear_targets", StringComparison.Ordinal));
        Assert.Contains(_host.Calls, call => call.StartsWith("draw 3", StringComparison.Ordinal));
        Assert.Equal(2, _host.BegunRenderings.Count);
        Assert.All(_host.BegunRenderings, rendering =>
        {
            Assert.False(rendering.ColorAttachments[0].IsClear);
            Assert.False(rendering.ColorAttachments[1].IsClear);
        });
    }

    [Fact]
    public void TargetWithoutTheFastClearBit_RequestsNoClear()
    {
        _executor.DrawAuto(1, Banks(), Auto(3));

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("request_clear", StringComparison.Ordinal));
    }

    private RegisterBanks MetadataQuadBanks()
    {
        var banks = Banks(PrimitiveTriangleStrip);
        var words = banks.Context.ColorTargets[0];
        banks.Context.ColorTargets[0] = words with { Info = words.Info | DccEnabledBit };
        banks.Context.BlendControls[0] = PremultipliedFill();
        _pipelines.Graphics = Programs(positionStream: new VertexPositionStream(VertexBase, 12, 0));
        WriteQuad(-1f, -1f, 1f, 1f);
        return banks;
    }

    [Fact]
    public void MetadataClearQuad_IsDroppedWithoutRecording()
    {
        var banks = MetadataQuadBanks();
        _executor.DrawAuto(1, banks, Auto(4));

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("prepare_bindings", StringComparison.Ordinal));
        Assert.Contains("reset_bindings", _host.Calls);
        Assert.Equal(4, _host.GuestReads);
    }

    [Fact]
    public void MetadataClearQuad_ThatDoesNotCoverClipSpace_Draws()
    {
        var banks = MetadataQuadBanks();
        WriteQuad(-1f, -1f, 0.5f, 1f);
        _executor.DrawAuto(1, banks, Auto(4));

        Assert.Contains(_host.Calls, call => call.StartsWith("draw 4", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataClearQuad_WithANonZeroClearWord_Draws()
    {
        var banks = MetadataQuadBanks();
        banks.Context.ColorClearWord1[0] = 1;
        _executor.DrawAuto(1, banks, Auto(4));

        Assert.Contains(_host.Calls, call => call.StartsWith("draw 4", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataClearQuad_WithAnOrdinaryBlend_Draws()
    {
        var banks = MetadataQuadBanks();
        banks.Context.BlendControls[0] = new BlendRegisters { Enable = true, ColorSourceFactor = 4, ColorDestinationFactor = 5 };
        _executor.DrawAuto(1, banks, Auto(4));

        Assert.Contains(_host.Calls, call => call.StartsWith("draw 4", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataClearQuad_WithAnImageInThePixelProgram_Draws()
    {
        var banks = MetadataQuadBanks();
        _pipelines.Graphics = Programs(
            pixelStage: Stage(Program(ShaderStageKind.Pixel, images: [new ImageResourceInfo(ImageResourceClass.Sampled, false)])),
            positionStream: new VertexPositionStream(VertexBase, 12, 0));
        _executor.DrawAuto(1, banks, Auto(4));

        Assert.Contains(_host.Calls, call => call.StartsWith("draw 4", StringComparison.Ordinal));
    }

    private RegisterBanks TargetlessBanks()
    {
        var banks = Banks();
        banks.Context.RenderTargetMask = 0;
        banks.Context.ColorTargets[0] = default;
        banks.Context.ShaderInterface.DepthShaderControl = new DepthShaderControlRegisters { KillEnable = true };
        return banks;
    }

    [Fact]
    public void TargetlessDrawWithImages_IsRetainedWhenTheHostKeepsIt()
    {
        _host.RetainTargetlessDraws = true;
        _pipelines.Graphics = Programs(pixelStage: Stage(Program(ShaderStageKind.Pixel, images: [new ImageResourceInfo(ImageResourceClass.Sampled, false)])));
        _executor.DrawIndexed(9, TargetlessBanks(), Indexed(6));

        Assert.Contains("retain_targetless 9", _host.Calls);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw", StringComparison.Ordinal));
        var (_, _, arguments) = Assert.Single(_host.RetainedDraws);
        Assert.True(arguments.Indexed);
        Assert.Equal(6u, arguments.Indexed_.IndexCount);
    }

    [Fact]
    public void TargetlessDrawWithImages_DrawsWhenTheHostDeclines()
    {
        _pipelines.Graphics = Programs(pixelStage: Stage(Program(ShaderStageKind.Pixel, images: [new ImageResourceInfo(ImageResourceClass.Sampled, false)])));
        _executor.DrawAuto(1, TargetlessBanks(), Auto(3));

        Assert.Contains("retain_targetless 1", _host.Calls);
        Assert.Contains(_host.Calls, call => call.StartsWith("draw 3", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetlessDrawThatWritesAStorageImage_IsNeverRetained()
    {
        _host.RetainTargetlessDraws = true;
        _pipelines.Graphics = Programs(pixelStage: Stage(Program(ShaderStageKind.Pixel, images: [new ImageResourceInfo(ImageResourceClass.Storage, true)])));
        _executor.DrawAuto(1, TargetlessBanks(), Auto(3));

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("retain_targetless", StringComparison.Ordinal));
        Assert.Contains(_host.Calls, call => call.StartsWith("draw 3", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsumedComputeProgram_RecordsNothing()
    {
        _pipelines.Compute = new ComputeProgram { Consumed = true };
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x1);

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("dispatch", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("prepare_bindings", StringComparison.Ordinal));
    }

    [Fact]
    public void UnavailableComputeProgram_SkipsTheDispatch()
    {
        _pipelines.Compute = new ComputeProgram { Available = false };
        _executor.Dispatch(1, Banks(), 4, 1, 1, 0x1);

        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("dispatch", StringComparison.Ordinal));
        Assert.DoesNotContain(_pipelines.Calls, call => call.StartsWith("create_compute", StringComparison.Ordinal));
    }
}
