// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Offline pipeline-compile benchmark: times vkCreateComputePipelines for dumped SPIR-V modules.
// Usage: pipebench [--disable-optimization] <file.spv> [more.spv ...]
// Needs "<file>.json" from spirv-cross --reflect next to each file.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

unsafe
{
    Environment.SetEnvironmentVariable("__GL_SHADER_DISK_CACHE", "0");
    var vk = Vk.GetApi();
    var appName = (byte*)SilkMarshal.StringToPtr("pipebench");
    var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, PApplicationName = appName, ApiVersion = Vk.Version13 };
    var instInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
    Check(vk.CreateInstance(&instInfo, null, out var instance), "instance");

    uint count = 0;
    vk.EnumeratePhysicalDevices(instance, ref count, null);
    var devices = new PhysicalDevice[count];
    fixed (PhysicalDevice* p = devices) vk.EnumeratePhysicalDevices(instance, ref count, p);
    var physical = devices.First(d => { vk.GetPhysicalDeviceProperties(d, out var pr); return pr.DeviceType == PhysicalDeviceType.DiscreteGpu; });
    vk.GetPhysicalDeviceProperties(physical, out var props);
    Console.WriteLine($"device: {SilkMarshal.PtrToString((nint)props.DeviceName)} maxPush={props.Limits.MaxPushConstantsSize}");

    // Enable every feature the device reports, so any capability the modules declare is legal.
    var f13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
    var f12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &f13 };
    var f11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features, PNext = &f12 };
    var f2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &f11 };
    vk.GetPhysicalDeviceFeatures2(physical, &f2);

    uint qCount = 0;
    vk.GetPhysicalDeviceQueueFamilyProperties(physical, ref qCount, null);
    var queues = new QueueFamilyProperties[qCount];
    fixed (QueueFamilyProperties* q = queues) vk.GetPhysicalDeviceQueueFamilyProperties(physical, ref qCount, q);
    var family = (uint)Array.FindIndex(queues, q => (q.QueueFlags & QueueFlags.ComputeBit) != 0);
    var priority = 1f;
    var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = family, QueueCount = 1, PQueuePriorities = &priority };
    var devInfo = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, PNext = &f2, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo };
    Check(vk.CreateDevice(physical, &devInfo, null, out var device), "device");

    var disableOptimization = args.Contains("--disable-optimization");
    foreach (var path in args.Where(arg => arg != "--disable-optimization"))
    {
        var code = File.ReadAllBytes(path);
        // A random generator word makes every module unique, so the driver's disk cache never hits.
        BitConverter.GetBytes((uint)Random.Shared.Next()).CopyTo(code, 8);
        using var json = JsonDocument.Parse(File.ReadAllText(path + ".json"));
        var setLayouts = BuildSetLayouts(vk, device, json.RootElement);
        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, props.Limits.MaxPushConstantsSize);
        var hasPush = json.RootElement.TryGetProperty("push_constants", out _);
        PipelineLayout layout;
        fixed (DescriptorSetLayout* sl = setLayouts)
        {
            var li = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = sl,
                PushConstantRangeCount = hasPush ? 1u : 0u,
                PPushConstantRanges = &pushRange,
            };
            Check(vk.CreatePipelineLayout(device, &li, null, out layout), "layout");
        }

        ShaderModule module;
        fixed (byte* c = code)
        {
            var mi = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)c };
            Check(vk.CreateShaderModule(device, &mi, null, out module), "module");
        }

        var entry = (byte*)SilkMarshal.StringToPtr("main");
        var ci = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Flags = disableOptimization ? (PipelineCreateFlags)0x1 : PipelineCreateFlags.None, // VK_PIPELINE_CREATE_DISABLE_OPTIMIZATION_BIT
            Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry },
            Layout = layout,
        };
        var sw = Stopwatch.StartNew();
        var result = vk.CreateComputePipelines(device, default, 1, &ci, null, out var pipeline);
        sw.Stop();
        Console.WriteLine($"{Path.GetFileName(path)}: {sw.Elapsed.TotalMilliseconds:F1} ms result={result} size={code.Length}");
        if (result == Result.Success) vk.DestroyPipeline(device, pipeline, null);
        vk.DestroyShaderModule(device, module, null);
        vk.DestroyPipelineLayout(device, layout, null);
        foreach (var s in setLayouts) vk.DestroyDescriptorSetLayout(device, s, null);
    }

    vk.DestroyDevice(device, null);
    vk.DestroyInstance(instance, null);
}

static unsafe DescriptorSetLayout[] BuildSetLayouts(Vk vk, Device device, JsonElement root)
{
    var bindings = new SortedDictionary<(uint Set, uint Binding), (DescriptorType Type, uint Count, bool Runtime)>();
    void Add(string key, DescriptorType type)
    {
        if (!root.TryGetProperty(key, out var list)) return;
        foreach (var r in list.EnumerateArray())
        {
            var set = r.GetProperty("set").GetUInt32();
            var binding = r.GetProperty("binding").GetUInt32();
            uint n = 1;
            var runtime = false;
            if (r.TryGetProperty("array", out var arr) && arr.GetArrayLength() > 0)
            {
                n = arr[0].GetUInt32();
                if (n == 0) { runtime = true; n = set == 0 ? 131072u : 1024u; }
            }

            bindings[(set, binding)] = (type, n, runtime);
        }
    }

    Add("separate_images", DescriptorType.SampledImage);
    Add("images", DescriptorType.StorageImage);
    Add("separate_samplers", DescriptorType.Sampler);
    Add("textures", DescriptorType.CombinedImageSampler);
    Add("ssbos", DescriptorType.StorageBuffer);
    Add("ubos", DescriptorType.UniformBuffer);
    var maxSet = bindings.Count == 0 ? -1 : (int)bindings.Keys.Max(k => k.Set);
    var layouts = new DescriptorSetLayout[maxSet + 1];
    for (uint set = 0; set <= maxSet; set++)
    {
        var entries = bindings.Where(b => b.Key.Set == set).ToArray();
        var vkb = entries.Select(e => new DescriptorSetLayoutBinding
        {
            Binding = e.Key.Binding,
            DescriptorType = e.Value.Type,
            DescriptorCount = e.Value.Count,
            StageFlags = ShaderStageFlags.ComputeBit,
        }).ToArray();
        var flags = entries.Select(e => e.Value.Runtime
            ? DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit
            : DescriptorBindingFlags.None).ToArray();
        var anyUab = flags.Any(f => f != DescriptorBindingFlags.None);
        fixed (DescriptorSetLayoutBinding* b = vkb)
        fixed (DescriptorBindingFlags* fl = flags)
        {
            var bf = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = (uint)flags.Length, PBindingFlags = fl };
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                PNext = &bf,
                Flags = anyUab ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit : 0,
                BindingCount = (uint)vkb.Length,
                PBindings = b,
            };
            Check(vk.CreateDescriptorSetLayout(device, &info, null, out layouts[set]), $"set{set}");
        }
    }

    return layouts;
}

static void Check(Result r, string what)
{
    if (r != Result.Success) throw new Exception($"{what} failed: {r}");
}
