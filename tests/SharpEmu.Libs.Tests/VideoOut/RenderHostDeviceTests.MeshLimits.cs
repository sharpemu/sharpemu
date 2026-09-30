// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void MeshLimitsMatchPhysicalDeviceProperties()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        if (!_vulkan.SupportsMeshShaders)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh shader support.");
            return;
        }
        using var presenter = new PresenterUnderTest(_vulkan);
        var fields = new[] { "_nativeSubgroupSize", "_nativeSubgroupShaderStages" }
            .Select(name => typeof(VulkanVideoPresenter).GetField(name,
                BindingFlags.Static | BindingFlags.NonPublic)!).ToArray();
        var saved = fields.Select(field => field.GetValue(null)).ToArray();
        try
        {
            var mesh = new PhysicalDeviceMeshShaderPropertiesEXT
            {
                SType = StructureType.PhysicalDeviceMeshShaderPropertiesExt,
            };
            var subgroup = new PhysicalDeviceSubgroupProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupProperties, PNext = &mesh,
            };
            var properties = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2, PNext = &subgroup,
            };
            _vulkan.Vk.GetPhysicalDeviceProperties2(_vulkan.Physical, &properties);
            presenter.InvokeMethod("LoadComputeDeviceLimits");
            var host = (IShaderPipelineHost)presenter.Instance;
            Assert.Equal(new MeshShaderLimits(mesh.MaxMeshWorkGroupInvocations,
                mesh.MaxMeshOutputVertices, mesh.MaxMeshOutputPrimitives,
                Math.Min(mesh.MaxMeshSharedMemorySize, mesh.MaxMeshPayloadAndSharedMemorySize),
                mesh.MaxMeshWorkGroupCount[0], mesh.MaxMeshWorkGroupCount[1],
                mesh.MaxMeshWorkGroupTotalCount, mesh.MaxMeshWorkGroupSize[0],
                mesh.MaxMeshOutputMemorySize, mesh.MaxMeshPayloadAndOutputMemorySize,
                mesh.MeshOutputPerVertexGranularity, mesh.MeshOutputPerPrimitiveGranularity,
                mesh.MaxMeshOutputComponents), host.MeshLimits);
            Assert.Equal(subgroup.SubgroupSize, host.MeshSubgroupSize);
            Assert.False(host.MeshShadersSupported);
            presenter.SetField("_supportsMeshShader", true);
            Assert.True(host.MeshShadersSupported);
        }
        finally
        {
            for (var index = 0; index < fields.Length; index++)
                fields[index].SetValue(null, saved[index]);
        }
        presenter.Harness.Shutdown();
    }
}
