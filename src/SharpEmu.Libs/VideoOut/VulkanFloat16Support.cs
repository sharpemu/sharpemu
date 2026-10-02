// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// Whether a device can run f16<->f32 conversions natively with the hardware's results:
// shaderFloat16, plus round-to-nearest-even and denormal preservation for 16-bit floats
// (float controls), including signed zero, infinity and NaN preservation. Shaders can
// then use both native conversions and fused half arithmetic with explicit float controls.
public static unsafe class VulkanFloat16Support
{
    public static bool SupportsExactConversions(Vk vk, PhysicalDevice physical)
    {
        var float16Features = new PhysicalDeviceShaderFloat16Int8Features
        {
            SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
        };
        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &float16Features,
        };
        vk.GetPhysicalDeviceFeatures2(physical, &features);

        var floatControls = new PhysicalDeviceFloatControlsProperties
        {
            SType = StructureType.PhysicalDeviceFloatControlsProperties,
        };
        var properties = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &floatControls,
        };
        vk.GetPhysicalDeviceProperties2(physical, &properties);
        return float16Features.ShaderFloat16 &&
            floatControls.ShaderRoundingModeRtefloat16 &&
            floatControls.ShaderDenormPreserveFloat16 &&
            floatControls.ShaderSignedZeroInfNanPreserveFloat16;
    }
}
