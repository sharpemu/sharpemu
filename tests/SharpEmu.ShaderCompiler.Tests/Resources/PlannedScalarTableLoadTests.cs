// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class PlannedScalarTableLoadTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void ScalarLoadsReadGuestWordsAfterTheImageSlotPrefix(bool bindless, int imageCount)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (var image = 0; image < imageCount; image++)
        {
            var register = (uint)(32 + image * 8);
            uint[] words = [(uint)(0x1000 + image * 0x1000), 22u << 20, 3u | (3u << 14), 0xFACu | (9u << 28), 0, 0, 0, 0];
            for (uint component = 0; component < words.Length; component++)
            {
                instructions.Add(MoveScalar(pc, register + component, words[component]));
                pc += 8;
            }
            instructions.Add(Image(pc, "ImageStore", register));
            pc += 8;
        }
        var scalarPc = pc;
        instructions.Add(ScalarLoad(pc, 0, destination: 16, count: 4)); pc += 8;
        for (uint component = 0; component < 4; component++)
        {
            instructions.Add(MoveVectorFromScalar(pc, 4, 16 + component)); pc += 4;
            instructions.Add(BufferStore(pc, 4, (int)(component * 4))); pc += 8;
        }
        instructions.Add(EndProgram(pc));
        var request = Request(Program([.. instructions]), userDataCount: 9, usesBindlessImages: bindless);
        Assert.Equal((uint)imageCount, BindingLayout.ImageSlotTableDwordCount(request.Resources.Info));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var names = new SpirvModuleInspector(shader.Spirv).Names;
        var tableId = Assert.Single(names, pair => pair.Value == "flattenedResourceTable").Key;
        var constants = new Dictionary<uint, uint>();
        var pointers = new Dictionary<uint, uint>();
        var loads = new Dictionary<uint, uint>();
        var loadedSlots = new Dictionary<uint, uint>();
        var wordsSpirv = new uint[shader.Spirv.Length / 4];
        Buffer.BlockCopy(shader.Spirv, 0, wordsSpirv, 0, shader.Spirv.Length);
        for (var offset = 5; offset < wordsSpirv.Length;)
        {
            var count = (int)(wordsSpirv[offset] >> 16);
            var operands = wordsSpirv.AsSpan(offset + 1, count - 1);
            switch ((SpirvOp)(wordsSpirv[offset] & 0xFFFF))
            {
                case SpirvOp.Constant when count == 4:
                    constants[operands[1]] = operands[2];
                    break;
                case SpirvOp.AccessChain when operands[2] == tableId && constants.TryGetValue(operands[^1], out var constantSlot):
                    pointers[operands[1]] = constantSlot;
                    break;
                case SpirvOp.Load when pointers.TryGetValue(operands[2], out var slot):
                    loads[operands[1]] = slot;
                    break;
                case SpirvOp.Store when loads.TryGetValue(operands[1], out var loaded):
                    loadedSlots[operands[0]] = loaded;
                    break;
            }
            offset += count;
        }
        for (uint component = 0; component < 4; component++)
        {
            Assert.True(request.Memory.TryGetIndex(scalarPc, component, out var memoryIndex));
            var flatSlot = request.FlattenedSlotByMemoryIndex[memoryIndex];
            Assert.True(flatSlot < request.FlattenedTableReservedWords);
            var register = Assert.Single(names, pair => pair.Value == $"s{16 + component}").Key;
            // The host binds image indices first, then the materialized guest words.
            Assert.Equal(flatSlot + (bindless ? (uint)imageCount : 0), loadedSlots[register]);
        }
    }
}
