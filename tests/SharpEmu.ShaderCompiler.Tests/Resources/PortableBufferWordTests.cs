// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class PortableBufferWordTests
{
    [Theory]
    [InlineData(0x3FFFu, 4012u)]
    [InlineData(0x2000u, 940u)]
    [InlineData(0x1000u, 556u)]
    public void PackingPreservesFullStrideAndDescriptorWord3(uint stride, uint swizzle)
    {
        uint[] descriptor = [0x1000, 0xC0000000 | (stride << 16), 1, (22u << 12) | swizzle | (1u << 23)];
        var packed = PortableBufferWord.Pack(descriptor);
        Assert.Equal(2, PortableBufferWord.DwordCount);
        Assert.Equal(stride, packed.Stride);
        Assert.Equal(descriptor[3], packed.DescriptorWord3);
        Assert.Equal(swizzle, packed.DescriptorWord3 & 0xFFF);
    }

    [Fact]
    public void NullAndInvalidDescriptorsPackAsZero()
    {
        uint[][] descriptors = [[], [0, 0, 0], [0, 0, 0, 0], [0x1000, 0x3FFF0000, 1, 0xC0016FAC], [0, 0, 0, 0, 0]];
        foreach (var descriptor in descriptors)
            Assert.Equal((0u, 0u), PortableBufferWord.Pack(descriptor));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(20u)]
    public void TwoDwordsPerBufferPrecedeDispatchLimitsAndAffectAllocation(uint cursor)
    {
        var info = new ShaderResourceInfo { Buffers = Enumerable.Range(0, 5).Select(_ => new BufferResource { Read = true }).ToList() };
        var layout = BindingLayout.Allocate(info, [0u], false, false, true, cursor, usesDispatchThreadLimits: true, portableBuffers: true);
        Assert.Equal(10u, layout.BufferWordCount);
        Assert.Equal(5u, layout.BufferWordDword);
        Assert.Equal(15u, layout.DispatchThreadLimitsDword);
        Assert.Equal(18u, layout.ShaderDataDwordCount);
        Assert.Equal(cursor == 0, layout.UsesPushData);
        Assert.Equal(cursor != 0, layout.Find(DescriptorBindingKind.ShaderData) is not null);
        Assert.NotEqual(layout, BindingLayout.Allocate(info, [0u], false, false, true, cursor, usesDispatchThreadLimits: true));
        BindingLayoutValidator.Validate(layout, info, [0u], false, false, true, Hash, ShaderStage.Compute);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void OnlyReadOnlyPortableBuffersShareSwizzleSpecializationsAndModules(bool portable, bool written)
    {
        var program = written
            ? Program(BufferLoad(0, 0, dwords: 4, formatted: true), BufferStore(8, 0, formatted: true), EndProgram(16))
            : Program(BufferLoad(0, 0, dwords: 4, formatted: true), EndProgram(8));
        var plan = Extract(program);
        var runtime = portable && !written;
        var specializations = new List<ResourceSpecialization>();
        var modules = new List<byte[]>();
        foreach (var swizzle in new uint[] { 4012, 940, 556 })
        {
            uint[] descriptor = [0x1000, 0x3FFF0000, 1, (22u << 12) | swizzle];
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = descriptor, PortableBuffers = portable },
                ref snapshot, ref specialization));
            Assert.Equal(descriptor, Assert.Single(snapshot.Buffers));
            var buffer = Assert.Single(specialization.Buffers);
            Assert.Equal(portable ? 0u : 0x3FFFu, buffer.PackedStride);
            Assert.Equal(runtime ? DescriptorConstants.InvalidFormat : 22u, buffer.DescriptorFormat);
            Assert.Equal(runtime ? DescriptorConstants.IdentityDestinationSelect : swizzle, buffer.DescriptorSwizzle);
            specializations.Add(specialization);
            modules.Add(Compile(specialization));

            if (runtime)
            {
                var recorded = specialization.Clone();
                recorded.Buffers[0] = new BufferSpecialization(0x3FFF, 22, swizzle);
                Assert.Equal(modules[^1], Compile(recorded));
            }
        }

        if (runtime)
        {
            Assert.All(specializations, specialization => Assert.Equal(ResourceSpecialization.Default(plan.Info), specialization));
            Assert.All(modules, module => Assert.Equal(Compile(ResourceSpecialization.Default(plan.Info)), module));
            AssertPortableDwordsLoaded(modules[0]);
        }
        else
        {
            Assert.NotEqual(specializations[0], specializations[1]);
            Assert.NotEqual(modules[0], modules[1]);
        }

        byte[] Compile(ResourceSpecialization specialization)
        {
            var resources = ResourceMaterializer.ApplyTo(plan, specialization);
            var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64),
                false, false, false, portableBuffers: portable);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out var shader, out var error), error);
            return shader.Spirv;
        }
    }

    [Fact]
    public void MaterializationCacheSeparatesPortableAndBakedModes()
    {
        var plan = Extract(Program(BufferLoad(0, 0, formatted: true), EndProgram(8)));
        var cache = new ResourceMaterializationCache();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        foreach (var portable in new[] { false, true, false, true })
        {
            var inputs = new ResourceRuntimeInputs { UserData = [0x1000, 16u << 16, 1, (22u << 12) | 940], PortableBuffers = portable };
            Assert.True(cache.Materialize(plan, inputs, (ulong _, Span<byte> _, bool _) => false, ref snapshot, ref specialization, out _));
            Assert.Equal(portable ? DescriptorConstants.IdentityDestinationSelect : 940u, specialization.Buffers[0].DescriptorSwizzle);
        }
        Assert.Equal(2, cache.Misses);
        Assert.Equal(2, cache.Hits);
    }

    private static void AssertPortableDwordsLoaded(byte[] spirv)
    {
        var array = new SpirvModuleInspector(spirv).Names.Single(pair => pair.Value == "guestBufferWords").Key;
        var words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
        var constants = new Dictionary<uint, uint>();
        var pointers = new Dictionary<uint, uint>();
        var loads = new HashSet<uint>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            switch ((SpirvOp)(words[offset] & 0xFFFF))
            {
                case SpirvOp.Constant when (words[offset] >> 16) == 4:
                    constants[words[offset + 2]] = words[offset + 3];
                    break;
                case SpirvOp.AccessChain when words[offset + 3] == array:
                    pointers[words[offset + 2]] = constants[words[offset + 4]];
                    break;
                case SpirvOp.Load when pointers.TryGetValue(words[offset + 3], out var dword):
                    loads.Add(dword);
                    break;
            }
        }
        Assert.True(loads.SetEquals([0u, 1u]), "Stride and descriptor word3 must both be loaded at runtime.");
    }
}
