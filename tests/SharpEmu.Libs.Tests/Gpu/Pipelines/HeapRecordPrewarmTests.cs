// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A program whose image table is too large to bind is planned again with heap records (HeapRecordReplanTests). Its
// prewarm record keeps that, so a later launch compiles the plan the draw uses, and the draw plans it so from the start.
[Collection(SchedulingStateCollection.Name)]
public sealed class HeapRecordPrewarmTests : IDisposable
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
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _fatal.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static uint Low(ulong value) => unchecked((uint)value);

    private static uint High(ulong value) => (uint)(value >> 32);

    private static uint[] UserData() =>
        [Low(Srt), High(Srt), 0x880, 0x00FFF000, 0x0A503F44, 0, 0x880, 0x00FFF000, 0x0A503F44, 0];

    private static void WriteScene(PipelineTestGuest guest, uint materialCount)
    {
        guest.WriteWords(Srt + 32,
            Low(Materials), High(Materials) | (MaterialBytes << 16), materialCount, 0x00FFF000,
            Low(Heap), High(Heap), materialCount * 32, 0x00FFF000);
        var records = new uint[materialCount * (MaterialBytes / 4)];
        for (uint material = 0; material < materialCount; material++)
        {
            records[material * (MaterialBytes / 4) + KeyOffset / 4] = material;
        }

        guest.WriteWords(Materials, records);
        for (uint key = 0; key < materialCount; key++)
        {
            guest.WriteWords(Heap + key * 32, 0x2000 + key, Format32Float << 20, 0, 0xFAC | (ImageType2D << 28), 0, 0, 0, 0);
        }
    }

    private static StageCompileOptions PixelOptions() => new()
    {
        PixelInfo = new PixelInputInfo(),
        PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
    };

    // One launch: the material table pixel program drawn with a table of the given size. Returns the host, what the
    // draw's compile saw, and what the launch wrote to the error stream.
    private (PipelineTestGuest Guest, ShaderCompileRequest Request, byte[] Spirv, string Log) Launch(ShaderPrewarmList list, uint materialCount)
    {
        var guest = new PipelineTestGuest(Compile);
        guest.Host.SupportsBindlessHeap = true;
        guest.Host.ShaderPrewarm = list;
        guest.RegisterProgram(Code, Header, CapturedShaders.MaterialTablePixelShader);
        WriteScene(guest, materialCount);
        var original = Console.Error;
        var log = new StringWriter();
        Console.SetError(log);
        try
        {
            var cursor = 0u;
            Assert.True(guest.Programs.TryGetProgram(guest.Source(Code, ShaderStage.Pixel, UserData()), PixelOptions(),
                strictShaders: false, ref cursor, out _, out _, out var rejection), rejection);
        }
        finally
        {
            Console.SetError(original);
        }

        return (guest, Assert.Single(guest.Compiler.Requests), Assert.Single(guest.Compiler.Shaders).Spirv, log.ToString());
    }

    private const string ReplanLog = "reads its image descriptors from the bindless heap";

    [Fact]
    public void AReplannedProgramIsRecordedForThePrewarmWithItsHeapPlan()
    {
        using (var list = Open())
        {
            var (_, request, _, log) = Launch(list, materialCount: 200);
            Assert.True(request.Resources.Info.UsesBindlessHeap);
            Assert.Contains(ReplanLog, log);
        }

        using var reloaded = Open();
        var (record, code, _) = Assert.IsType<PixelPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.True(record.PreferHeapRecords);
        Assert.True(reloaded.PrefersHeapRecords(record.Hash, record.CodeSize, record.ContinuationAddressOffset));
        Assert.Equal(code.Hash, record.Hash);
    }

    [Fact]
    public void AProgramWhoseTableFitsIsRecordedWithoutTheFlag()
    {
        using (var list = Open())
        {
            var (_, request, _, _) = Launch(list, materialCount: 8);
            Assert.False(request.Resources.Info.UsesBindlessHeap);
        }

        using var reloaded = Open();
        var (record, _, _) = Assert.IsType<PixelPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.False(record.PreferHeapRecords);
        Assert.False(reloaded.PrefersHeapRecords(record.Hash, record.CodeSize, record.ContinuationAddressOffset));
    }

    // The prewarm compiles the plan the draw ran, so the draw finds it: the same request and the same translation.
    [Fact]
    public void ThePrewarmReplaysAFlaggedRecordWithTheDrawsPlan()
    {
        byte[] runtime;
        IShaderPipelineHost host;
        using (var list = Open())
        {
            var (guest, _, spirv, _) = Launch(list, materialCount: 200);
            host = guest.Host;
            runtime = spirv;
        }

        using var reloaded = Open();
        var (record, code, _) = Assert.IsType<PixelPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        var compiler = new FakeShaderCompiler(Compile);
        Assert.True(ShaderProgramCache.TryCompilePixelPrewarm(record, code, compiler, host, out var compiled, out var layout, out var error), error);
        Assert.NotNull(layout);
        Assert.True(Assert.Single(compiler.Requests).Resources.Info.UsesBindlessHeap);
        Assert.Equal(runtime, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
    }

    // A launch after the recording plans the program with heap records at once: it neither fails to materialize it nor
    // plans it twice, and compiles the translation the first launch (and the prewarm) did.
    [Fact]
    public void ALaterLaunchPlansAKnownProgramWithHeapRecordsFromTheStart()
    {
        byte[] first;
        using (var list = Open())
        {
            first = Launch(list, materialCount: 200).Spirv;
        }

        using var reloaded = Open();
        var (_, request, spirv, log) = Launch(reloaded, materialCount: 200);
        Assert.True(request.Resources.Info.UsesBindlessHeap);
        Assert.DoesNotContain(ReplanLog, log);
        Assert.Equal(first, spirv);
    }

    [Fact]
    public void AHostWithoutTheHeapKeepsPlanningWithoutHeapRecords()
    {
        using (var list = Open())
        {
            Launch(list, materialCount: 200);
        }

        using var reloaded = Open();
        var guest = new PipelineTestGuest(Compile);
        guest.Host.SupportsBindlessHeap = false;
        guest.Host.ShaderPrewarm = reloaded;
        guest.RegisterProgram(Code, Header, CapturedShaders.MaterialTablePixelShader);
        WriteScene(guest, 8);
        var cursor = 0u;
        Assert.True(guest.Programs.TryGetProgram(guest.Source(Code, ShaderStage.Pixel, UserData()), PixelOptions(),
            strictShaders: false, ref cursor, out _, out _, out var rejection), rejection);
        Assert.False(Assert.Single(guest.Compiler.Requests).Resources.Info.UsesBindlessHeap);
    }

    private static ComputePrewarmRecord ComputeRecord(bool deviceRecordCounts, bool preferHeapRecords) => new()
    {
        Hash = 0xC0DE, CodeSize = 12, Address = Code, UserDataBase = 0, UserDataCount = 4, PushDataCursor = 0,
        Info = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, WaveSize = 64 },
        Specialization = new ResourceSpecialization(),
        DeviceRecordCounts = deviceRecordCounts,
        PreferHeapRecords = preferHeapRecords,
    };

    private static ShaderCodeCapture Capture(ulong hash, ulong continuationOffset = 0) => new()
    {
        Hash = hash, CodeSize = 12, Address = Code, Generation = Generation.Gen5,
        Fused = continuationOffset == 0 ? null : new FusedCodeParts(Header, Code + continuationOffset, Header + 0x100),
        Ranges = [new CodeRange(Code, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])],
    };

    // The compute extension byte is a bit set: 1, the only value written before the flag existed, still means device
    // record counts.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AComputeRecordKeepsItsHeapFlagBesideItsDeviceCountFlag(bool deviceRecordCounts, bool preferHeapRecords)
    {
        var record = ComputeRecord(deviceRecordCounts, preferHeapRecords);
        using (var list = Open()) list.RecordCompute(Capture(record.Hash), record);

        using var reloaded = Open();
        var (loaded, _) = Assert.Single(reloaded.LoadedComputes());
        Assert.Equal(deviceRecordCounts, loaded.DeviceRecordCounts);
        Assert.Equal(preferHeapRecords, loaded.PreferHeapRecords);
        Assert.Equal(preferHeapRecords, reloaded.PrefersHeapRecords(record.Hash, record.CodeSize, 0));
        Assert.Equal(ShaderPrewarmList.Identity(record), ShaderPrewarmList.Identity(loaded));
    }

    [Fact]
    public void TheHeapFlagChangesARecordsIdentityOnlyWhenSet()
    {
        var plain = ComputeRecord(deviceRecordCounts: true, preferHeapRecords: false);
        Assert.NotEqual(ShaderPrewarmList.Identity(plain), ShaderPrewarmList.Identity(ComputeRecord(true, true)));

        // The bytes of a record without the flag are what an earlier build wrote: the flag adds nothing to them.
        Assert.Equal(ShaderPrewarmList.Identity(plain), ShaderPrewarmList.Identity(ComputeRecord(true, preferHeapRecords: false)));
    }

    [Fact]
    public void ThePixelAndVertexSchemasIgnoreTheHeapFlagSoRecordedListsStillLoad()
    {
        Assert.DoesNotContain(nameof(PixelPrewarmRecord.PreferHeapRecords),
            ShaderPrewarmList.SchemaText(typeof(PixelPrewarmRecord), typeof(PixelInputInfo)));
        Assert.DoesNotContain(nameof(VertexPrewarmRecord.PreferHeapRecords),
            ShaderPrewarmList.SchemaText(typeof(VertexPrewarmRecord), typeof(VertexInputInfo)));
    }

    [Theory]
    [InlineData(-1, -1, false)]
    [InlineData(-1, -1, true)]
    [InlineData(5, -1, false)]
    [InlineData(5, -1, true)]
    [InlineData(5, 6, false)]
    [InlineData(5, 6, true)]
    public void AVertexRecordKeepsItsHeapFlagBesideItsDrawRegisters(int startInstance, int baseVertex, bool preferHeapRecords)
    {
        var code = Capture(0x7E57);
        var record = new VertexPrewarmRecord
        {
            Hash = code.Hash, CodeSize = code.CodeSize, Address = code.Address, UserDataBase = 0, UserDataCount = 8, PushDataCursor = 0,
            VertexInfo = new VertexInputInfo { StartInstanceUserData = startInstance, BaseVertexUserData = baseVertex },
            RequiredVertexOutputCount = 2,
            Specialization = new ResourceSpecialization(),
            PreferHeapRecords = preferHeapRecords,
        };
        var state = new PreRasterizationState(0, 2, 1, false, false, true, false);
        using (var list = Open()) list.RecordPreRasterization(list.RecordVertex(code, record), state);

        using var reloaded = Open();
        var item = Assert.IsType<PreRasterizationPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.Equal(preferHeapRecords, item.Program.PreferHeapRecords);
        Assert.Equal(startInstance, item.Program.VertexInfo!.StartInstanceUserData);
        Assert.Equal(baseVertex, item.Program.VertexInfo.BaseVertexUserData);
        Assert.Equal(preferHeapRecords, reloaded.PrefersHeapRecords(code.Hash, code.CodeSize, 0));
    }

    [Fact]
    public void AStageRecordKeepsItsHeapFlag()
    {
        var code = Capture(0x57A6);
        var record = new StagePrewarmRecord
        {
            Stage = ShaderStage.Pixel,
            CodeKey = code.Key,
            Registered = new RegisteredShader(code.Address, 0, code.CodeSize, 0, 0, 0, 0, 0, 0),
            Hash = code.Hash,
            UserDataBase = 0,
            UserDataCount = 4,
            PushDataCursor = 0,
            Options = PixelOptions(),
            Specialization = new ResourceSpecialization(),
            PreferHeapRecords = true,
        };
        using (var list = Open()) list.RecordStage(code, module: 1, record);

        using var reloaded = Open();
        Assert.True(reloaded.PrefersHeapRecords(code.Hash, code.CodeSize, 0));
        Assert.False(reloaded.PrefersHeapRecords(code.Hash + 1, code.CodeSize, 0));
    }
}
