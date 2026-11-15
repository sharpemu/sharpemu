// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// Graphics pipeline libraries (VK_EXT_graphics_pipeline_library). A graphics pipeline compiles its vertex and pixel programs
// together, so a program that many pipelines share (a material vertex shader paired with each material's pixel shader, or
// a pixel shader drawn into other targets) is compiled again for each of them. With libraries each part compiles once:
// the vertex input, the vertex or mesh stage with its rasterization state, the pixel stage, and the color outputs. A
// pipeline links its parts without optimizing across them.
// Parts link only through layouts with independent sets: the vertex stage's bindings are set 0 and the pixel stage's set 2
// (PixelDescriptorSet: each pixel module's set-0 variables move there), the bindless heap stays set 1. The vertex set is
// pushed when it fits, the pixel set is allocated, so each set's layout depends on its own stage alone. The heap set is in
// every part's layout, read by its stage or not: its bindings are visible to every graphics stage, and parts with
// independent sets may leave out only a set none of the other part's stages can see.
// Every depth and stencil state is dynamic (BaseDynamicStates), so the pixel stage's part depends on the pixel program
// alone. It starts compiling on a worker as soon as the program is translated (PixelProgramCreated), usually before the
// draw that needs it, and the programs a scene brings at once compile side by side instead of one draw after another.
// SHARPEMU_GRAPHICS_PIPELINE_LIBRARY=0 keeps whole pipelines with one set; SHARPEMU_EARLY_PIXEL_LIBRARIES=0 compiles each
// part when a pipeline first needs it.
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

        private static readonly bool _earlyPixelLibraries =
            Environment.GetEnvironmentVariable("SHARPEMU_EARLY_PIXEL_LIBRARIES") != "0";

        // Device setup sets this when the device links libraries fast.
        private bool _graphicsPipelineLibraries;

        // The compiled parts by the state each holds, and the parts a thread is compiling now, both under
        // _graphicsLibraryLock: pipeline workers and early pixel compiles share them with the command stream. The parts
        // are destroyed with the pipelines.
        private readonly object _graphicsLibraryLock = new();
        private readonly Dictionary<string, Pipeline> _graphicsLibraries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<Pipeline>> _graphicsLibraryCompiles = new(StringComparer.Ordinal);

        // The early pixel compiles not yet waited for; teardown waits for them before the modules go.
        private readonly List<Task> _earlyPixelLibraryTasks = new();

        private static readonly SemaphoreSlim _pixelLibrarySlots = new(Math.Max(2, Environment.ProcessorCount / 2));

        // The states every guest graphics pipeline takes from the draw (SetDynamicState), in this order.
        private DynamicState[]? _baseDynamicStates;

        // Tests: told the set layouts 0, 1 and 2 of each layout CreateLibraryPipelineLayout makes.
        internal Action<DescriptorSetLayout, DescriptorSetLayout, DescriptorSetLayout>? LibraryLayoutCreatedForTest;

        private DynamicState[] BaseDynamicStates => _baseDynamicStates ??= BuildBaseDynamicStates();

        private DynamicState[] BuildBaseDynamicStates()
        {
            var states = new List<DynamicState>
            {
                DynamicState.Viewport,
                DynamicState.Scissor,
                DynamicState.LineWidth,
                DynamicState.DepthTestEnableExt,
                DynamicState.DepthWriteEnableExt,
                DynamicState.DepthCompareOpExt,
                DynamicState.DepthBiasEnableExt,
                DynamicState.DepthBias,
                DynamicState.StencilCompareMask,
                DynamicState.StencilReference,
                DynamicState.StencilWriteMask,
                DynamicState.BlendConstants,
                DynamicState.StencilTestEnableExt,
                DynamicState.StencilOpExt,
            };
            if (_supportsDepthBounds)
            {
                states.Add(DynamicState.DepthBoundsTestEnableExt);
                states.Add(DynamicState.DepthBounds);
            }

            return states.ToArray();
        }

        // The push constant stages of every linked pipeline and part. Linked parts need equal ranges, so vertex and mesh
        // pipelines share them.
        internal ShaderStageFlags LibraryPushStages =>
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | (_supportsMeshShader ? ShaderStageFlags.MeshBitExt : 0);

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
            var vertexBindings = new List<DescriptorSetLayoutBinding>();
            CollectVertexStageBindings(vertexBindings, description);
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

            var heap = UsesBindlessHeap(description.VertexStage) || (description.PixelStage is { } heapStage && UsesBindlessHeap(heapStage));
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
                Layout = CreateLibraryPipelineLayout(vertexSet, pixelSet),
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

        // The layout of a linked pipeline or of one part of it: sets 0 (vertex) and 2 (pixel), each null where the pipeline or
        // part has none, the bindless heap's set 1 whenever the device has the heap, and one push constant range for every
        // stage.
        private PipelineLayout CreateLibraryPipelineLayout(DescriptorSetLayout vertexSet, DescriptorSetLayout pixelSet)
        {
            // Linked parts need equal ranges: with task launches on, every part carries the task range too.
            var pushRanges = stackalloc PushConstantRange[2];
            pushRanges[0] = new PushConstantRange
            {
                StageFlags = LibraryPushStages,
                Offset = 0,
                Size = PushConstantBytes,
            };
            var pushRangeCount = 1u;
            if (_supportsTaskShader)
            {
                pushRanges[pushRangeCount++] = TaskPushRange;
            }
            var setLayouts = stackalloc DescriptorSetLayout[3];
            setLayouts[0] = vertexSet;
            setLayouts[1] = _heapSetLayout;
            setLayouts[2] = pixelSet;
            var create = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                Flags = PipelineLayoutCreateFlags.IndependentSetsBitExt,
                SetLayoutCount = 3,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = pushRangeCount,
                PPushConstantRanges = pushRanges,
            };
            Check(_vk.CreatePipelineLayout(_device, &create, null, out var layout), "vkCreatePipelineLayout(independent sets)");
            LibraryLayoutCreatedForTest?.Invoke(setLayouts[0], setLayouts[1], setLayouts[2]);
            return layout;
        }

        // Links the pipeline from its parts when its entry is laid out for them, else builds it whole, as when a part fails.
        // A tessellated pipeline links too: its pre-rasterization part holds the control and evaluation stages, so its pixel
        // part, often the largest, is the one the prewarm or an earlier pipeline compiled.
        private Result LinkOrBuildGraphicsPipeline(RenderPipelineEntry? libraries, GraphicsPipelineDescription description,
            bool expandRectangles, GraphicsPipelineCreateInfo* info, PipelineDynamicStateCreateInfo* baseDynamicState,
            PipelineCache cache, out Pipeline pipeline, out double milliseconds)
        {
            if (libraries is { LinkedFromLibraries: true } && !expandRectangles &&
                CreateLinkedGraphicsPipeline(libraries, description, info, baseDynamicState, out pipeline, out milliseconds) == Result.Success)
            {
                return Result.Success;
            }

            return CreatePipelineWithBinaries(info, &info->PNext, cache, &CreateGraphicsPipelineCall, out pipeline,
                out milliseconds);
        }

        // Links a pipeline from its parts, compiling the parts no earlier pipeline compiled. info holds the whole state the
        // pipeline would be created with; each part takes its share. A part's dynamic states are the ones that apply to it
        // (baseDynamicState: all but color write enable, which only the color outputs take).
        private Result CreateLinkedGraphicsPipeline(RenderPipelineEntry entry, GraphicsPipelineDescription description,
            GraphicsPipelineCreateInfo* info, PipelineDynamicStateCreateInfo* baseDynamicState, out Pipeline pipeline,
            out double milliseconds)
        {
            var started = Stopwatch.GetTimestamp();
            var mesh = description.VertexStage.Stage == ShaderStageKind.Mesh;
            var label = $"vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16}";
            var vertexInputKey = mesh ? null : VertexInputKey(info, baseDynamicState);
            var preRasterizationKey = PreRasterizationKey(entry, info, baseDynamicState);
            var pixelIndex = StageIndex(info, ShaderStageFlags.FragmentBit);
            var pixelModule = pixelIndex >= 0 ? info->PStages[pixelIndex].Module.Handle : 0;
            var fragmentKey = FragmentKey(pixelModule, entry.PixelSetSignature, info->PMultisampleState, baseDynamicState);
            var outputKey = OutputKey(info);
            var parts = stackalloc Pipeline[4];
            var count = 0u;
            if (vertexInputKey is not null)
            {
                parts[count++] = GraphicsLibrary(vertexInputKey, GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt,
                    info, baseDynamicState, entry, label);
            }

            parts[count++] = GraphicsLibrary(preRasterizationKey, GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt,
                info, baseDynamicState, entry, label);
            parts[count++] = GraphicsLibrary(fragmentKey, GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt, info, baseDynamicState, entry, label);
            parts[count++] = GraphicsLibrary(outputKey, GraphicsPipelineLibraryFlagsEXT.FragmentOutputInterfaceBitExt,
                info, baseDynamicState, entry, label);
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

        // The part for key from the pipeline state in info: compiled by this thread unless it is compiled already or another
        // thread compiles it now. A part that fails is not kept; its pipeline is built whole.
        private Pipeline GraphicsLibrary(string key, GraphicsPipelineLibraryFlagsEXT part, GraphicsPipelineCreateInfo* info,
            PipelineDynamicStateCreateInfo* baseDynamicState, RenderPipelineEntry entry, string label)
        {
            if (TryTakeGraphicsLibrary(key, out var library, out var compile))
            {
                return library;
            }

            var created = default(Pipeline);
            try
            {
                var partInfo = new GraphicsPipelineLibraryCreateInfoEXT
                {
                    SType = StructureType.GraphicsPipelineLibraryCreateInfoExt,
                    PNext = info->PNext,
                    Flags = part,
                };
                var create = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &partInfo,
                    Flags = PipelineCreateFlags.CreateLibraryBitKhr,
                    PDynamicState = baseDynamicState,
                };
                // Every depth and stencil state is dynamic.
                var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
                PipelineLayout layout = default;
                switch (part)
                {
                    case GraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitExt:
                        create.PVertexInputState = info->PVertexInputState;
                        create.PInputAssemblyState = info->PInputAssemblyState;
                        break;
                    case GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt:
                        // Built as the prewarm builds it (CreatePreRasterizationLibrary).
                        created = CompilePreRasterizationPart(info, entry, label);
                        return created;
                    case GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt:
                        var fragmentIndex = StageIndex(info, ShaderStageFlags.FragmentBit);
                        create.StageCount = fragmentIndex >= 0 ? 1u : 0u;
                        create.PStages = fragmentIndex >= 0 ? info->PStages + fragmentIndex : null;
                        create.PMultisampleState = info->PMultisampleState;
                        create.PDepthStencilState = &depthStencil;
                        create.Layout = layout = CreateLibraryPipelineLayout(default, entry.PixelSetLayout);
                        break;
                    default:
                        create.PColorBlendState = info->PColorBlendState;
                        create.PMultisampleState = info->PMultisampleState;
                        create.PDynamicState = info->PDynamicState;
                        break;
                }

                try
                {
                    created = CompileGraphicsLibrary(&create, part, label);
                }
                finally
                {
                    if (layout.Handle != 0)
                    {
                        _vk.DestroyPipelineLayout(_device, layout, null);
                    }
                }
            }
            finally
            {
                FinishGraphicsLibrary(key, compile!, created);
            }

            return created;
        }

        // The pre-rasterization part of a linked pipeline, from the draw's stage and rasterization state, as the
        // graphics prewarm builds it from a recorded state: both compile one create info through the pipeline cache.
        private Pipeline CompilePreRasterizationPart(GraphicsPipelineCreateInfo* info, RenderPipelineEntry entry, string label)
        {
            var state = PreRasterizationStateOf(info);
            var taskIndex = StageIndex(info, ShaderStageFlags.TaskBitExt);
            var controlIndex = StageIndex(info, ShaderStageFlags.TessellationControlBit);
            var evaluationIndex = StageIndex(info, ShaderStageFlags.TessellationEvaluationBit);
            var tessellated = controlIndex >= 0 && evaluationIndex >= 0 && info->PTessellationState is not null;
            var tessellationStages = stackalloc PipelineShaderStageCreateInfo[2];
            if (tessellated)
            {
                tessellationStages[0] = info->PStages[controlIndex];
                tessellationStages[1] = info->PStages[evaluationIndex];
            }

            var layout = CreateLibraryPipelineLayout(entry.SetLayout, default);
            try
            {
                var created = CreatePreRasterizationLibrary(info->PStages[0], taskIndex >= 0 ? info->PStages + taskIndex : null,
                    state, layout, label, tessellated ? tessellationStages : null, tessellated ? info->PTessellationState : null);
                // The prewarm records parts of one vertex or mesh stage.
                if (created.Handle != 0 && !tessellated)
                {
                    RecordPreRasterization(info->PStages[0].Module.Handle, state);
                }

                return created;
            }
            finally
            {
                _vk.DestroyPipelineLayout(_device, layout, null);
            }
        }

        private static PreRasterizationState PreRasterizationStateOf(GraphicsPipelineCreateInfo* info)
        {
            var raster = info->PRasterizationState;
            var viewport = info->PViewportState;
            return new PreRasterizationState(
                (int)raster->PolygonMode,
                (int)raster->CullMode,
                (int)raster->FrontFace,
                raster->DepthClampEnable,
                raster->RasterizerDiscardEnable,
                raster->PNext is not null && ((PipelineRasterizationDepthClipStateCreateInfoEXT*)raster->PNext)->DepthClipEnable,
                viewport->PNext is not null && ((PipelineViewportDepthClipControlCreateInfoEXT*)viewport->PNext)->NegativeOneToOne);
        }

        // A pre-rasterization part from its stage (and a task stage), a rasterization state and its layout. It takes no
        // attachment formats, which belong to the color outputs part, so the graphics prewarm builds the same create
        // info from a recorded state and one part serves pipelines of any attachments.
        private Pipeline CreatePreRasterizationLibrary(PipelineShaderStageCreateInfo stage, PipelineShaderStageCreateInfo* task,
            PreRasterizationState state, PipelineLayout layout, string label,
            PipelineShaderStageCreateInfo* tessellationStages = null, PipelineTessellationStateCreateInfo* tessellation = null)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[4];
            var stageCount = 0u;
            stages[stageCount++] = stage;
            if (task is not null)
            {
                stages[stageCount++] = *task;
            }

            if (tessellationStages is not null)
            {
                stages[stageCount++] = tessellationStages[0];
                stages[stageCount++] = tessellationStages[1];
            }

            var clipControl = new PipelineViewportDepthClipControlCreateInfoEXT
            {
                SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt,
                NegativeOneToOne = state.NegativeOneToOne,
            };
            var viewport = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                PNext = _supportsDepthClipControl ? &clipControl : null,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var depthClip = new PipelineRasterizationDepthClipStateCreateInfoEXT
            {
                SType = StructureType.PipelineRasterizationDepthClipStateCreateInfoExt,
                DepthClipEnable = state.DepthClipEnable,
            };
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PNext = _supportsDepthClipEnable ? &depthClip : null,
                DepthClampEnable = state.DepthClampEnable,
                RasterizerDiscardEnable = state.RasterizerDiscardEnable,
                PolygonMode = (PolygonMode)state.PolygonMode,
                CullMode = (CullModeFlags)state.CullMode,
                FrontFace = (FrontFace)state.FrontFace,
                LineWidth = 1,
            };
            var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo };
            var states = BaseDynamicStates;
            fixed (DynamicState* statePointer = states)
            {
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)states.Length,
                    PDynamicStates = statePointer,
                };
                var partInfo = new GraphicsPipelineLibraryCreateInfoEXT
                {
                    SType = StructureType.GraphicsPipelineLibraryCreateInfoExt,
                    PNext = &rendering,
                    Flags = GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt,
                };
                var create = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &partInfo,
                    Flags = PipelineCreateFlags.CreateLibraryBitKhr,
                    StageCount = stageCount,
                    PStages = stages,
                    PTessellationState = tessellation,
                    PViewportState = &viewport,
                    PRasterizationState = &rasterization,
                    PDynamicState = &dynamicState,
                    Layout = layout,
                };
                return CompileGraphicsLibrary(&create, GraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitExt, label);
            }
        }

        // The vertex or mesh stage of a pipeline or pre-rasterization part; the graphics prewarm builds the same one.
        private static PipelineShaderStageCreateInfo VertexStageInfo(bool mesh, ShaderModule module, byte* entryPoint) => new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = mesh ? ShaderStageFlags.MeshBitExt : ShaderStageFlags.VertexBit,
            Flags = mesh ? PipelineShaderStageCreateFlags.RequireFullSubgroupsBit : 0,
            Module = module,
            PName = entryPoint,
        };

        // True with a compiled part, after waiting while another thread compiles it; false with the completion this
        // thread finishes (FinishGraphicsLibrary) once it has compiled the part itself.
        private bool TryTakeGraphicsLibrary(string key, out Pipeline library, out TaskCompletionSource<Pipeline>? compile)
        {
            while (true)
            {
                TaskCompletionSource<Pipeline>? running;
                lock (_graphicsLibraryLock)
                {
                    if (_graphicsLibraries.TryGetValue(key, out library))
                    {
                        compile = null;
                        return true;
                    }

                    if (!_graphicsLibraryCompiles.TryGetValue(key, out running))
                    {
                        compile = new TaskCompletionSource<Pipeline>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _graphicsLibraryCompiles.Add(key, compile);
                        return false;
                    }
                }

                library = running.Task.GetAwaiter().GetResult();
                if (library.Handle != 0)
                {
                    compile = null;
                    return true;
                }

                // The other thread's compile failed: this thread tries once more itself.
            }
        }

        private void FinishGraphicsLibrary(string key, TaskCompletionSource<Pipeline> compile, Pipeline created)
        {
            lock (_graphicsLibraryLock)
            {
                _graphicsLibraryCompiles.Remove(key);
                if (created.Handle != 0)
                {
                    _graphicsLibraries.Add(key, created);
                }
            }

            compile.SetResult(created);
        }

        // A part is compiled through the pipeline cache, not from and into pipeline binaries. The vertex input and color
        // output parts hold no code: the driver gives them an all-zero pipeline key and no binaries, so on the binary path
        // every one of them waited on the same build gate and looked for a binary that is never stored. The parts with code
        // reach later pipelines through the cache, which the graphics prewarm fills before the session's draws ask for them.
        private Pipeline CompileGraphicsLibrary(GraphicsPipelineCreateInfo* create, GraphicsPipelineLibraryFlagsEXT part, string label)
        {
            var started = Stopwatch.GetTimestamp();
            var created = default(Pipeline);
            var result = _vk.CreateGraphicsPipelines(_device, _pipelineCache, 1, create, null, &created);
            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result == Result.Success)
            {
                MarkPipelineCacheDirty();
            }

            if (result != Result.Success)
            {
                Console.Error.WriteLine($"[GPU][WARN] Graphics pipeline library failed: part={PartName(part)} {label} result={result}; the pipeline is built whole.");
                return default;
            }

            if (milliseconds >= SlowPipelineCreationMilliseconds)
            {
                var module = create->StageCount != 0 ? create->PStages[0].Module.Handle : 0;
                int count;
                lock (_graphicsLibraryLock)
                {
                    count = _graphicsLibraries.Count + 1;
                }

                Console.Error.WriteLine($"[GPU][INFO] Graphics pipeline library compiled: part={PartName(part)} {label} ms={milliseconds:F0} " +
                    $"module=0x{module:X} libraries={count}");
            }

            return created;
        }

        // A pixel program was translated: its pixel stage part is compiled on a worker for the sample state draws give it
        // (one sample, no sample shading), so the draws that use the program link the part instead of compiling it, and
        // the programs a scene brings compile side by side.
        public void PixelProgramCreated(ShaderProgramInfo program, ulong module)
        {
            if (!_graphicsPipelineLibraries || !_earlyPixelLibraries || program.Bindings is null || module == 0)
            {
                return;
            }

            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, program, ShaderStage.Pixel);
            var multisample = SingleSampleState();
            var states = BaseDynamicStates;
            string key;
            fixed (DynamicState* statePointer = states)
            {
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = (uint)states.Length,
                    PDynamicStates = statePointer,
                };
                key = FragmentKey(module, SetSignature(bindings, push: false), &multisample, &dynamicState);
            }

            lock (_graphicsLibraryLock)
            {
                if (_graphicsLibraries.ContainsKey(key) || _graphicsLibraryCompiles.ContainsKey(key))
                {
                    return;
                }
            }

            var label = $"vs=early ps=0x{program.Hash:X16}";
            var task = Task.Factory.StartNew(
                () =>
                {
                    _pixelLibrarySlots.Wait();
                    try
                    {
                        CompileEarlyPixelLibrary(key, module, bindings, label);
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"[GPU][WARN] Early pixel library failed: {label} {exception.Message}");
                    }
                    finally
                    {
                        _pixelLibrarySlots.Release();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            lock (_earlyPixelLibraryTasks)
            {
                _earlyPixelLibraryTasks.RemoveAll(static item => item.IsCompleted);
                _earlyPixelLibraryTasks.Add(task);
            }
        }

        private static PipelineMultisampleStateCreateInfo SingleSampleState() => new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
            MinSampleShading = 1f,
        };

        // The pixel stage part GraphicsLibrary builds for a one-sample pipeline of this program.
        private void CompileEarlyPixelLibrary(string key, ulong module, List<DescriptorSetLayoutBinding> bindings, string label)
        {
            if (TryTakeGraphicsLibrary(key, out _, out var compile))
            {
                return;
            }

            var created = default(Pipeline);
            try
            {
                created = CreateEarlyPixelLibrary(module, bindings, label);
            }
            finally
            {
                FinishGraphicsLibrary(key, compile!, created);
            }
        }

        // The part itself; the pixel prewarm builds it the same way into the pipeline cache.
        private Pipeline CreateEarlyPixelLibrary(ulong module, List<DescriptorSetLayoutBinding> bindings, string label)
        {
            var created = default(Pipeline);
            DescriptorSetLayout pixelSet = default;
            PipelineLayout layout = default;
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                if (bindings.Count != 0)
                {
                    pixelSet = CreateDescriptorSetLayout(bindings, allowPush: false, out _, out _);
                }

                layout = CreateLibraryPipelineLayout(default, pixelSet);
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = new ShaderModule(module),
                    PName = entryPoint,
                };
                var multisample = SingleSampleState();
                var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
                var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo };
                var states = BaseDynamicStates;
                fixed (DynamicState* statePointer = states)
                {
                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = (uint)states.Length,
                        PDynamicStates = statePointer,
                    };
                    var partInfo = new GraphicsPipelineLibraryCreateInfoEXT
                    {
                        SType = StructureType.GraphicsPipelineLibraryCreateInfoExt,
                        PNext = &rendering,
                        Flags = GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt,
                    };
                    var create = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        PNext = &partInfo,
                        Flags = PipelineCreateFlags.CreateLibraryBitKhr,
                        StageCount = 1,
                        PStages = &stage,
                        PMultisampleState = &multisample,
                        PDepthStencilState = &depthStencil,
                        PDynamicState = &dynamicState,
                        Layout = layout,
                    };
                    created = CompileGraphicsLibrary(&create, GraphicsPipelineLibraryFlagsEXT.FragmentShaderBitExt, label);
                }
            }
            finally
            {
                if (layout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, layout, null);
                }

                if (pixelSet.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, pixelSet, null);
                }

                SilkMarshal.Free((nint)entryPoint);
            }

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

        // A task stage and its specialization values, or nothing.
        private static string TaskKey(GraphicsPipelineCreateInfo* info)
        {
            var index = StageIndex(info, ShaderStageFlags.TaskBitExt);
            if (index < 0) return "";
            var builder = new System.Text.StringBuilder(";task");
            var specialization = info->PStages[index].PSpecializationInfo;
            if (specialization is not null)
            {
                var values = (uint*)specialization->PData;
                for (var word = 0; word < (int)(specialization->DataSize / sizeof(uint)); word++)
                    builder.Append(':').Append(values[word]);
            }

            return builder.ToString();
        }

        // The index of the stage of this kind in info, or -1.
        // The control and evaluation modules and the patch size of a tessellated part.
        private static string TessellationKey(GraphicsPipelineCreateInfo* info)
        {
            var control = StageIndex(info, ShaderStageFlags.TessellationControlBit);
            var evaluation = StageIndex(info, ShaderStageFlags.TessellationEvaluationBit);
            return control < 0 || evaluation < 0 || info->PTessellationState is null
                ? ""
                : $";tess:{info->PStages[control].Module.Handle:X}:{info->PStages[evaluation].Module.Handle:X}:" +
                    $"{info->PTessellationState->PatchControlPoints}";
        }

        private static int StageIndex(GraphicsPipelineCreateInfo* info, ShaderStageFlags stage)
        {
            for (var index = 0; index < (int)info->StageCount; index++)
            {
                if (info->PStages[index].Stage == stage)
                {
                    return index;
                }
            }

            return -1;
        }

        // Depth bias enable and line width are dynamic.
        private static string PreRasterizationKey(RenderPipelineEntry entry, GraphicsPipelineCreateInfo* info,
            PipelineDynamicStateCreateInfo* dynamicState)
        {
            var stage = info->PStages[0];
            var task = TaskKey(info) + TessellationKey(info);
            var viewport = info->PViewportState;
            var clipControl = viewport->PNext is null ? "-" : ((bool)((PipelineViewportDepthClipControlCreateInfoEXT*)viewport->PNext)->NegativeOneToOne).ToString();
            var raster = info->PRasterizationState;
            var depthClip = raster->PNext is null ? "-" : ((bool)((PipelineRasterizationDepthClipStateCreateInfoEXT*)raster->PNext)->DepthClipEnable).ToString();
            return $"pr;{stage.Module.Handle:X};{(int)stage.Stage};{(int)stage.Flags}{task};{entry.VertexSetSignature};" +
                $"{clipControl};{(int)raster->PolygonMode};{(int)raster->CullMode};{(int)raster->FrontFace};{depthClip};" +
                $"{(bool)raster->DepthClampEnable};{(bool)raster->RasterizerDiscardEnable};" +
                DynamicKey(dynamicState);
        }

        // The pixel stage part depends on the program, its set and the sample state: depth and stencil state are dynamic.
        private static string FragmentKey(ulong pixelModule, string pixelSetSignature,
            PipelineMultisampleStateCreateInfo* multisample, PipelineDynamicStateCreateInfo* dynamicState) =>
            $"fs;{pixelModule:X};{pixelSetSignature};{MultisampleKey(multisample)};" + DynamicKey(dynamicState);

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

        // Waits for the early pixel compiles still running: they use program modules, which teardown destroys.
        private void WaitForEarlyPixelLibraries()
        {
            Task[] running;
            lock (_earlyPixelLibraryTasks)
            {
                running = _earlyPixelLibraryTasks.ToArray();
                _earlyPixelLibraryTasks.Clear();
            }

            try
            {
                Task.WaitAll(running);
            }
            catch (AggregateException)
            {
                // Each compile reports its own failure.
            }
        }

        private void DestroyGraphicsLibraries()
        {
            WaitForEarlyPixelLibraries();
            lock (_graphicsLibraryLock)
            {
                foreach (var library in _graphicsLibraries.Values)
                {
                    _vk.DestroyPipeline(_device, library, null);
                }

                _graphicsLibraries.Clear();
            }
        }
    }
}
