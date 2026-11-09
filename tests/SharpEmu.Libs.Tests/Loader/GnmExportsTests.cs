// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Gnm;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Kernel;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void GnmRingExportIsRegisteredOnlyInGnmMode()
    {
        var exports = GnmExportRegistry.CreateExports();
        var export = Assert.Single(exports, export => export.Nid == "ln33zjBrfjk");
        Assert.Equal("sceGnmGetTheTessellationFactorRingBufferBaseAddress", export.Name);
        Assert.Equal("libSceGnmDriver", export.LibraryName);
        Assert.Equal(Generation.Gen4, export.Target);
        Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            export => export.Nid == "ln33zjBrfjk");
        Assert.Equal(exports.Count, exports.Select(export => export.Nid).Distinct().Count());
        Assert.Single(exports, export => export.Nid == "A2CQ3kgSopQ");
        Assert.Single(exports, export => export.Nid == "QOQtbeDqsT4");
        var updatePs = Assert.Single(exports, export => export.Nid == "4MgRw-bVNQU");
        Assert.Equal("sceGnmUpdatePsShader", updatePs.Name);
        Assert.Equal(Generation.Gen4, updatePs.Target);
    }

    [Fact]
    public void ReturnsUnmappedAlignedAgcReservationAndPreservesArguments()
    {
        using var test = new BackedKernelMemory();
        var memory = test.Memory;
        var context = new CpuContext(memory, Generation.Gen4);
        context[CpuRegister.Rdi] = 0x11223344;
        context[CpuRegister.Rsi] = 0x55667788;
        Assert.Equal(0, GnmDriverExports.GetTessellationFactorRingBufferBaseAddress(context));

        var address = context[CpuRegister.Rax];
        Assert.True(address > uint.MaxValue);
        Assert.Equal(0UL, address & 0xFFFF);
        Assert.Equal(0x11223344UL, context[CpuRegister.Rdi]);
        Assert.Equal(0x55667788UL, context[CpuRegister.Rsi]);
        var contents = new byte[AgcExports.DefaultTessellationFactorRingBytes];
        Assert.False(memory.TryRead(address, contents));
        Assert.True(AgcExports.TryGetTessellationFactorRingAddress(memory, out var agcAddress));
        Assert.Equal(address, agcAddress);
    }

    [Fact]
    public void ReusesRingThroughNestedMemoryWrappersWithoutClearingItsContents()
    {
        using var test = new BackedKernelMemory();
        var memory = test.Memory;
        Assert.True(AgcExports.TryGetTessellationFactorRingAddress(memory, out var address));
        test.Allocate(0, AgcExports.DefaultTessellationFactorRingBytes);
        Assert.Equal(address, test.Map(0, AgcExports.DefaultTessellationFactorRingBytes, address));
        byte[] marker = [1, 2, 3, 4];
        Assert.True(memory.TryWrite(address, marker));
        var context = new CpuContext(new WrappedMemory(new WrappedMemory(memory)), Generation.Gen4);
        Assert.Equal(0, GnmDriverExports.GetTessellationFactorRingBufferBaseAddress(context));
        Assert.Equal(address, context[CpuRegister.Rax]);
        var contents = new byte[marker.Length];
        Assert.True(memory.TryRead(address, contents));
        Assert.Equal(marker, contents);
    }

    [Fact]
    public async Task ConcurrentQueriesShareOneRing()
    {
        using var test = new BackedKernelMemory();
        var memory = test.Memory;
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var context = new CpuContext(new WrappedMemory(memory), Generation.Gen4);
            Assert.Equal(0, GnmDriverExports.GetTessellationFactorRingBufferBaseAddress(context));
            return context[CpuRegister.Rax];
        })).ToArray();
        var addresses = await Task.WhenAll(tasks);
        Assert.Single(addresses.Distinct());
        Assert.NotEqual(0UL, addresses[0]);
    }

    [Fact]
    public void GuestCanMapItsOwnDirectMemoryAtTheReturnedAddress()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, AgcExports.DefaultTessellationFactorRingBytes);
        var context = new CpuContext(new WrappedMemory(test.Memory), Generation.Gen4);
        Assert.Equal(0, GnmDriverExports.GetTessellationFactorRingBufferBaseAddress(context));
        var address = context[CpuRegister.Rax];
        Assert.True(context.TryWriteUInt64(test.Output, address));
        context[CpuRegister.Rdi] = test.Output;
        context[CpuRegister.Rsi] = AgcExports.DefaultTessellationFactorRingBytes;
        context[CpuRegister.Rdx] = 0x33;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = 0x10000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.True(context.TryReadUInt64(test.Output, out var mapped));
        Assert.Equal(address, mapped);
        Assert.True(test.Memory.IsBackedRange(mapped, AgcExports.DefaultTessellationFactorRingBytes));
        Assert.True(test.Memory.TryWrite(mapped, new byte[] { 123 }));
        Assert.True(test.Memory.TryReadBacking(mapped, new byte[1]));
        Assert.Equal(0, GnmDriverExports.GetTessellationFactorRingBufferBaseAddress(context));
        Assert.Equal(address, context[CpuRegister.Rax]);
    }

    [Fact]
    public void AllocationFailureReturnsNullNotAnErrorCodeAsAPointer()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen4);
        context[CpuRegister.Rax] = 0xDEADBEEF;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            GnmDriverExports.GetTessellationFactorRingBufferBaseAddress(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void ResetInvalidatesThePreviousSessionRing()
    {
        using var test = new BackedKernelMemory();
        var memory = test.Memory;
        Assert.True(AgcExports.TryGetTessellationFactorRingAddress(memory, out var first));
        AgcExports.ResetTessellationFactorRing(memory);
        Assert.True(AgcExports.TryGetTessellationFactorRingAddress(memory, out var second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EmbeddedVertexShaderHasTrackedBackingAndCompilesThroughTheGcnFrontend()
    {
        using var test = new BackedKernelMemory();
        var memory = test.Memory;
        var commands = memory.AllocateAt(0, 0x4000, false);
        var context = new CpuContext(new WrappedMemory(memory), Generation.Gen4);
        context[CpuRegister.Rdi] = commands;
        context[CpuRegister.Rsi] = 29;
        Assert.Equal(0, GnmDriverExports.SetEmbeddedVsShader(context));
        Assert.True(context.TryReadUInt32(commands + 8, out var low));
        Assert.True(context.TryReadUInt32(commands + 12, out var high));
        var address = ((ulong)high << 40) | ((ulong)low << 8);
        Assert.True(memory.IsBackedRange(address, 92));
        var binary = GcnShaderBinary.Read(memory, address);
        Assert.Equal(64u, binary.CodeSize);
        Assert.Equal(1, binary.ShaderType);
        var frontend = new GcnShaderFrontend();
        var shader = frontend.ReadShader(context, address, "vertex");
        Assert.True(frontend.TryDecode(context, shader, ShaderStage.Vertex, out var program, out var error), error);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, 1, 0, 16);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var request = new ShaderCompileRequest(plan, resources, BindingLayout.Allocate(resources.Info, [], false, false, false))
        {
            EnableGraphicsSubgroupOperations = false,
            VertexInputs = [],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out error), error);
        Assert.NotEmpty(compiled.Spirv);
        context[CpuRegister.Rcx] = 0x40;
        Assert.Equal(0, GnmDriverExports.SetEmbeddedVsShader(context));
        Assert.True(context.TryReadUInt32(commands + 8, out var reusedLow));
        Assert.Equal(low, reusedLow);
        Assert.True(context.TryReadUInt32(commands + 24, out var resource));
        Assert.Equal(0xC0040u, resource);
        Assert.Equal(0UL, context[CpuRegister.Rdx]);
        Assert.Equal(0x40UL, context[CpuRegister.Rcx]);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void UnknownEmbeddedVertexShaderIdIsRejected(uint id)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen4);
        context[CpuRegister.Rdi] = MemoryBase;
        context[CpuRegister.Rsi] = 29;
        context[CpuRegister.Rdx] = id;
        Assert.Equal(unchecked((int)0x8EEE00FF), GnmDriverExports.SetEmbeddedVsShader(context));
    }

    [Theory]
    [InlineData(0u, 15u)]
    [InlineData(1u, 3u)]
    public void EmbeddedPixelShadersBindAndCompileWithTheRequestedExportMask(uint id, uint mask)
    {
        using var test = new BackedKernelMemory();
        var memory = test.Memory;
        var commands = memory.AllocateAt(0, 0x4000, false);
        var context = new CpuContext(new WrappedMemory(memory), Generation.Gen4);
        context[CpuRegister.Rdi] = commands;
        context[CpuRegister.Rsi] = 40;
        context[CpuRegister.Rdx] = id;
        Assert.Equal(0, GnmDriverExports.SetEmbeddedPsShader(context));
        Assert.True(context.TryReadUInt32(commands + 8, out var low));
        Assert.True(context.TryReadUInt32(commands + 12, out var high));
        var address = ((ulong)high << 40) | ((ulong)low << 8);
        Assert.True(memory.IsBackedRange(address, 52));
        Assert.Equal(0, GcnShaderBinary.Read(memory, address).ShaderType);
        var frontend = new GcnShaderFrontend();
        var shader = frontend.ReadShader(context, address, "pixel");
        Assert.True(frontend.TryDecode(context, shader, ShaderStage.Pixel, out var program, out var error), error);
        var export = Assert.Single(program.Instructions, instruction => instruction.Control is Gen5ExportControl);
        Assert.Equal(mask, Assert.IsType<Gen5ExportControl>(export.Control).EnableMask);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, 1, 0, 16);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var request = new ShaderCompileRequest(plan, resources, BindingLayout.Allocate(resources.Info, [], false, false, false));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out error), error);
        Assert.NotEmpty(compiled.Spirv);
        Assert.True(context.TryReadUInt32(commands + 20, out var resourceRegister));
        Assert.Equal(10u, resourceRegister);
        Assert.True(context.TryReadUInt32(commands + 108, out var colorMask));
        Assert.Equal(mask, colorMask);
        Assert.Equal(0, GnmDriverExports.SetEmbeddedPsShader(context));
        Assert.True(context.TryReadUInt32(commands + 8, out var reusedLow));
        Assert.Equal(low, reusedLow);
        Assert.Equal((ulong)id, context[CpuRegister.Rdx]);
    }

    private sealed class WrappedMemory(ICpuMemory memory) : ICpuMemory, ICpuMemoryWrapper
    {
        public ICpuMemory Inner => memory;
        public bool TryRead(ulong address, Span<byte> destination) => memory.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => memory.TryWrite(address, source);
    }
}
