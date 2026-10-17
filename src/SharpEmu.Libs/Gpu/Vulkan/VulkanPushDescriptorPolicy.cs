// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// How many descriptors a set may push. MoltenVK reports a zero OpArrayLength for storage buffers
// bound through push descriptors, which turns every bounds-checked load into zero and drops every
// store, so portability (MoltenVK) devices push none. The portability-subset extension is not
// enumerated for every device, and macOS only ever has MoltenVK, so macOS pushes none outright.
// Every device the emulator or its tools create takes the limit from here.
public static unsafe class VulkanPushDescriptorPolicy
{
    private const string PortabilitySubsetExtensionName = "VK_KHR_portability_subset";

    public static uint UsableCount(Vk vk, PhysicalDevice physical, uint reported) =>
        OperatingSystem.IsMacOS() || HasDeviceExtension(vk, physical, PortabilitySubsetExtensionName) ? 0 : reported;

    private static bool HasDeviceExtension(Vk vk, PhysicalDevice physical, string name)
    {
        uint count = 0;
        if (vk.EnumerateDeviceExtensionProperties(physical, (byte*)null, &count, null) != Result.Success || count == 0)
        {
            return false;
        }

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            if (vk.EnumerateDeviceExtensionProperties(physical, (byte*)null, &count, pointer) != Result.Success)
            {
                return false;
            }

            var expected = Encoding.UTF8.GetBytes(name);
            for (var index = 0; index < count; index++)
            {
                var actual = new ReadOnlySpan<byte>(pointer[index].ExtensionName, 256);
                var length = actual.IndexOf((byte)0);
                if (actual[..(length < 0 ? actual.Length : length)].SequenceEqual(expected))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
