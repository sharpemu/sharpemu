// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Tests.VideoOut;

// The scheduler drives the presenter's submission bookkeeping and rendering state, as in production.
internal sealed class SchedulerForwarder : IRenderingState
{
    public PresenterUnderTest? Target { get; set; }

    public bool IsRendering => Target is { } target && ((IRenderingState)target.Instance).IsRendering;

    public void EndRendering()
    {
        if (Target is { } target)
        {
            ((IRenderingState)target.Instance).EndRendering();
        }
    }

    public void Prepare(SubmitBundle bundle) => Target?.InvokeMethod("PrepareGuestSubmission", bundle);

    public void Complete(ulong tick) => Target?.InvokeMethod("CompleteGuestSubmission", tick);
}

// A presenter over the harness caches without a window, a swapchain or a render thread.
internal sealed class PresenterUnderTest : IDisposable
{
    public const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static readonly Type PresenterType = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;

    public PresenterUnderTest(HeadlessVulkan vulkan, bool startScheduler = true)
    {
        var forwarder = new SchedulerForwarder();
        Harness = new CacheHarness(vulkan, hooks: new SchedulerHooks(forwarder, forwarder.Prepare, forwarder.Complete), startScheduler: startScheduler);
        Samplers = new SamplerStore(vulkan.DeviceInfo);
        Instance = RuntimeHelpers.GetUninitializedObject(PresenterType);
        SetField("_vk", vulkan.Vk);
        SetField("_instance", vulkan.Instance);
        SetField("_physicalDevice", vulkan.Physical);
        SetField("_device", vulkan.Device);
        SetField("_deviceInfo", vulkan.DeviceInfo);
        SetField("_scheduler", Harness.Scheduler);
        SetField("_relay", Harness.Worker.Relay);
        SetField("_bufferCache", Harness.Cache);
        SetField("_imageCache", Harness.Images);
        SetField("_guestBacking", Harness.Memory);
        SetField("_guestMemory", Harness.Memory);
        SetField("_samplerStore", Samplers);
        SetField("_trackedImageBindings", new List<ResourceSlotIdentifier>());
        SetField("_submissionContext", new SubmissionContext { QueueName = "presenter.test", SubmissionId = 1 });
        SetField("_commandStream", new CommandStreamQueue((ICommandStreamHost)Instance));
        SetField("_computeThreadLimits", new uint[3]);
        HostBuffers = new VulkanHostBufferPool(128UL * 1024 * 1024, allocation => InvokeMethod("DestroyHostBufferAllocation", allocation));
        SetField("_hostBufferPool", HostBuffers);
        SetField("_maxColorAttachments", 8u);
        SetField("_renderHostLimits", new RenderHostLimits(16384, 16384, 16384, 16384));
        foreach (var name in new[]
        {
            "_batchResources", "_batchRetireBuffers", "_pendingGuestSubmissions", "_recycledDescriptorPools",
            "_deferredResourceDestroys", "_deferredGuestImageVersionDestroys", "_descriptorLayouts", "_shaderDigests",
            "_renderPipelines", "_computeEntries", "_pipelineEntries",
        })
        {
            var field = PresenterType.GetField(name, InstanceMembers)!;
            field.SetValue(Instance, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }

        forwarder.Target = this;
    }

    public CacheHarness Harness { get; }

    public SamplerStore Samplers { get; }

    public VulkanHostBufferPool HostBuffers { get; }

    public object Instance { get; }

    public IRenderHost RenderHost => (IRenderHost)Instance;

    public CommandBuffer Command => new(Harness.Scheduler.Current.Handle);

    // Loads the dynamic rendering commands the render host records with; the device must carry the extensions.
    public void LoadRenderingCommands() => InvokeMethod("LoadRenderingCommands", false, Harness.Vulkan.DeviceName);

    public void SetField(string name, object value) => PresenterType.GetField(name, InstanceMembers)!.SetValue(Instance, value);

    public T GetField<T>(string name) => (T)PresenterType.GetField(name, InstanceMembers)!.GetValue(Instance)!;

    public object? InvokeMethod(string name, params object?[] arguments)
    {
        try
        {
            return PresenterType.GetMethod(name, InstanceMembers | BindingFlags.Static)!.Invoke(Instance, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    public T Run<T>(Func<T> work) => Harness.Worker.Run(work);

    public void Run(Action work) => Harness.Worker.Run(work);

    public void Dispose()
    {
        try
        {
            Run(() =>
            {
                // Complete recorded work before destroying resources that its commands use.
                if (Harness.Scheduler.Active)
                {
                    Harness.Scheduler.Finish();
                    Harness.Scheduler.WaitForAllPriorityOperations();
                }

                InvokeMethod("WaitForAllGuestSubmissions");
                DestroyPipelineResources();
                Samplers.Dispose();
                HostBuffers.Dispose();
            });
        }
        finally
        {
            Harness.Dispose();
        }

        Harness.Vulkan.AssertNoValidationMessages();
    }

    private unsafe void DestroyPipelineResources()
    {
        InvokeMethod("DestroyRenderPipelines");
        var layouts = GetField<IDictionary>("_descriptorLayouts");
        foreach (var layout in layouts.Values)
        {
            var layoutType = layout!.GetType();
            var pipelineLayout = (PipelineLayout)layoutType.GetProperty("PipelineLayout")!.GetValue(layout)!;
            var descriptorSetLayout = (DescriptorSetLayout)layoutType.GetProperty("DescriptorSetLayout")!.GetValue(layout)!;
            Harness.Vulkan.Vk.DestroyPipelineLayout(Harness.Vulkan.Device, pipelineLayout, null);
            if (descriptorSetLayout.Handle != 0)
            {
                Harness.Vulkan.Vk.DestroyDescriptorSetLayout(Harness.Vulkan.Device, descriptorSetLayout, null);
            }
        }

        layouts.Clear();
        var descriptorPools = GetField<Stack<DescriptorPool>>("_recycledDescriptorPools");
        while (descriptorPools.TryPop(out var descriptorPool))
        {
            Harness.Vulkan.Vk.DestroyDescriptorPool(Harness.Vulkan.Device, descriptorPool, null);
        }
    }
}
