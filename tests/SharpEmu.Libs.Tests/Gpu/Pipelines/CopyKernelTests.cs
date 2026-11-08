// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Evaluation;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class CopyKernelTests
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong SourceAddress = PipelineTestGuest.MemoryBase + 0x20000;
    private const ulong DestinationAddress = PipelineTestGuest.MemoryBase + 0x30000;
    private const ulong ControlAddress = PipelineTestGuest.MemoryBase + 0xF0000;
    private const uint Untouched = 0xEEEEEEEE;

    private sealed class DescriptorFrontend(RegisteredShader shader, bool normalized) : IShaderFrontend
    {
        public ulong CacheDomain => 1;
        public BufferDescriptorNormalizer? NormalizeBufferDescriptor => normalized ? words => words.ToArray() : null;
        public RegisteredShader ReadShader(CpuContext context, ulong codeAddress, string label, ReadOnlySpan<uint> userData = default) =>
            shader with { HeaderAddress = 0, Frontend = this };

        public bool TryDecode(CpuContext context, RegisteredShader registered, out Gen5ShaderProgram program, out string error) =>
            Gen5ShaderTranslator.TryDecodeProgram(context, registered.CodeAddress, out program, out error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KernelPredictionDoesNotInterpretDescriptorsThatRequireFrontendNormalization(bool normalized)
    {
        using var fatal = new FatalScope();
        var guest = Guest(4, 2, [3, 4]);
        var frontend = new DescriptorFrontend(guest.Registry.Require(CodeAddress, "compute"), normalized);
        var registry = new ShaderHeaderRegistry(guest.Context, _ => 0, _ => null, frontend);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, registry);

        var result = cache.GetComputeProgram(Registers(2, 64), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);

        Assert.True(result.Available);
        Assert.False(result.Consumed);
        Assert.Equal(normalized, result.KnownWrite is null);
        Assert.Equal(Untouched, Destination(guest, 1)[0]);
    }

    private static uint[] DwordBuffer(ulong address, uint records) =>
        [unchecked((uint)address), 0x0004_0000 | (uint)(address >> 32), records, 0x0001_4004];

    private static ComputeStageRegisters Registers(uint sourceRecords, uint destinationRecords, uint[]? control = null)
    {
        var registers = new ComputeStageRegisters
        {
            Address = CodeAddress, ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, UserScalarCount = 12, ThreadGroupIdXEnable = true,
            ThreadIdComponentCount = 1,
        };
        uint[] words =
        [
            .. DwordBuffer(SourceAddress, sourceRecords),
            .. DwordBuffer(DestinationAddress, destinationRecords),
            .. control ?? [unchecked((uint)ControlAddress), 0x0010_0000 | (uint)(ControlAddress >> 32), 1, 0x0004_DFAC],
        ];
        for (var index = 0u; index < words.Length; index++)
        {
            registers.UserScalars.Set(index, words[index], default);
        }

        return registers;
    }

    private static uint[] Destination(PipelineTestGuest guest, int count)
    {
        var bytes = new byte[count * sizeof(uint)];
        Assert.True(guest.Memory.TryRead(DestinationAddress, bytes));
        var words = new uint[count];
        for (var index = 0; index < count; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)));
        }

        return words;
    }

    private static uint[] Kernel(string name) => name switch
    {
        "104" => EvaluationTestSupport.ModuloKernel104,
        "107" => EvaluationTestSupport.ModuloKernel107,
        _ => EvaluationTestSupport.MaskedKernel,
    };

    private static PipelineTestGuest Guest(uint elementCount, uint parameter, uint[] source, string kernel = "104")
    {
        var guest = new PipelineTestGuest();
        guest.Host.ResidentReads = true;
        guest.Host.ShaderSignedZeroInfNanPreserveFloat32Supported = true;
        guest.RegisterProgram(CodeAddress, HeaderAddress, Kernel(kernel));
        guest.WriteWords(SourceAddress, source);
        guest.WriteWords(ControlAddress, elementCount, parameter, 0, 0);
        guest.WriteWords(DestinationAddress, Enumerable.Repeat(Untouched, 200).ToArray());
        return guest;
    }

    [Theory]
    [InlineData("104", 130u, 7u, 3u, false)]
    [InlineData("104", 100u, 12u, 70u, true)]
    [InlineData("107", 10u, 3u, 2u, false)]
    [InlineData("107", 160u, 6u, 131u, true)]
    public void TheModuloCopyKernels_StayOnTheGpuAndTheirResultIsKnown(string kernel, uint elementCount, uint period, uint dimension, bool threadDimensions)
    {
        var source = Enumerable.Range(0, 80).Select(index => 0x1000u + (uint)index).ToArray();
        var guest = Guest(elementCount, period, source, kernel);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(
            Registers(80, 160), new ShaderInterfaceRegisters(), threadDimensions ? 0x8021u : 0x8001u, dimension, 1, 1);

        Assert.False(result.Consumed);
        Assert.True(result.Available);
        Assert.Equal(Untouched, Destination(guest, 1)[0]);
        Assert.True(result.KnownWrite is not null, ShaderPipelineCache.TakeCpuKernelReport());
        var known = result.KnownWrite!;
        Assert.Equal(DestinationAddress, known.Address);
        var written = (int)Math.Min(elementCount, threadDimensions ? dimension : dimension * 64);
        Assert.Equal(written, known.Words.Length);
        for (var index = 0; index < written; index++)
        {
            var sourceIndex = period == 0 ? index : index % (int)period;
            Assert.Equal(source[sourceIndex], known.Words[index]);
        }
    }

    [Theory]
    [InlineData(0x8001u, 1u, 10)]
    [InlineData(0x8021u, 64u, 10)]
    [InlineData(0x0021u, 7u, 7)]
    public void TheMaskedCopyKernel_StaysOnTheGpuAndItsResultIsKnown(uint initiator, uint dimension, int written)
    {
        var guest = Guest(elementCount: 10, parameter: 3, [1, 2, 3, 4], "masked");
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(Registers(4, 64, DwordBuffer(ControlAddress, 2)), new ShaderInterfaceRegisters(), initiator, dimension, 1, 1);

        Assert.False(result.Consumed);
        Assert.Equal(Untouched, Destination(guest, 1)[0]);
        Assert.True(result.KnownWrite is not null, ShaderPipelineCache.TakeCpuKernelReport());
        var known = result.KnownWrite!;
        Assert.Equal(new uint[] { 1, 2, 3, 4, 1, 2, 3, 4, 1, 2 }.Take(written), known.Words.ToArray());
    }

    [Theory]
    [InlineData(SourceAddress, false)]
    [InlineData(ControlAddress, false)]
    [InlineData(DestinationAddress + 8, true)]
    public void AnInputTheGpuOwns_LeavesTheResultUnknown(ulong owned, bool known)
    {
        var guest = Guest(elementCount: 16, parameter: 4, [1, 2, 3, 4]);
        guest.Host.GpuOwned = (owned, sizeof(uint));
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(Registers(4, 64), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);

        Assert.False(result.Consumed);
        Assert.Equal(known, result.KnownWrite is not null);
        guest.Host.GpuOwned = default;
        Assert.Equal(Untouched, Destination(guest, 1)[0]);
    }

    [Theory]
    [InlineData(1048640u, 16385u, false)]
    [InlineData(20000u, 400u, true)]
    [InlineData(4096u, 64u, true)]
    public void OnlyADispatchWithinTheEvaluationLimits_IsEvaluated(uint elementCount, uint groups, bool known)
    {
        var guest = Guest(elementCount, parameter: 4, [1, 2, 3, 4]);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(Registers(4, elementCount), new ShaderInterfaceRegisters(), 0x8001, groups, 1, 1);

        Assert.True(result.Available);
        Assert.Equal(known, result.KnownWrite is not null);
        if (result.KnownWrite is { } write)
        {
            Assert.Equal((int)elementCount, write.Words.Length);
            Assert.Equal(4u, write.Words[^1]);
        }

        Assert.Equal(Untouched, Destination(guest, 1)[0]);
    }

    [Fact]
    public void AnIndirectDispatch_IsNotEvaluated()
    {
        var guest = Guest(elementCount: 16, parameter: 4, [1, 2, 3, 4], "107");
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = new RegisterBanks(message => new InvalidOperationException(message));
        banks.Shader.Compute = Registers(4, 64);

        var indirect = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1, indirect: true);
        var direct = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1, indirect: false);

        Assert.Null(indirect.KnownWrite);
        Assert.NotNull(direct.KnownWrite);
    }

    [Fact]
    public void AKeptProgram_PredictsEachDispatchFromItsCurrentInputs()
    {
        var guest = Guest(elementCount: 8, parameter: 2, [5, 6], "107");
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = new RegisterBanks(message => new InvalidOperationException(message));
        banks.Shader.Compute = Registers(2, 64);

        var first = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1, indirect: false);
        guest.WriteWords(ControlAddress, 4u, 1u);
        var modules = guest.Host.Modules.Count;
        var second = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1, indirect: false);

        Assert.Equal(new uint[] { 5, 6, 5, 6, 5, 6, 5, 6 }, first.KnownWrite!.Words.ToArray());
        Assert.Equal(new uint[] { 5, 5, 5, 5 }, second.KnownWrite!.Words.ToArray());
        Assert.NotSame(first.KnownWrite, second.KnownWrite);
        Assert.Same(first.Input, second.Input);
        Assert.Equal(modules, guest.Host.Modules.Count);
    }

    [Fact]
    public void AReboundProgram_PredictsTheResultOfItsNewUserData()
    {
        var guest = Guest(elementCount: 6, parameter: 3, [7, 8, 9]);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = new RegisterBanks(message => new InvalidOperationException(message));
        banks.Shader.Compute = Registers(3, 64);
        var first = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1, indirect: false);
        const ulong movedControl = ControlAddress + 0x100;
        guest.WriteWords(movedControl, 5u, 2u, 0u, 0u);
        banks.Shader.Compute = Registers(3, 64, [unchecked((uint)movedControl), 0x0010_0000 | (uint)(movedControl >> 32), 1, 0x0004_DFAC]);
        var modules = guest.Host.Modules.Count;

        var second = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1, indirect: false);
        var third = cache.GetComputeProgram(banks, 0x8001, 2, 1, 1, indirect: false);

        Assert.Equal(new uint[] { 7, 8, 9, 7, 8, 9 }, first.KnownWrite!.Words.ToArray());
        Assert.Equal(new uint[] { 7, 8, 7, 8, 7 }, second.KnownWrite!.Words.ToArray());
        Assert.Equal(new uint[] { 7, 8, 7, 8, 7 }, third.KnownWrite!.Words.ToArray());
        Assert.Equal(modules, guest.Host.Modules.Count);
    }

    [Fact]
    public void RefusedEvaluations_MuteTheProgramUntilARecheck()
    {
        var guest = Guest(elementCount: 4, parameter: 2, [3, 4]);
        guest.Host.GpuOwned = (SourceAddress, 2 * sizeof(uint));
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);
        var banks = new RegisterBanks(message => new InvalidOperationException(message));
        banks.Shader.Compute = Registers(2, 64);
        KnownGpuWrite? Dispatch() => cache.GetComputeProgram(banks, 0x8001, 1, 1, 1, indirect: false).KnownWrite;

        for (var index = 0; index < ShaderPipelineCache.MuteAfterRefusals; index++)
        {
            Assert.Null(Dispatch());
        }

        guest.Host.GpuOwned = default;
        for (var index = 1; index < ShaderPipelineCache.MutedRecheckPeriod; index++)
        {
            Assert.Null(Dispatch());
        }

        var recheck = Dispatch();
        Assert.NotNull(recheck);
        recheck.NoteRead();
        Assert.NotNull(Dispatch());
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(2, false)]
    public void AnInputAnEarlierPredictionCoversWhole_IsReadFromThatPrediction(int shadowWords, bool known)
    {
        var guest = Guest(elementCount: 8, parameter: 4, [1, 2, 3, 4]);
        guest.Host.GpuOwned = (SourceAddress, 4 * sizeof(uint));
        guest.Host.Shadow = (SourceAddress, new uint[] { 9, 8, 7, 6 }[..shadowWords]);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(Registers(4, 64), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);

        Assert.Equal(known, result.KnownWrite is not null);
        if (known)
        {
            Assert.Equal(new uint[] { 9, 8, 7, 6, 9, 8, 7, 6 }, result.KnownWrite!.Words.ToArray());
        }
    }

    private static readonly uint[] ConsumedWords = [1, 2, 3, 4, 1, 2, 3, 4, 1, 2];

    [Fact]
    public void AFullyEvaluatedDispatch_IsConsumedAndItsWordsLandAsAStoreBehindEarlierWork()
    {
        var guest = Guest(elementCount: 10, parameter: 4, [1, 2, 3, 4]);
        var stream = new FakeKernelWriteStream(guest.Memory);
        guest.Host.KernelWriteStream = stream;
        stream.QueueGpuRead("earlier read", DestinationAddress, 64);
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(Registers(4, 64), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);

        Assert.True(result.Consumed, ShaderPipelineCache.TakeCpuKernelReport());
        Assert.Null(result.KnownWrite);
        Assert.Equal(Untouched, Destination(guest, 1)[0]);
        stream.RunAll();
        Assert.Equal(new[] { "earlier read", "store" }, stream.Ran);
        Assert.Equal(ConsumedWords, Destination(guest, ConsumedWords.Length));
        Assert.Equal(Untouched, Destination(guest, ConsumedWords.Length + 1)[^1]);
    }

    [Theory]
    [InlineData(DestinationAddress + 16, false)]
    [InlineData(DestinationAddress - 4, true)]
    [InlineData(DestinationAddress + 40, true)]
    public void AnEarlierQueuedWriteOverTheDestination_KeepsTheDispatchOnTheGpuWithItsResultKnown(ulong written, bool consumed)
    {
        var guest = Guest(elementCount: 10, parameter: 4, [1, 2, 3, 4]);
        var stream = new FakeKernelWriteStream(guest.Memory);
        guest.Host.KernelWriteStream = stream;
        stream.QueueGpuWrite("earlier write", written, sizeof(uint));
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(Registers(4, 64), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);

        Assert.Equal(consumed, result.Consumed);
        Assert.Equal(!consumed, result.KnownWrite is not null);
        if (!consumed)
        {
            Assert.Equal(ConsumedWords, result.KnownWrite!.Words.ToArray());
        }

        stream.RunAll();
        Assert.Equal(consumed ? new[] { "earlier write", "store" } : new[] { "earlier write" }, stream.Ran);
    }

    [Theory]
    [InlineData("thread")]
    [InlineData("cross queue")]
    [InlineData("image bytes")]
    [InlineData("surface metadata")]
    public void AStoreTheStreamCannotOrder_FallsBackToThePrediction(string reason)
    {
        var guest = Guest(elementCount: 10, parameter: 4, [1, 2, 3, 4]);
        var stream = new FakeKernelWriteStream(guest.Memory)
        {
            OnStreamThread = reason != "thread",
            InCrossQueueOrderWindow = reason == "cross queue",
            ImageBytes = reason == "image bytes" ? (DestinationAddress + 36, 4ul) : default,
            SurfaceMetadata = reason == "surface metadata" ? (DestinationAddress + 8, 4ul) : default,
        };
        guest.Host.KernelWriteStream = stream;
        var cache = new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry);

        var result = cache.GetComputeProgram(Registers(4, 64), new ShaderInterfaceRegisters(), 0x8001, 1, 1, 1);

        Assert.False(result.Consumed);
        Assert.Equal(ConsumedWords, result.KnownWrite!.Words.ToArray());
        stream.RunAll();
        Assert.Empty(stream.Ran);
    }

    [Fact]
    public void AConsumedDispatch_RecordsNoGpuDispatch()
    {
        var guest = Guest(elementCount: 10, parameter: 4, [1, 2, 3, 4]);
        var stream = new FakeKernelWriteStream(guest.Memory);
        guest.Host.KernelWriteStream = stream;
        var host = new RecordingRenderHost();
        var executor = new RenderExecutor(host, new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry));
        var banks = RenderExecutorFixtures.Banks();
        banks.Shader.Compute = Registers(4, 64);

        executor.Dispatch(1, banks, 1, 1, 1, 0x8001);

        Assert.DoesNotContain(host.Calls, call => call.StartsWith("dispatch", StringComparison.Ordinal));
        stream.RunAll();
        Assert.Equal(new[] { "store" }, stream.Ran);
        Assert.Equal(ConsumedWords, Destination(guest, ConsumedWords.Length));
    }
}
