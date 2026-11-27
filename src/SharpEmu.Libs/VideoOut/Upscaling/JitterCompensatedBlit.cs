// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SharpEmu.Libs.VideoOut.Upscaling;

internal sealed unsafe class JitterCompensatedBlit(Vk vk, PhysicalDevice physicalDevice, Device device, KhrPushDescriptor descriptors)
    : IDisposable
{
    private readonly Dictionary<(Format Color, Format Output), bool> _supportedFormats = [];
    private bool _initialized;
    private bool _warned;
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private Pipeline _pipeline;

    private readonly record struct BlitParameters(Vector2 FirstSample, Vector2 Step, Vector2 Minimum, Vector2 Maximum,
        Extent2D OutputSize);

    public bool TryUpscale(CommandBuffer command, SuperResolutionFrame frame, in ImageViewDescription color,
        in ImageViewDescription output)
    {
        if ((frame.Dispatch.Flags & SuperResolutionFlags.PassThrough) != 0 ||
            frame.RenderWidth == 0 || frame.RenderHeight == 0 || frame.OutputWidth == 0 || frame.OutputHeight == 0)
        {
            return false;
        }

        var jitter = new Vector2(SuperResolutionTuning.JitterSignX * frame.Dispatch.JitterX,
            SuperResolutionTuning.JitterSignY * frame.Dispatch.JitterY);
        if (jitter == Vector2.Zero || !float.IsFinite(jitter.X) || !float.IsFinite(jitter.Y))
        {
            return false;
        }

        // Sampled views inherit their usage from the backing image after view normalization.
        if (frame.Color.Image == frame.Output.Image ||
            (frame.Color.Usage & (uint)ImageUsageFlags.SampledBit) == 0 ||
            (output.Usage & ImageUsageFlags.StorageBit) == 0 ||
            !IsSupportedView(color) || !IsSupportedView(output) || !SupportsFormats(color.Format, output.Format) ||
            !EnsurePipeline())
        {
            if (!_warned)
            {
                _warned = true;
                Console.Error.WriteLine($"[PSSR][WARN] Basic jitter correction unavailable for {color.Format} to {output.Format}; using a plain blit.");
            }
            return false;
        }

        var size = new Vector2(frame.Color.Width, frame.Color.Height);
        var origin = new Vector2(frame.RenderX, frame.RenderY);
        var extent = new Vector2(frame.RenderWidth, frame.RenderHeight);
        var step = extent / size / new Vector2(frame.OutputWidth, frame.OutputHeight);
        // The guest projection moved the raster by jitter. Sample at the displaced position.
        var parameters = new BlitParameters((origin + jitter) / size + step * 0.5f, step,
            (origin + new Vector2(0.5f)) / size, (origin + extent - new Vector2(0.5f)) / size,
            new Extent2D(frame.OutputWidth, frame.OutputHeight));
        var source = new DescriptorImageInfo(_sampler, new ImageView(frame.Color.View), ImageLayout.General);
        var destination = new DescriptorImageInfo(default, new ImageView(frame.Output.View), ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &source,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &destination,
        };

        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
        descriptors.CmdPushDescriptorSet(command, PipelineBindPoint.Compute, _layout, 0, 2, writes);
        vk.CmdPushConstants(command, _layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(BlitParameters), &parameters);
        vk.CmdDispatch(command, (frame.OutputWidth + 7) / 8, (frame.OutputHeight + 7) / 8, 1);
        return true;
    }

    private static bool IsSupportedView(in ImageViewDescription view) =>
        view.Type == ImageViewType.Type2D && view.Aspect == ImageAspectFlags.ColorBit &&
        view.BaseLevel == 0 && view.BaseLayer == 0 && view.MinLod == 0 &&
        !ViewFormatRules.IsIntegerFormat(view.Format);

    private bool SupportsFormats(Format color, Format output)
    {
        if (!_supportedFormats.TryGetValue((color, output), out var supported))
        {
            vk.GetPhysicalDeviceFormatProperties(physicalDevice, color, out var colorProperties);
            var outputFeatures = new FormatProperties3 { SType = StructureType.FormatProperties3 };
            var outputProperties = new FormatProperties2 { SType = StructureType.FormatProperties2, PNext = &outputFeatures };
            vk.GetPhysicalDeviceFormatProperties2(physicalDevice, output, &outputProperties);
            supported = (colorProperties.OptimalTilingFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) != 0 &&
                (outputFeatures.OptimalTilingFeatures & FormatFeatureFlags2.StorageWriteWithoutFormatBit) != 0;
            _supportedFormats[(color, output)] = supported;
        }
        return supported;
    }

    private bool EnsurePipeline()
    {
        if (_initialized)
        {
            return _pipeline.Handle != 0;
        }

        _initialized = true;
        vk.GetPhysicalDeviceFeatures(physicalDevice, out var features);
        if (!features.ShaderStorageImageWriteWithoutFormat)
        {
            return false;
        }

        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit);
        bindings[1] = new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
        var setInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            Flags = DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr,
            BindingCount = 2,
            PBindings = bindings,
        };
        if (vk.CreateDescriptorSetLayout(device, &setInfo, null, out _setLayout) != Result.Success)
        {
            return false;
        }

        var setLayout = _setLayout;
        var range = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(BlitParameters));
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        if (vk.CreatePipelineLayout(device, &layoutInfo, null, out _layout) != Result.Success ||
            vk.CreateSampler(device, &samplerInfo, null, out _sampler) != Result.Success)
        {
            Dispose();
            return false;
        }

        var spirv = BlitShaders.CreateJitterCompensatedBlit();
        ShaderModule shader;
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            if (vk.CreateShaderModule(device, &info, null, out shader) != Result.Success)
            {
                Dispose();
                return false;
            }
        }

        fixed (byte* entry = "main\0"u8)
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shader,
                    PName = entry,
                },
                Layout = _layout,
            };
            var result = vk.CreateComputePipelines(device, default, 1, &info, null, out _pipeline);
            vk.DestroyShaderModule(device, shader, null);
            if (result != Result.Success)
            {
                Dispose();
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        vk.DestroyPipeline(device, _pipeline, null);
        vk.DestroySampler(device, _sampler, null);
        vk.DestroyPipelineLayout(device, _layout, null);
        vk.DestroyDescriptorSetLayout(device, _setLayout, null);
        _pipeline = default;
        _sampler = default;
        _layout = default;
        _setLayout = default;
    }
}
