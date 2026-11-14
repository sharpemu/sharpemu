// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// Graphics pipeline libraries (VK_EXT_graphics_pipeline_library). A graphics pipeline compiles its vertex and pixel programs
// together, so a program that many pipelines share (a material vertex shader paired with each material's pixel shader, or
// a pixel shader drawn into other targets) is compiled again for each of them. With libraries each part compiles once:
// the vertex input, the vertex or mesh stage with its rasterization state, the pixel stage with its depth and stencil
// state, and the color outputs. A pipeline links its parts without optimizing across them.
// Parts link only through layouts with independent sets: the vertex stage's bindings are set 0 and the pixel stage's set 2
// (PixelDescriptorSet: each pixel module's set-0 variables move there), the bindless heap stays set 1. The vertex set is
// pushed when it fits, the pixel set is allocated, so each set's layout depends on its own stage alone.
// SHARPEMU_GRAPHICS_PIPELINE_LIBRARY=0 keeps whole pipelines with one set.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        internal const uint PixelDescriptorSet = 2;
        private const uint DescriptorSetDecoration = 34;
        private const string PipelineLibraryExtensionName = "VK_KHR_pipeline_library";
        private const string GraphicsPipelineLibraryExtensionName = "VK_EXT_graphics_pipeline_library";

        private static bool GraphicsPipelineLibrariesRequested() =>
            Environment.GetEnvironmentVariable("SHARPEMU_GRAPHICS_PIPELINE_LIBRARY") != "0";

        // Device setup sets this when the device links libraries fast.
        private bool _graphicsPipelineLibraries;

        // The compiled parts by the state each holds; destroyed with the pipelines.
        private readonly Dictionary<string, Pipeline> _graphicsLibraries = new(StringComparer.Ordinal);

        // A copy of a SPIR-V module whose descriptor set `from` decorations name set `to`.
        internal static byte[] WithDescriptorSet(byte[] code, uint from, uint to)
        {
            var copy = (byte[])code.Clone();
            for (var offset = 20; offset + 4 <= copy.Length;)
            {
                var header = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(offset));
                var wordCount = (int)(header >> 16);
                if (wordCount == 0 || wordCount > (copy.Length - offset) / 4)
                {
                    throw new InvalidOperationException("The shader contains an invalid SPIR-V instruction.");
                }

                if ((header & 0xFFFF) == (uint)SpirvOp.Decorate && wordCount == 4 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(offset + 8)) == DescriptorSetDecoration &&
                    BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(offset + 12)) == from)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset + 12), to);
                }

                offset += wordCount * 4;
            }

            return copy;
        }

        private RenderPipelineEntry CreateLibraryPipelineEntry(GraphicsPipelineDescription description)
        {
            var mesh = description.VertexStage.Stage == ShaderStageKind.Mesh;
            var vertexBindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(vertexBindings, description.VertexStage, mesh ? ShaderStage.Mesh : ShaderStage.Vertex);
            var pixelBindings = new List<DescriptorSetLayoutBinding>();
            if (description.PixelStage is { } pixelStage)
            {
                CollectLayoutBindings(pixelBindings, pixelStage, ShaderStage.Pixel);
            }

            DescriptorSetLayout vertexSet = default;
            DescriptorSetDemand vertexDemand = default;
            var vertexPush = false;
            if (vertexBindings.Count != 0)
            {
                vertexSet = CreateDescriptorSetLayout(vertexBindings, allowPush: true, out vertexPush, out vertexDemand);
            }

            DescriptorSetLayout pixelSet = default;
            DescriptorSetDemand pixelDemand = default;
            if (pixelBindings.Count != 0)
            {
                pixelSet = CreateDescriptorSetLayout(pixelBindings, allowPush: false, out _, out pixelDemand);
            }

            var heap = UsesBindlessHeap(description.VertexStage) ||
                (description.PixelStage is { } heapStage && UsesBindlessHeap(heapStage));
            return new RenderPipelineEntry
            {
                SetLayout = vertexSet,
                Demand = vertexDemand,
                UsesPushDescriptors = vertexPush,
                PixelSetLayout = pixelSet,
                PixelDemand = pixelDemand,
                LinkedFromLibraries = true,
                VertexSetSignature = SetSignature(vertexBindings, vertexPush),
                PixelSetSignature = SetSignature(pixelBindings, push: false),
                Layout = CreateLibraryPipelineLayout(vertexSet, heap, pixelSet, mesh),
                UsesBindlessHeap = heap,
                Description = description,
                ProfileVertexHash = description.VertexStage.Hash,
                ProfilePixelHash = description.PixelStage?.Hash ?? 0,
            };
        }

        private static string SetSignature(List<DescriptorSetLayoutBinding> bindings, bool push)
        {
            var builder = new StringBuilder(push ? "p" : "a");
            foreach (var binding in bindings)
            {
                builder.Append(';').Append(binding.Binding).Append(',').Append((int)binding.DescriptorType).Append(',')
                    .Append(binding.DescriptorCount).Append(',').Append((int)binding.StageFlags);
            }

            return builder.ToString();
        }

        // The layout of a linked pipeline or of one part of it: sets 0 (vertex), 1 (bindless heap) and 2 (pixel), each
        // null where the pipeline or part has none, and one push constant range for both stages.
        private PipelineLayout CreateLibraryPipelineLayout(DescriptorSetLayout vertexSet, bool heap, DescriptorSetLayout pixelSet, bool mesh)
        {
            var pushConstants = new PushConstantRange
            {
                StageFlags = (mesh ? ShaderStageFlags.MeshBitExt : ShaderStageFlags.VertexBit) | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = PushConstantBytes,
            };
            var setLayouts = stackalloc DescriptorSetLayout[3];
            setLayouts[0] = vertexSet;
            setLayouts[1] = heap ? RequireHeapSetLayout() : default;
            setLayouts[2] = pixelSet;
            var create = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                Flags = PipelineLayoutCreateFlags.IndependentSetsBitExt,
                SetLayoutCount = 3,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstants,
            };
            Check(_vk.CreatePipelineLayout(_device, &create, null, out var layout), "vkCreatePipelineLayout(independent sets)");
            return layout;
        }

        // Links a pipeline from its parts, compiling the parts no earlier pipeline compiled. info holds the whole state the
        // pipeline would be created with; each part takes its share. A part's dynamic states are the ones that apply to it
        // (baseDynamicState: all but color write enable, which only the color outputs take).
        private Result CreateLinkedGraphicsPipeline(RenderPipelineEntry entry, GraphicsPipelineDescription description,
            GraphicsPipelineCreateInfo* info, PipelineDepthStencilStateCreateInfo* depthStencil,
            PipelineDynamicStateCreateInfo* baseDynamicState, out Pipeline pipeline, out double milliseconds)
        {
            var started = Stopwatch.GetTimestamp();
            var mesh = description.VertexStage.Stage == ShaderStageKind.Mesh;
            var label = $"vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16}";
            var parts = stackalloc Pipeline[4];
            var count = 0u;
            if (!mesh)
            {
                parts[count++] = GraphicsLibrary(VertexInputKey(info, baseDynamicState), GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt,
                    info, depthStencil, baseDynamicState, entry, mesh, label);
            }

            parts[count++] = GraphicsLibrary(PreRasterizationKey(entry, info, mesh, baseDynamicState), GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt,
                info, depthStencil, baseDynamicState, entry, mesh, label);
            parts[count++] = GraphicsLibrary(FragmentKey(entry, info, depthStencil, mesh, baseDynamicState), GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt,
                info, depthStencil, baseDynamicState, entry, mesh, label);
            parts[count++] = GraphicsLibrary(OutputKey(info), GraphicsPipelineLibraryFlagsEXT.FragmentOutputInterfaceBitExt,
                info, depthStencil, baseDynamicState, entry, mesh, label);
            for (var index = 0; index < count; index++)
            {
                if (parts[index].Handle == 0)
                {
                    pipeline = default;
                    milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    return Result.ErrorInitializationFailed;
                }
            }

            var libraries = new PipelineLibraryCreateInfoKHR
            {
                SType = StructureType.PipelineLibraryCreateInfoKhr,
                LibraryCount = count,
                PLibraries = parts,
            };
            var link = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &libraries,
                Layout = entry.Layout,
            };
            Pipeline linked;
            var result = _vk.CreateGraphicsPipelines(_device, default, 1, &link, null, &linked);
            pipeline = linked;
            milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return result;
        }

        private Pipeline GraphicsLibrary(string key, GraphicsPipelineLibraryFlagsEXT part, GraphicsPipelineCreateInfo* info,
            PipelineDepthStencilStateCreateInfo* depthStencil, PipelineDynamicStateCreateInfo* baseDynamicState,
            RenderPipelineEntry entry, bool mesh, string label)
        {
            if (_graphicsLibraries.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var partInfo = new GraphicsPipelineLibraryCreateInfoEXT
            {
                SType = StructureType.GraphicsPipelineLibraryCreateInfoExt,
                PNext = info->PNext,
                Flags = part,
            };
            var library = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &partInfo,
                Flags = PipelineCreateFlags.CreateLibraryBitKhr,
                PDynamicState = baseDynamicState,
            };
            PipelineLayout layout = default;
            switch (part)
            {
                case GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt:
                    library.PVertexInputState = info->PVertexInputState;
                    library.PInputAssemblyState = info->PInputAssemblyState;
                    break;
                case GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt:
                    library.StageCount = 1;
                    library.PStages = info->PStages;
                    library.PViewportState = info->PViewportState;
                    library.PRasterizationState = info->PRasterizationState;
                    library.Layout = layout = CreateLibraryPipelineLayout(entry.SetLayout,
                        entry.UsesBindlessHeap, default, mesh);
                    break;
                case GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt:
                    library.StageCount = info->StageCount > 1 ? 1u : 0u;
                    library.PStages = info->StageCount > 1 ? info->PStages + 1 : null;
                    library.PMultisampleState = info->PMultisampleState;
                    library.PDepthStencilState = depthStencil;
                    library.Layout = layout = CreateLibraryPipelineLayout(default, entry.UsesBindlessHeap,
                        entry.PixelSetLayout, mesh);
                    break;
                default:
                    library.PColorBlendState = info->PColorBlendState;
                    library.PMultisampleState = info->PMultisampleState;
                    library.PDynamicState = info->PDynamicState;
                    break;
            }

            Pipeline created;
            double milliseconds;
            Result result;
            try
            {
                result = CreatePipelineWithBinaries(&library, &library.PNext, default, &CreateGraphicsPipelineCall,
                    out created, out milliseconds, PipelineCreateFlags2.Vk2LibraryBitKhr);
            }
            finally
            {
                if (layout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, layout, null);
                }
            }

            if (result != Result.Success)
            {
                Console.Error.WriteLine($"[GPU][WARN] Graphics pipeline library failed: part={PartName(part)} {label} result={result}; the pipeline is built whole.");
                return default;
            }

            if (milliseconds >= SlowPipelineCreationMilliseconds)
            {
                Console.Error.WriteLine($"[GPU][INFO] Graphics pipeline library compiled: part={PartName(part)} {label} ms={milliseconds:F0} libraries={_graphicsLibraries.Count + 1}");
            }

            _graphicsLibraries.Add(key, created);
            return created;
        }

        private static string PartName(GraphicsPipelineLibraryFlagsEXT part) => part switch
        {
            GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt => "vertex-input",
            GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt => "vertex",
            GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt => "pixel",
            _ => "output",
        };

        private static string DynamicKey(PipelineDynamicStateCreateInfo* dynamicState)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < dynamicState->DynamicStateCount; index++)
            {
                builder.Append(',').Append((int)dynamicState->PDynamicStates[index]);
            }

            return builder.ToString();
        }

        private static string MultisampleKey(PipelineMultisampleStateCreateInfo* multisample) =>
            $"{(int)multisample->RasterizationSamples},{(bool)multisample->SampleShadingEnable},{multisample->MinSampleShading}";

        private static string VertexInputKey(GraphicsPipelineCreateInfo* info, PipelineDynamicStateCreateInfo* dynamicState)
        {
            var input = info->PVertexInputState;
            var builder = new StringBuilder("vi");
            for (var index = 0; index < input->VertexBindingDescriptionCount; index++)
            {
                var binding = input->PVertexBindingDescriptions[index];
                builder.Append(";b").Append(binding.Binding).Append(',').Append(binding.Stride).Append(',').Append((int)binding.InputRate);
            }

            for (var index = 0; index < input->VertexAttributeDescriptionCount; index++)
            {
                var attribute = input->PVertexAttributeDescriptions[index];
                builder.Append(";a").Append(attribute.Location).Append(',').Append(attribute.Binding).Append(',')
                    .Append((int)attribute.Format).Append(',').Append(attribute.Offset);
            }

            var assembly = info->PInputAssemblyState;
            builder.Append(";t").Append((int)assembly->Topology).Append(',').Append((bool)assembly->PrimitiveRestartEnable)
                .Append(";d").Append(DynamicKey(dynamicState));
            return builder.ToString();
        }

        private static string PreRasterizationKey(RenderPipelineEntry entry, GraphicsPipelineCreateInfo* info, bool mesh,
            PipelineDynamicStateCreateInfo* dynamicState)
        {
            var stage = info->PStages[0];
            var viewport = info->PViewportState;
            var clipControl = viewport->PNext is null ? "-" : ((bool)((PipelineViewportDepthClipControlCreateInfoEXT*)viewport->PNext)->NegativeOneToOne).ToString();
            var raster = info->PRasterizationState;
            var depthClip = raster->PNext is null ? "-" : ((bool)((PipelineRasterizationDepthClipStateCreateInfoEXT*)raster->PNext)->DepthClipEnable).ToString();
            // Depth bias enable and line width are dynamic (CreateRenderPipeline's dynamic states).
            return $"pr;{stage.Module.Handle:X};{(int)stage.Stage};{(int)stage.Flags};{entry.VertexSetSignature};{entry.UsesBindlessHeap};{mesh};" +
                $"{clipControl};{(int)raster->PolygonMode};{(int)raster->CullMode};{(int)raster->FrontFace};{depthClip};" +
                $"{(bool)raster->DepthClampEnable};{(bool)raster->RasterizerDiscardEnable};" +
                DynamicKey(dynamicState);
        }

        private static string FragmentKey(RenderPipelineEntry entry, GraphicsPipelineCreateInfo* info,
            PipelineDepthStencilStateCreateInfo* depthStencil, bool mesh, PipelineDynamicStateCreateInfo* dynamicState)
        {
            var pixelModule = info->StageCount > 1 ? info->PStages[1].Module.Handle : 0;
            var ds = depthStencil;
            // Depth test, depth write, the compare op and the stencil masks and reference are dynamic.
            return $"fs;{pixelModule:X};{entry.PixelSetSignature};{entry.UsesBindlessHeap};{mesh};{MultisampleKey(info->PMultisampleState)};" +
                $"{(bool)ds->DepthBoundsTestEnable},{(bool)ds->StencilTestEnable},{StencilKey(ds->Front)},{StencilKey(ds->Back)}," +
                $"{ds->MinDepthBounds},{ds->MaxDepthBounds};" +
                DynamicKey(dynamicState);
        }

        private static string StencilKey(StencilOpState state) =>
            $"{(int)state.FailOp}/{(int)state.PassOp}/{(int)state.DepthFailOp}/{(int)state.CompareOp}";

        private static string OutputKey(GraphicsPipelineCreateInfo* info)
        {
            var blend = info->PColorBlendState;
            var rendering = (PipelineRenderingCreateInfo*)info->PNext;
            var builder = new StringBuilder("fo;");
            builder.Append((bool)blend->LogicOpEnable).Append(',').Append((int)blend->LogicOp);
            for (var index = 0; index < blend->AttachmentCount; index++)
            {
                var attachment = blend->PAttachments[index];
                builder.Append(";c").Append((int)attachment.ColorWriteMask).Append(',').Append((bool)attachment.BlendEnable).Append(',')
                    .Append((int)attachment.SrcColorBlendFactor).Append(',').Append((int)attachment.DstColorBlendFactor).Append(',')
                    .Append((int)attachment.ColorBlendOp).Append(',').Append((int)attachment.SrcAlphaBlendFactor).Append(',')
                    .Append((int)attachment.DstAlphaBlendFactor).Append(',').Append((int)attachment.AlphaBlendOp);
            }

            builder.Append(";f");
            for (var index = 0; index < rendering->ColorAttachmentCount; index++)
            {
                builder.Append(',').Append((int)rendering->PColorAttachmentFormats[index]);
            }

            builder.Append(";z").Append((int)rendering->DepthAttachmentFormat).Append(',').Append((int)rendering->StencilAttachmentFormat)
                .Append(';').Append(MultisampleKey(info->PMultisampleState)).Append(";d").Append(DynamicKey(info->PDynamicState));
            return builder.ToString();
        }

        private void DestroyGraphicsLibraries()
        {
            foreach (var library in _graphicsLibraries.Values)
            {
                _vk.DestroyPipeline(_device, library, null);
            }

            _graphicsLibraries.Clear();
        }
    }
}
