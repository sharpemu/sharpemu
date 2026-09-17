// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

// This partial creates the graphics and compute pipelines of the shader cache and the presentation pipeline.
internal static unsafe partial class VulkanVideoPresenter
{
    private const string PushDescriptorExtensionName = "VK_KHR_push_descriptor";

    private sealed partial class Presenter : IShaderPipelineHost
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private const uint PushConstantBytes = PushData.ByteSize;
        private static int _shaderModuleDumpSequence;
        private static int _vertexSwizzleLines;
        private static int _vertexOutOfBoundsLines;

        // One created pipeline with the layout it binds and the description its rectangle-list variants derive from.
        private sealed class RenderPipelineEntry
        {
            public ulong Id;
            public Pipeline Pipeline;
            public PipelineLayout Layout;
            public DescriptorSetLayout SetLayout;
            public DescriptorSetDemand Demand;
            public bool UsesPushDescriptors;
            public GraphicsPipelineDescription? Description;
            public Pipeline StripVariant;
            public Pipeline ListVariant;
            public ulong ProfileVertexHash;
            public ulong ProfilePixelHash;
            public ulong ProfileComputeHash;

            public bool RectangleList => Description is { StaticParameters.Topology: PrimitiveTopology.PatchList };
        }

        private readonly Dictionary<ulong, ShaderModule> _shaderModules = new();
        private KhrPushDescriptor _pushDescriptorApi = null!;
        private uint _maxPushDescriptors;
        private SampleCountFlags _noAttachmentSampleCounts;
        private DescriptorHeap _descriptorHeap = null!;

        uint IShaderPipelineHost.MaxPushDescriptors => _maxPushDescriptors;

        // Wave64 compute runs natively when the device's subgroup is that wide; smaller devices emulate it.
        bool IShaderPipelineHost.ComputeWave64Supported => Volatile.Read(ref _nativeSubgroupSize) >= 64;

        bool IShaderPipelineHost.GraphicsSubgroupOperationsEnabled => GraphicsSubgroupOperationsEnabled;

        RenderHostLimits IShaderPipelineHost.Limits => _renderHostLimits;

        SampleCountFlags IShaderPipelineHost.NoAttachmentSampleCounts => _noAttachmentSampleCounts;

        // A range the GPU wrote is downloaded first, so the word is what the guest CPU would read.
        public bool TryReadGuestWord(ulong address, out uint word)
        {
            using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.GuestRead);
            word = 0;
            if (!_bufferCache.TrySynchronizeCpuRead(address, sizeof(uint)))
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        // Refused while a GPU buffer or image write may still own the range.
        public bool TryReadCleanGuestWord(ulong address, out uint word)
        {
            using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.CleanGuestRead);
            word = 0;
            if (_bufferCache.HasGpuDirtyPages(address, sizeof(uint)) ||
                _bufferCache.HasGpuDirtyBytes(address, sizeof(uint)) ||
                _imageCache.HasGpuModifiedImageBytes(address, sizeof(uint)))
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramCompile);
            var module = CreateShaderModule(shader.Payload);
            SetDebugName(ObjectType.ShaderModule, module.Handle, $"SharpEmu {stage} 0x{hash:X16}");
            _shaderModules.Add(programId, module);
            return module.Handle;
        }

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
                MarkPipelineCacheDirty();
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            string? dumpPath = null;
            var dumpDirectory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
            if (!string.IsNullOrWhiteSpace(dumpDirectory))
            {
                Directory.CreateDirectory(dumpDirectory);

                var sequence = Interlocked.Increment(ref _shaderModuleDumpSequence);
                dumpPath = Path.Combine(dumpDirectory, $"{sequence:D4}.spv");
                File.WriteAllBytes(dumpPath, code);

                _pendingShaderModuleDumpPath = dumpPath;
            }

            try
            {
                fixed (byte* codePointer = code)
                {
                    var createInfo = new ShaderModuleCreateInfo
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (nuint)code.Length,
                        PCode = (uint*)codePointer,
                    };
                    Check(
                        _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                        "vkCreateShaderModule");
                    return module;
                }
            }
            finally
            {
                _pendingShaderModuleDumpPath = null;
            }
        }

        // One layout binding per descriptor binding of the stage, at the stage's native binding numbers.
        private static void CollectLayoutBindings(List<DescriptorSetLayoutBinding> bindings, ShaderProgramInfo program, ShaderStage stage)
        {
            var layout = program.Bindings ?? throw SubmissionScheduler.Fatal($"The program has no binding layout: hash=0x{program.Hash:X16}.");
            foreach (var binding in layout.Descriptors)
            {
                bindings.Add(new DescriptorSetLayoutBinding
                {
                    Binding = BindingLayout.NativeBindingIndex(stage, binding.Kind),
                    DescriptorType = DescriptorWriter.DescriptorType(binding.Kind),
                    DescriptorCount = DescriptorWriter.DescriptorCount(binding),
                    StageFlags = DescriptorWriter.ShaderStageFlag(stage),
                });
            }
        }

        // The set is pushed when its descriptors fit the device limit, else it comes from the heap.
        private DescriptorSetLayout CreateDescriptorSetLayout(List<DescriptorSetLayoutBinding> bindings, out bool usesPushDescriptors, out DescriptorSetDemand demand)
        {
            var descriptorCount = 0u;
            demand = default;
            foreach (var binding in bindings)
            {
                descriptorCount += binding.DescriptorCount;
                demand = demand.Add(DescriptorSetDemand.Of(binding.DescriptorType, binding.DescriptorCount));
            }

            usesPushDescriptors = descriptorCount <= _maxPushDescriptors;
            var bindingArray = bindings.ToArray();
            fixed (DescriptorSetLayoutBinding* bindingPointer = bindingArray)
            {
                var create = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    Flags = usesPushDescriptors ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0,
                    BindingCount = (uint)bindingArray.Length,
                    PBindings = bindingArray.Length == 0 ? null : bindingPointer,
                };
                Check(_vk.CreateDescriptorSetLayout(_device, &create, null, out var layout), "vkCreateDescriptorSetLayout");
                return layout;
            }
        }

        private PipelineLayout CreatePipelineLayout(DescriptorSetLayout setLayout, ShaderStageFlags pushStages)
        {
            var pushConstants = new PushConstantRange { StageFlags = pushStages, Offset = 0, Size = PushConstantBytes };
            var create = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstants,
            };
            Check(_vk.CreatePipelineLayout(_device, &create, null, out var layout), "vkCreatePipelineLayout");
            return layout;
        }

        private PipelineHandle RegisterPipeline(RenderPipelineEntry entry)
        {
            entry.Id = ++_nextPipelineId;
            _pipelineEntries.Add(entry.Id, entry);
            if (_gpuCommandProfile is not null)
                Console.Error.WriteLine($"[PERF][GPU_PIPELINE] pipeline={entry.Id} vertex=0x{entry.ProfileVertexHash:X16} pixel=0x{entry.ProfilePixelHash:X16} compute=0x{entry.ProfileComputeHash:X16}");
            return new PipelineHandle(entry.Id, entry.Layout.Handle, entry.UsesPushDescriptors);
        }

        public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            if (description.Rendering.ColorCount > _maxColorAttachments)
            {
                throw SubmissionScheduler.Fatal($"The draw binds more color attachments than the device supports: count={description.Rendering.ColorCount} max={_maxColorAttachments}.");
            }

            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, description.VertexStage, ShaderStage.Vertex);
            if (description.PixelStage is { } pixelStage)
            {
                CollectLayoutBindings(bindings, pixelStage, ShaderStage.Pixel);
            }

            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var entry = new RenderPipelineEntry
            {
                SetLayout = setLayout,
                Demand = demand,
                Layout = CreatePipelineLayout(setLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit),
                UsesPushDescriptors = usesPushDescriptors,
                Description = description,
                ProfileVertexHash = description.VertexStage.Hash,
                ProfilePixelHash = description.PixelStage?.Hash ?? 0,
            };
            // A rectangle list draws through a strip or list variant chosen by its vertex count.
            if (!entry.RectangleList)
            {
                entry.Pipeline = CreateRenderPipeline(description, description.StaticParameters.Topology, entry.Layout);
            }

            return RegisterPipeline(entry);
        }

        private static StencilOpState ToVkStencilOpState(in StencilOperations operations) => new()
        {
            FailOp = operations.FailOperation,
            PassOp = operations.PassOperation,
            DepthFailOp = operations.DepthFailOperation,
            CompareOp = operations.Compare,
        };

        // The host format of each attribute, narrowed to the components the program fetches.
        private static void BuildVertexAttributes(
            GraphicsPipelineDescription description,
            VertexInputAttributeDescription[] attributes,
            VertexInputBindingDescription[] bindings)
        {
            var vertexInput = description.VertexInput;
            var info = description.VertexInfo;
            for (var binding = 0; binding < vertexInput.BindingCount; binding++)
            {
                bindings[binding] = new VertexInputBindingDescription
                {
                    Binding = (uint)binding,
                    Stride = vertexInput.Bindings[binding].Stride,
                    InputRate = vertexInput.Bindings[binding].Instance ? VertexInputRate.Instance : VertexInputRate.Vertex,
                };
            }

            for (var index = 0; index < vertexInput.AttributeCount; index++)
            {
                var resource = info.Attributes[index];
                var descriptor = resource.Descriptor;
                var compiledComponents = description.VertexStage.VertexFetchComponents[index];
                var usedComponents = compiledComponents > 0 ? compiledComponents : (uint)resource.RegisterCount;
                var format = VertexAttributeFormats.Resolve(in descriptor, usedComponents, out var attributeSize);
                if (descriptor.OutOfBounds != 0 && Interlocked.Exchange(ref _vertexOutOfBoundsLines, 1) == 0)
                {
                    Console.Error.WriteLine($"[LOADER][INFO] vertex_input accepted the out-of-bounds mode {descriptor.OutOfBounds} of an attribute buffer");
                }

                if (descriptor.AddThreadId)
                {
                    throw SubmissionScheduler.Fatal($"A vertex attribute buffer adds the thread index: attribute={index} hash=0x{description.VertexStage.Hash:X16}.");
                }

                if (descriptor.SwizzleEnabled)
                {
                    throw SubmissionScheduler.Fatal($"A vertex attribute buffer is swizzled: attribute={index} hash=0x{description.VertexStage.Hash:X16}.");
                }

                CheckVertexSwizzle(in descriptor, resource.RegisterCount, attributeSize, index);
                attributes[index] = new VertexInputAttributeDescription
                {
                    Location = (uint)index,
                    Binding = vertexInput.Attributes[index].Binding,
                    Format = format,
                    Offset = vertexInput.Attributes[index].Offset,
                };
            }
        }

        private static uint DestinationSelect(uint x, uint y = 0, uint z = 0, uint w = 0) => x | (y << 3) | (z << 6) | (w << 9);

        // A destination select the fixed-function fetch cannot apply is logged once and accepted.
        private static void CheckVertexSwizzle(in BufferDescriptorWords descriptor, int registerCount, uint attributeSize, int index)
        {
            uint swizzle;
            uint expected;
            var supported = true;
            switch (registerCount)
            {
                case 1:
                    swizzle = descriptor.DestinationSelectX;
                    expected = DestinationSelect(4);
                    supported = swizzle == expected;
                    break;
                case 2:
                    swizzle = descriptor.DestinationSelectXY;
                    expected = attributeSize == 1 ? DestinationSelect(4, 0) : DestinationSelect(4, 5);
                    supported = swizzle == expected;
                    break;
                case 3:
                    swizzle = descriptor.DestinationSelectXYZ;
                    expected = attributeSize switch
                    {
                        1 => DestinationSelect(4, 0, 0),
                        2 => DestinationSelect(4, 5, 0),
                        _ => DestinationSelect(4, 5, 6),
                    };
                    supported = swizzle == expected;
                    break;
                case 4:
                    swizzle = descriptor.DestinationSelectXYZW;
                    switch (attributeSize)
                    {
                        case 1:
                            expected = DestinationSelect(4, 0, 0, 1);
                            supported = swizzle == expected;
                            break;
                        case 2:
                            expected = DestinationSelect(4, 5, 0, 1);
                            supported = swizzle == expected;
                            break;
                        case 3:
                            expected = DestinationSelect(4, 5, 6, 1);
                            supported = swizzle == expected || swizzle == DestinationSelect(4, 5, 6, 0);
                            break;
                        default:
                            expected = DestinationSelect(4, 5, 6, 7);
                            supported = swizzle == expected || swizzle == DestinationSelect(4, 5, 6, 1) || swizzle == DestinationSelect(4, 5, 6, 0);
                            break;
                    }

                    break;
                default:
                    throw SubmissionScheduler.Fatal($"A vertex attribute fills an invalid register count: attribute={index} registers={registerCount}.");
            }

            if (!supported && Interlocked.Exchange(ref _vertexSwizzleLines, 1) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] vertex_input accepted an unsupported destination select attribute={index} size={attributeSize} " +
                    $"registers={registerCount} swizzle=0x{swizzle:X3} expected=0x{expected:X3}");
            }
        }

        // One graphics pipeline for dynamic rendering: the attachment formats travel in the create info.
        private Pipeline CreateRenderPipeline(GraphicsPipelineDescription description, PrimitiveTopology topology, PipelineLayout layout)
        {
            var parameters = description.StaticParameters;
            var rendering = description.Rendering;
            var vertexModule = new ShaderModule(description.VertexProgram.Module);
            var pixelModule = description.PixelStage is null ? default : new ShaderModule(description.PixelProgram.Module);
            if (vertexModule.Handle == 0 || (description.PixelStage is not null && pixelModule.Handle == 0))
            {
                throw SubmissionScheduler.Fatal($"A graphics pipeline stage has no module: vertex=0x{vertexModule.Handle:X} pixel=0x{pixelModule.Handle:X}.");
            }

            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                var stageCount = 1u;
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                if (pixelModule.Handle != 0)
                {
                    shaderStages[stageCount++] = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.FragmentBit,
                        Module = pixelModule,
                        PName = entryPoint,
                    };
                }

                var vertexBindings = new VertexInputBindingDescription[description.VertexInput.BindingCount];
                var vertexAttributes = new VertexInputAttributeDescription[description.VertexInput.AttributeCount];
                BuildVertexAttributes(description, vertexAttributes, vertexBindings);
                var colorCount = (int)parameters.ColorCount;
                var blends = new PipelineColorBlendAttachmentState[colorCount];
                for (var index = 0; index < colorCount; index++)
                {
                    var mask = parameters.GetColorMask(index);
                    if ((mask & ~0xFu) != 0)
                    {
                        throw SubmissionScheduler.Fatal($"A color write mask has unknown bits: attachment={index} mask=0x{mask:X}.");
                    }

                    var separateAlpha = parameters.GetSeparateAlphaBlend(index);
                    blends[index] = new PipelineColorBlendAttachmentState
                    {
                        ColorWriteMask = ToVkColorWriteMask(mask),
                        BlendEnable = parameters.GetBlendEnable(index) && !parameters.GetBlendBypass(index),
                        SrcColorBlendFactor = ToVkBlendFactor(parameters.GetColorSourceBlend(index)),
                        DstColorBlendFactor = ToVkBlendFactor(parameters.GetColorDestinationBlend(index)),
                        ColorBlendOp = ToVkBlendOp(parameters.GetColorBlendFunction(index)),
                        SrcAlphaBlendFactor = ToVkBlendFactor(separateAlpha ? parameters.GetAlphaSourceBlend(index) : parameters.GetColorSourceBlend(index)),
                        DstAlphaBlendFactor = ToVkBlendFactor(separateAlpha ? parameters.GetAlphaDestinationBlend(index) : parameters.GetColorDestinationBlend(index)),
                        AlphaBlendOp = ToVkBlendOp(separateAlpha ? parameters.GetAlphaBlendFunction(index) : parameters.GetColorBlendFunction(index)),
                    };
                }

                var colorFormats = new Format[colorCount];
                Array.Copy(rendering.ColorFormats, colorFormats, colorCount);
                var cullMode = CullModeFlags.None;
                if (parameters.CullBack)
                {
                    cullMode |= CullModeFlags.BackBit;
                }

                if (parameters.CullFront)
                {
                    cullMode |= CullModeFlags.FrontBit;
                }

                fixed (VertexInputBindingDescription* bindingPointer = vertexBindings)
                fixed (VertexInputAttributeDescription* attributePointer = vertexAttributes)
                fixed (PipelineColorBlendAttachmentState* blendPointer = blends)
                fixed (Format* colorFormatPointer = colorFormats)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindings.Length,
                        PVertexBindingDescriptions = vertexBindings.Length == 0 ? null : bindingPointer,
                        VertexAttributeDescriptionCount = (uint)vertexAttributes.Length,
                        PVertexAttributeDescriptions = vertexAttributes.Length == 0 ? null : attributePointer,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = topology,
                        PrimitiveRestartEnable = parameters.PrimitiveRestartEnable,
                    };
                    var depthClipControl = new PipelineViewportDepthClipControlCreateInfoEXT
                    {
                        SType = StructureType.PipelineViewportDepthClipControlCreateInfoExt,
                        NegativeOneToOne = parameters.NegativeOneToOne,
                    };
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        PNext = _supportsDepthClipControl ? &depthClipControl : null,
                        ViewportCount = 1,
                        ScissorCount = 1,
                    };
                    var depthClip = new PipelineRasterizationDepthClipStateCreateInfoEXT
                    {
                        SType = StructureType.PipelineRasterizationDepthClipStateCreateInfoExt,
                        DepthClipEnable = parameters.DepthClipEnable,
                    };
                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        PNext = _supportsDepthClipEnable ? &depthClip : null,
                        PolygonMode = PolygonMode.Fill,
                        CullMode = cullMode,
                        FrontFace = parameters.FrontFaceClockwise ? FrontFace.Clockwise : FrontFace.CounterClockwise,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        SampleShadingEnable = parameters.SampleShadingEnable,
                        RasterizationSamples = ImageDescription.VulkanSampleCount(parameters.Samples),
                        MinSampleShading = 1f,
                    };
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        LogicOp = LogicOp.Copy,
                        AttachmentCount = (uint)blends.Length,
                        PAttachments = blends.Length == 0 ? null : blendPointer,
                    };
                    var depthStencil = new PipelineDepthStencilStateCreateInfo
                    {
                        SType = StructureType.PipelineDepthStencilStateCreateInfo,
                        DepthBoundsTestEnable = _supportsDepthBounds && parameters.DepthBoundsTestEnable,
                        StencilTestEnable = parameters.StencilTestEnable,
                        Front = ToVkStencilOpState(parameters.StencilFront),
                        Back = ToVkStencilOpState(parameters.StencilBack),
                        MinDepthBounds = parameters.DepthMinBounds,
                        MaxDepthBounds = parameters.DepthMaxBounds,
                    };
                    var dynamicStates = stackalloc DynamicState[13];
                    dynamicStates[0] = DynamicState.Viewport;
                    dynamicStates[1] = DynamicState.Scissor;
                    dynamicStates[2] = DynamicState.LineWidth;
                    dynamicStates[3] = DynamicState.DepthTestEnableExt;
                    dynamicStates[4] = DynamicState.DepthWriteEnableExt;
                    dynamicStates[5] = DynamicState.DepthCompareOpExt;
                    dynamicStates[6] = DynamicState.DepthBiasEnableExt;
                    dynamicStates[7] = DynamicState.DepthBias;
                    dynamicStates[8] = DynamicState.StencilCompareMask;
                    dynamicStates[9] = DynamicState.StencilReference;
                    dynamicStates[10] = DynamicState.StencilWriteMask;
                    dynamicStates[11] = DynamicState.BlendConstants;
                    var dynamicStateCount = 12u;
                    // Last so a pipeline without color attachments can leave it out.
                    if (_colorWriteEnableApi is not null && colorCount != 0)
                    {
                        dynamicStates[dynamicStateCount++] = DynamicState.ColorWriteEnableExt;
                    }

                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = dynamicStateCount,
                        PDynamicStates = dynamicStates,
                    };
                    var renderingInfo = new PipelineRenderingCreateInfo
                    {
                        SType = StructureType.PipelineRenderingCreateInfo,
                        ColorAttachmentCount = (uint)colorCount,
                        PColorAttachmentFormats = colorCount == 0 ? null : colorFormatPointer,
                        DepthAttachmentFormat = rendering.DepthFormat,
                        StencilAttachmentFormat = rendering.StencilFormat,
                    };
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        PNext = &renderingInfo,
                        StageCount = stageCount,
                        PStages = shaderStages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PDepthStencilState = parameters.WithDepth ? &depthStencil : null,
                        PColorBlendState = &colorBlend,
                        PDynamicState = &dynamicState,
                        Layout = layout,
                    };
                    Check(_vk.CreateGraphicsPipelines(_device, _pipelineCache, 1, &pipelineInfo, null, out var pipeline), "vkCreateGraphicsPipelines(rendering)");
                    MarkPipelineCacheDirty();
                    Interlocked.Increment(ref _perfPipelineCreations);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics vs=0x{description.VertexStage.Hash:X16} ps=0x{description.PixelStage?.Hash ?? 0:X16} colors={colorCount}");
                    return pipeline;
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
            }
        }

        public PipelineHandle CreateComputePipeline(ComputePipelineDescription description)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineSetup);
            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, description.Stage, ShaderStage.Compute);
            var setLayout = CreateDescriptorSetLayout(bindings, out var usesPushDescriptors, out var demand);
            var layout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit);
            var computeModule = new ShaderModule(description.Program.Module);
            if (computeModule.Handle == 0)
            {
                throw SubmissionScheduler.Fatal($"The compute pipeline has no module: hash=0x{description.Stage.Hash:X16}.");
            }

            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            Pipeline pipeline;
            try
            {
                var stageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stageInfo,
                    Layout = layout,
                };
                Check(_vk.CreateComputePipelines(_device, _pipelineCache, 1, &pipelineInfo, null, out pipeline), "vkCreateComputePipelines(rendering)");
                MarkPipelineCacheDirty();
                Interlocked.Increment(ref _perfPipelineCreations);
                SetDebugName(ObjectType.Pipeline, pipeline.Handle, $"SharpEmu compute cs=0x{description.Stage.Hash:X16}");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
            }

            var entry = new RenderPipelineEntry
            {
                Pipeline = pipeline,
                Layout = layout,
                SetLayout = setLayout,
                Demand = demand,
                UsesPushDescriptors = usesPushDescriptors,
                ProfileComputeHash = description.Stage.Hash,
            };
            return RegisterPipeline(entry);
        }

        private void DestroyRenderPipelines()
        {
            foreach (var entry in _pipelineEntries.Values)
            {
                foreach (var pipeline in new[] { entry.Pipeline, entry.StripVariant, entry.ListVariant })
                {
                    if (pipeline.Handle != 0)
                    {
                        _vk.DestroyPipeline(_device, pipeline, null);
                    }
                }

                _vk.DestroyPipelineLayout(_device, entry.Layout, null);
                _vk.DestroyDescriptorSetLayout(_device, entry.SetLayout, null);
            }

            _pipelineEntries.Clear();
            foreach (var module in _shaderModules.Values)
            {
                _vk.DestroyShaderModule(_device, module, null);
            }

            _shaderModules.Clear();
        }
    }
}
