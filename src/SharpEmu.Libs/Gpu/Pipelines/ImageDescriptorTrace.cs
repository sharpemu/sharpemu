// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed class ImageDescriptorTrace
{
    private static readonly RecentImageTrace _recent = new(512);
    private static readonly object _gate = new();

    internal static void WriteHistory()
    {
        if (!ImageClearTrace.Enabled) return;
        lock (_gate) _recent.WriteTo(Console.Error, "ImageDescriptorHistory");
    }

    internal void Record(ShaderSource shader, ShaderResourcePlan plan, ResourceSnapshot snapshot)
    {
        if (!ImageClearTrace.Enabled) return;
        for (var index = 0; index < Math.Min(plan.Info.Images.Count, snapshot.Images.Length); index++)
        {
            var words = snapshot.Images[index];
            if (words.Length < 8) continue;
            var descriptor = new TextureDescriptorWords(words);
            ImageTraceRange.NoteDescriptor(shader.Hash, index, descriptor.BaseAddress);
            // A traced range selects descriptors of any type by base address.
            if (descriptor.Type != GuestImageType.Color3D && !ImageTraceRange.Overlaps(descriptor.BaseAddress, 1)) continue;
            using var output = new StringWriter();
            var resource = plan.Info.Images[index];
            output.WriteLine($"[GPU][TRACE] ImageDescriptor time={DateTime.UtcNow:O} shader=0x{shader.Address:X16} hash=0x{shader.Hash:X16} stage={shader.Label} " +
                $"image={index} address=0x{descriptor.BaseAddress:X16} source={resource.Source} firstUsePc=0x{resource.FirstUsePc:X} " +
                $"class={resource.ResourceClass} read={resource.Read} written={resource.Written} words={string.Join(',', words.Select(word => word.ToString("X8")))} " +
                $"userDataBase={plan.UserDataBase} userData={string.Join(',', snapshot.UserData.Select(word => word.ToString("X8")))}");
            if (resource.Source < plan.DescriptorSources.Count)
            {
                var source = plan.DescriptorSources[(int)resource.Source];
                output.WriteLine($"[GPU][TRACE] ImageDescriptorRoots hash=0x{shader.Hash:X16} image={index} indirect={source.IndirectImage != null} nodes={string.Join(',', source.Dwords.Select(value => value.Id))}");
                var pending = new Queue<ScalarValue>(source.Dwords);
                var visited = new HashSet<int>();
                while (pending.Count != 0 && visited.Count < 256)
                {
                    var value = pending.Dequeue();
                    if (!visited.Add(value.Id)) continue;
                    var resolved = value.Kind == ScalarValueKind.ResourceTableWord && value.Payload < (ulong)snapshot.FlattenedResourceTable.Length
                        ? $"0x{snapshot.FlattenedResourceTable[(int)value.Payload]:X8}" : "not-recorded";
                    if (value.Kind == ScalarValueKind.UserData && value.Payload >= plan.UserDataBase &&
                        value.Payload - plan.UserDataBase < (ulong)snapshot.UserData.Length)
                        resolved = $"0x{snapshot.UserData[(int)(value.Payload - plan.UserDataBase)]:X8}";
                    output.WriteLine($"[GPU][TRACE] ImageDescriptorNode hash=0x{shader.Hash:X16} image={index} id={value.Id} " +
                        $"kind={value.Kind} type={value.Type} operation={value.Operation} payload=0x{value.Payload:X} " +
                        $"operands={string.Join(',', value.Operands.Select(operand => operand.Id))} predecessors={string.Join(',', value.PhiPredecessors)} resolved={resolved}");
                    foreach (var operand in value.Operands) pending.Enqueue(operand);
                    if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord &&
                        (uint)value.MemoryIndex < (uint)plan.Memory.Count)
                    {
                        var memory = plan.Memory[value.MemoryIndex];
                        output.WriteLine($"[GPU][TRACE] ImageDescriptorMemory hash=0x{shader.Hash:X16} image={index} node={value.Id} " +
                            $"memoryIndex={value.MemoryIndex} pc=0x{memory.Pc:X} opcode={memory.Opcode} offset=0x{memory.Offset:X8} " +
                            $"component={memory.ComponentIndex} components={memory.ComponentCount} planningOnly={memory.PlanningOnly}");
                        foreach (var instruction in plan.Graph.Program.Instructions)
                        {
                            if (instruction.Pc != memory.Pc) continue;
                            output.WriteLine($"[GPU][TRACE] ImageDescriptorInstruction hash=0x{shader.Hash:X16} image={index} pc=0x{instruction.Pc:X} " +
                                $"words={string.Join('_', instruction.Words.Select(word => word.ToString("X8")))} opcode={instruction.Opcode} " +
                                $"destinations={string.Join(',', instruction.Destinations)} sources={string.Join(',', instruction.Sources)} control={instruction.Control}");
                            break;
                        }
                    }
                    if (value.Kind == ScalarValueKind.ResourceTableWord)
                    {
                        if (value.Payload < (ulong)plan.TableReads.Count)
                        {
                            var read = plan.TableReads[(int)value.Payload];
                            output.WriteLine($"[GPU][TRACE] ImageDescriptorTableSource hash=0x{shader.Hash:X16} image={index} " +
                                $"node={value.Id} slot={value.Payload} flatOffset={read.FlatOffset} sourceNode={read.Value.Id}");
                            pending.Enqueue(read.Value);
                        }
                        else
                            output.WriteLine($"[GPU][TRACE] ImageDescriptorTableSource hash=0x{shader.Hash:X16} image={index} node={value.Id} slot={value.Payload} source=unavailable");
                    }
                }
                output.WriteLine($"[GPU][TRACE] ImageDescriptorGraph hash=0x{shader.Hash:X16} image={index} nodes={visited.Count} truncated={pending.Count != 0}");
                foreach (var read in plan.TableReads)
                {
                    if (!visited.Contains(read.Value.Id) || read.FlatOffset >= snapshot.FlattenedResourceTable.Length) continue;
                    output.WriteLine($"[GPU][TRACE] ImageDescriptorRead hash=0x{shader.Hash:X16} image={index} node={read.Value.Id} " +
                        $"flatOffset={read.FlatOffset} value=0x{snapshot.FlattenedResourceTable[(int)read.FlatOffset]:X8}");
                }
            }
            lock (_gate) _recent.Record($"{shader.Hash:X16}:{index}:{descriptor.BaseAddress:X16}", output.ToString());
        }
    }
}
