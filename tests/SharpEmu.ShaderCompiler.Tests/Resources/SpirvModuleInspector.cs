// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Vulkan;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Reads the declarations of a SPIR-V module: names, decorations, capabilities, memory model.
internal sealed class SpirvModuleInspector
{
    public Dictionary<uint, string> Names { get; } = [];
    public Dictionary<uint, uint> Bindings { get; } = [];
    public Dictionary<uint, uint> DescriptorSets { get; } = [];
    public HashSet<uint> Capabilities { get; } = [];
    public HashSet<ushort> Opcodes { get; } = [];
    public Dictionary<uint, uint> VariableStorageClasses { get; } = [];
    public uint AddressingModel { get; private set; }

    public SpirvModuleInspector(byte[] spirv)
    {
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var offset = 5;
        while (offset < words.Length)
        {
            var wordCount = (int)(words[offset] >> 16);
            var opcode = (ushort)(words[offset] & 0xFFFF);
            Opcodes.Add(opcode);
            switch ((SpirvOp)opcode)
            {
                case SpirvOp.Name:
                    Names[words[offset + 1]] = DecodeString(words, offset + 2, offset + wordCount);
                    break;
                case SpirvOp.Decorate when wordCount >= 4 && words[offset + 2] == (uint)SpirvDecoration.Binding:
                    Bindings[words[offset + 1]] = words[offset + 3];
                    break;
                case SpirvOp.Decorate when wordCount >= 4 && words[offset + 2] == (uint)SpirvDecoration.DescriptorSet:
                    DescriptorSets[words[offset + 1]] = words[offset + 3];
                    break;
                case SpirvOp.Capability:
                    Capabilities.Add(words[offset + 1]);
                    break;
                case SpirvOp.MemoryModel:
                    AddressingModel = words[offset + 1];
                    break;
                case SpirvOp.Variable when wordCount >= 4:
                    VariableStorageClasses[words[offset + 2]] = words[offset + 3];
                    break;
            }

            offset += Math.Max(wordCount, 1);
        }
    }

    // The (set, binding) pairs of every decorated variable.
    public HashSet<(uint Set, uint Binding)> DescriptorBindings =>
        Bindings.Select(pair => (DescriptorSets.GetValueOrDefault(pair.Key, uint.MaxValue), pair.Value)).ToHashSet();

    public uint BindingOf(string name)
    {
        var id = Names.First(pair => pair.Value == name).Key;
        return Bindings[id];
    }

    public bool HasVariableInStorageClass(SpirvStorageClass storageClass) =>
        VariableStorageClasses.ContainsValue((uint)storageClass);

    private static string DecodeString(uint[] words, int start, int end)
    {
        var bytes = new List<byte>();
        for (var index = start; index < end; index++)
        {
            foreach (var shift in new[] { 0, 8, 16, 24 })
            {
                var value = (byte)(words[index] >> shift);
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
