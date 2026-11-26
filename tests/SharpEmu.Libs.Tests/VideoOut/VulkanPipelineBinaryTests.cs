// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPipelineBinaryTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void ASlowBuildIsStoredAndTheNextBuildIsMadeFromItsBinaries()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan) || BinaryDevice.TryCreate(vulkan) is not { } device) return;
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        using var presenter = new PresenterUnderTest(vulkan);
        var harnessDevice = presenter.GetField<Device>("_device");
        presenter.SetField("_device", device.Handle);
        try
        {
            presenter.InvokeMethod("InitializePipelineBinaries", directory);
            presenter.SetField("_pipelineBinarySlowMilliseconds", 0.0);
            presenter.SetField("_sharedPipelineBinaryBytes", int.MaxValue);
            using var compute = new ComputeObjects(vulkan.Vk, device.Handle, workGroupWidth: 21);

            compute.Build(presenter);
            var files = Directory.GetFiles(directory, "*.bin");
            Assert.Single(files);
            Assert.Equal(0, presenter.GetField<int>("_pipelineBinaryLoads"));
            compute.Build(presenter);
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryLoads"));

            // A cut file is refused; the pipeline compiles and writes the file again.
            var stored = File.ReadAllBytes(files[0]);
            File.WriteAllBytes(files[0], stored[..^4]);
            compute.Build(presenter);
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryLoads"));
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryRefusals"));
            compute.Build(presenter);
            Assert.Equal(2, presenter.GetField<int>("_pipelineBinaryLoads"));

            // Another driver's file (a different global key in the first line) is not read.
            var otherDriver = File.ReadAllBytes(files[0]);
            otherDriver["SharpEmuPB2:".Length] ^= 0x01;
            File.WriteAllBytes(files[0], otherDriver);
            compute.Build(presenter);
            Assert.Equal(2, presenter.GetField<int>("_pipelineBinaryLoads"));
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryRefusals"));
            compute.Build(presenter);
            Assert.Equal(3, presenter.GetField<int>("_pipelineBinaryLoads"));
        }
        finally
        {
            presenter.SetField("_device", harnessDevice);
            device.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DamagedFilesAreRefusedAndAnotherPipelinesFileIsNotRead()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan) || BinaryDevice.TryCreate(vulkan) is not { } device) return;
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        using var presenter = new PresenterUnderTest(vulkan);
        var harnessDevice = presenter.GetField<Device>("_device");
        presenter.SetField("_device", device.Handle);
        try
        {
            presenter.InvokeMethod("InitializePipelineBinaries", directory);
            presenter.SetField("_pipelineBinarySlowMilliseconds", 0.0);
            presenter.SetField("_sharedPipelineBinaryBytes", int.MaxValue);
            using var first = new ComputeObjects(vulkan.Vk, device.Handle, workGroupWidth: 26);
            first.Build(presenter);
            var file = Assert.Single(Directory.GetFiles(directory, "*.bin"));
            var signature = (string)presenter.InvokeMethod("PipelineBinarySignature", Convert.FromHexString(Path.GetFileNameWithoutExtension(file)))!;
            var good = File.ReadAllBytes(file);
            Assert.True(PipelineCacheSignature.TryUnwrap(signature, good, out var payload));

            // Payloads with a valid hash that do not hold exactly the binaries they declare: each is refused, then the
            // pipeline builds and writes its file again.
            var tooMany = payload.ToArray();
            BitConverter.GetBytes(17).CopyTo(tooMany, 0);
            var oneMissing = payload.ToArray();
            BitConverter.GetBytes(BitConverter.ToInt32(payload, 0) + 1).CopyTo(oneMissing, 0);
            var pastTheEnd = payload.ToArray();
            BitConverter.GetBytes(int.MaxValue).CopyTo(pastTheEnd, 8 + BitConverter.ToInt32(payload, 4));
            var refusals = 0;
            foreach (var damaged in new byte[][] { tooMany, oneMissing, pastTheEnd, payload[..^1], [.. payload, 0] })
            {
                File.WriteAllBytes(file, PipelineCacheSignature.Wrap(signature, damaged));
                first.Build(presenter);
                Assert.Equal(++refusals, presenter.GetField<int>("_pipelineBinaryRefusals"));
                Assert.True(PipelineCacheSignature.TryUnwrap(signature, File.ReadAllBytes(file), out _));
            }

            Assert.Equal(0, presenter.GetField<int>("_pipelineBinaryLoads"));
            first.Build(presenter);
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryLoads"));

            // A good file under another pipeline's name is not read.
            using var second = new ComputeObjects(vulkan.Vk, device.Handle, workGroupWidth: 27);
            second.Build(presenter);
            var secondFile = Assert.Single(Directory.GetFiles(directory, "*.bin"), path => path != file);
            File.Copy(file, secondFile, overwrite: true);
            second.Build(presenter);
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryLoads"));
            Assert.Equal(refusals, presenter.GetField<int>("_pipelineBinaryRefusals"));
            second.Build(presenter);
            Assert.Equal(2, presenter.GetField<int>("_pipelineBinaryLoads"));
        }
        finally
        {
            presenter.SetField("_device", harnessDevice);
            device.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ALargeBinaryIsKeptInTheSharedFolderAndAChangedOneIsWrittenAgain()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan) || BinaryDevice.TryCreate(vulkan) is not { } device) return;
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        using var presenter = new PresenterUnderTest(vulkan);
        var harnessDevice = presenter.GetField<Device>("_device");
        presenter.SetField("_device", device.Handle);
        try
        {
            presenter.InvokeMethod("InitializePipelineBinaries", directory);
            presenter.SetField("_pipelineBinarySlowMilliseconds", 0.0);
            presenter.SetField("_sharedPipelineBinaryBytes", 1);
            using var compute = new ComputeObjects(vulkan.Vk, device.Handle, workGroupWidth: 23);

            compute.Build(presenter);
            var sharedFiles = Directory.GetFiles(Path.Combine(directory, "shared"), "*.bin");
            Assert.NotEmpty(sharedFiles);
            var pipelineFile = Assert.Single(Directory.GetFiles(directory, "*.bin"));
            Assert.True(new FileInfo(pipelineFile).Length < sharedFiles.Min(file => new FileInfo(file).Length));
            compute.Build(presenter);
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryLoads"));

            // A shared binary whose contents no longer match its name is refused once read from disk again; the build
            // that follows writes it again.
            presenter.GetField<ConcurrentDictionary<string, byte[]>>("_sharedPipelineBinaries").Clear();
            var changed = File.ReadAllBytes(sharedFiles[0]);
            changed[^1] ^= 0x01;
            File.WriteAllBytes(sharedFiles[0], changed);
            compute.Build(presenter);
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryLoads"));
            Assert.Equal(1, presenter.GetField<int>("_pipelineBinaryRefusals"));
            presenter.GetField<ConcurrentDictionary<string, byte[]>>("_sharedPipelineBinaries").Clear();
            compute.Build(presenter);
            Assert.Equal(2, presenter.GetField<int>("_pipelineBinaryLoads"));
        }
        finally
        {
            presenter.SetField("_device", harnessDevice);
            device.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AFastBuildStoresNothingAndAKeyNeedsAFile()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan) || BinaryDevice.TryCreate(vulkan) is not { } device) return;
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        using var presenter = new PresenterUnderTest(vulkan);
        var harnessDevice = presenter.GetField<Device>("_device");
        presenter.SetField("_device", device.Handle);
        try
        {
            presenter.InvokeMethod("InitializePipelineBinaries", directory);
            presenter.SetField("_pipelineBinarySlowMilliseconds", double.MaxValue);
            using var compute = new ComputeObjects(vulkan.Vk, device.Handle, workGroupWidth: 22);
            compute.Build(presenter);
            compute.Build(presenter);
            Assert.False(Directory.Exists(directory) && Directory.GetFiles(directory, "*.bin").Length != 0);
            Assert.Equal(0, presenter.GetField<int>("_pipelineBinaryLoads"));
            Assert.Equal(0, presenter.GetField<int>("_pipelineBinaryRefusals"));
        }
        finally
        {
            presenter.SetField("_device", harnessDevice);
            device.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // A device of its own with VK_KHR_pipeline_binary; null when the driver lacks it.
    private sealed unsafe class BinaryDevice : IDisposable
    {
        private readonly Vk _vk;

        private BinaryDevice(Vk vk, Device handle)
        {
            _vk = vk;
            Handle = handle;
        }

        public Device Handle { get; }

        public static BinaryDevice? TryCreate(HeadlessVulkan vulkan)
        {
            var vk = vulkan.Vk;
            uint count = 0;
            vk.EnumerateDeviceExtensionProperties(vulkan.Physical, (byte*)null, &count, null);
            var properties = new ExtensionProperties[count];
            fixed (ExtensionProperties* pointer = properties)
            {
                vk.EnumerateDeviceExtensionProperties(vulkan.Physical, (byte*)null, &count, pointer);
            }

            var names = properties.Select(property => SilkMarshal.PtrToString((nint)property.ExtensionName)).ToHashSet();
            if (!names.Contains("VK_KHR_pipeline_binary") || !names.Contains("VK_KHR_maintenance5"))
            {
                return null;
            }

            var binaryFeatures = new PhysicalDevicePipelineBinaryFeaturesKHR { SType = StructureType.PhysicalDevicePipelineBinaryFeaturesKhr, PipelineBinaries = true };
            var maintenance5 = new PhysicalDeviceMaintenance5FeaturesKHR { SType = StructureType.PhysicalDeviceMaintenance5FeaturesKhr, Maintenance5 = true, PNext = &binaryFeatures };
            var priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = vulkan.QueueFamily, QueueCount = 1, PQueuePriorities = &priority };
            var extensions = (byte**)SilkMarshal.StringArrayToPtr(["VK_KHR_pipeline_binary", "VK_KHR_maintenance5"]);
            try
            {
                var info = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &maintenance5,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = 2,
                    PpEnabledExtensionNames = extensions,
                };
                return vk.CreateDevice(vulkan.Physical, &info, null, out var device) == Result.Success
                    ? new BinaryDevice(vk, device)
                    : null;
            }
            finally
            {
                SilkMarshal.Free((nint)extensions);
            }
        }

        public void Dispose() => _vk.DestroyDevice(Handle, null);
    }

    // A compute program with no resources, built through the presenter's worker compile path with a pipeline cache, as
    // production builds are.
    private sealed unsafe class ComputeObjects : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly ShaderModule _module;
        private readonly PipelineLayout _layout;
        private readonly PipelineCache _cache;

        public ComputeObjects(Vk vk, Device device, uint workGroupWidth)
        {
            _vk = vk;
            _device = device;
            var builder = new SpirvModuleBuilder();
            builder.AddCapability(SpirvCapability.Shader);
            var voidType = builder.TypeVoid();
            var main = builder.BeginFunction(voidType, builder.TypeFunction(voidType));
            builder.AddLabel();
            builder.AddStatement(SpirvOp.Return);
            builder.EndFunction();
            builder.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", []);
            builder.AddExecutionMode(main, SpirvExecutionMode.LocalSize, workGroupWidth, 1, 1);
            var code = builder.Build();
            fixed (byte* pointer = code)
            {
                var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)pointer };
                Assert.Equal(Result.Success, vk.CreateShaderModule(device, &moduleInfo, null, out _module));
            }

            var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo };
            Assert.Equal(Result.Success, vk.CreatePipelineLayout(device, &layoutInfo, null, out _layout));
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Assert.Equal(Result.Success, vk.CreatePipelineCache(device, &cacheInfo, null, out _cache));
        }

        public void Build(PresenterUnderTest presenter)
        {
            var pipeline = (Pipeline)presenter.InvokeMethod("CompileComputePipeline", _vk, _device, _cache, _module, _layout, 0u)!;
            Assert.NotEqual(0ul, pipeline.Handle);
            _vk.DestroyPipeline(_device, pipeline, null);
        }

        public void Dispose()
        {
            _vk.DestroyPipelineCache(_device, _cache, null);
            _vk.DestroyPipelineLayout(_device, _layout, null);
            _vk.DestroyShaderModule(_device, _module, null);
        }
    }
}
