// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A material table selects its image through a record the shader indexes at run time. While the table is small the
// planner binds every image it can select, as it always did; when it selects more than a draw can bind, a host that
// can serve runtime descriptors lets the shader read them from its registers, and any other host rejects the draw.
[Collection(SchedulingStateCollection.Name)]
public sealed class RuntimeDescriptorRoutingTests : IDisposable
{
    private const ulong Code = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong Srt = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong Materials = PipelineTestGuest.MemoryBase + 0x2_0000;
    private const ulong Heap = PipelineTestGuest.MemoryBase + 0x8_0000;
    private const uint MaterialBytes = 0x74;
    private const uint KeyOffset = 60;
    private const uint ImageType2D = 9;
    private const uint Format32Float = 22;

    private readonly FatalScope _fatal = new();
    private readonly PipelineTestGuest _guest = new();

    public void Dispose() => _fatal.Dispose();

    private static uint Low(ulong value) => unchecked((uint)value);

    private static uint High(ulong value) => (uint)(value >> 32);

    // The shader's user data: the pointer to its resource table, then two sampler descriptors.
    private static uint[] UserData() =>
        [Low(Srt), High(Srt), 0x880, 0x00FFF000, 0x0A503F44, 0, 0x880, 0x00FFF000, 0x0A503F44, 0];

    // The resource table the shader reads at fixed offsets (+32: the material V#, +48: the heap V#), the material
    // records, and the heap of T#s they select by key.
    private void WriteScene(uint materialCount, uint declaredCount = 0)
    {
        // The V#s may declare more records than the guest has mapped, as a game's pools do.
        var declared = declaredCount == 0 ? materialCount : declaredCount;
        _guest.WriteWords(Srt + 32,
            Low(Materials), High(Materials) | (MaterialBytes << 16), declared, 0x00FFF000,
            Low(Heap), High(Heap), declared * 32, 0x00FFF000);
        var records = new uint[materialCount * (MaterialBytes / 4)];
        for (uint material = 0; material < materialCount; material++)
        {
            records[material * (MaterialBytes / 4) + KeyOffset / 4] = material;
        }

        _guest.WriteWords(Materials, records);
        for (uint key = 0; key < materialCount; key++)
        {
            // Every record holds a different image, so no two candidates share a binding.
            _guest.WriteWords(Heap + key * 32, 0x2000 + key, Format32Float << 20, 0, 0xFAC | (ImageType2D << 28), 0, 0, 0, 0);
        }
    }

    private static StageCompileOptions PixelOptions() => new()
    {
        PixelInfo = new PixelInputInfo(),
        PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
    };

    private bool TryCompile(out ShaderStageResources stage, out string rejection)
    {
        _guest.RegisterProgram(Code, Header, CapturedShaders.MaterialTablePixelShader);
        var source = _guest.Source(Code, ShaderStage.Pixel, UserData());
        var cursor = 0u;
        return _guest.Programs.TryGetProgram(source, PixelOptions(), strictShaders: false, ref cursor, out _, out stage, out rejection);
    }

    [Fact]
    public void ATableThatFitsStaysOnItsPlannedBinding()
    {
        _guest.Host.RuntimeImageDescriptorsSupported = true;
        WriteScene(materialCount: 8);

        Assert.True(TryCompile(out _, out var rejection), rejection);

        var request = Assert.Single(_guest.Compiler.Requests);
        Assert.False(request.Resources.Info.UsesRuntimeDescriptors);
        Assert.Null(request.Bindings.Find(DescriptorBindingKind.RuntimeDescriptorTable));
        Assert.True(request.Resources.Info.Images.Count > 1);
    }

    [Fact]
    public void ATableTooLargeToBindIsRejectedWithoutARuntimeHost()
    {
        _guest.Host.RuntimeImageDescriptorsSupported = false;
        WriteScene(materialCount: 200);

        Assert.False(TryCompile(out _, out var rejection));

        Assert.Contains("ImageCapacityExceeded", rejection);
        Assert.Empty(_guest.Compiler.Requests);
    }

    [Fact]
    public void ATableTooLargeToBindIsReadAtRunTimeWhenTheHostCanServeIt()
    {
        _guest.Host.RuntimeImageDescriptorsSupported = true;
        WriteScene(materialCount: 200);

        Assert.True(TryCompile(out var stage, out var rejection), rejection);

        var request = Assert.Single(_guest.Compiler.Requests);
        Assert.True(request.Resources.Info.UsesRuntimeDescriptors);
        Assert.NotNull(request.Bindings.Find(DescriptorBindingKind.RuntimeDescriptorTable));
        Assert.NotNull(request.Bindings.Find(DescriptorBindingKind.RuntimeDescriptorMisses));
        Assert.Contains(request.Memory.Entries, memory => memory.RuntimeDescriptor);
        // The material table is no longer enumerated: nothing but the one fixed image is bound.
        Assert.True(request.Resources.Info.Images.Count < 8);
        Assert.Equal(request.Resources.Info.Images.Count, stage.Resources.Images.Length);

        // The program keeps its runtime plan: another draw finds the permutation, whatever the table holds now.
        WriteScene(materialCount: 3);
        Assert.True(TryCompile(out _, out rejection), rejection);
        Assert.Single(_guest.Compiler.Requests);
    }

    // The V#s declare far more records than the guest ever fills, as a game's pool does: the table cannot be enumerated.
    [Fact]
    public void ATableWhoseDeclaredRecordsCannotAllBeEnumeratedIsReadAtRunTimeWhenTheHostCanServeIt()
    {
        const uint declared = 200_000;
        _guest.Host.RuntimeImageDescriptorsSupported = true;
        WriteScene(materialCount: 200, declaredCount: declared);

        Assert.True(TryCompile(out _, out var rejection), rejection);

        var request = Assert.Single(_guest.Compiler.Requests);
        Assert.True(request.Resources.Info.UsesRuntimeDescriptors);
    }

    [Fact]
    public void ATableThatCannotBeEnumeratedIsRejectedWithItsReasonWithoutARuntimeHost()
    {
        const uint declared = 200_000;
        const string kind = "ImageCapacityExceeded";
        const string detail = "selector probes exceed";
        _guest.Host.RuntimeImageDescriptorsSupported = false;
        WriteScene(materialCount: 200, declaredCount: declared);

        Assert.False(TryCompile(out _, out var rejection));

        Assert.Contains(kind, rejection);
        Assert.Contains("detail=", rejection);
        Assert.Contains(detail, rejection);
    }
}
