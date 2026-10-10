// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class AsyncComputeBindingLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACompletedAsyncPipelineRetainsItsGlobalDescriptorSet(bool usesBindlessImages)
    {
        // Exercise completion bookkeeping without calling Vulkan. Zero native
        // handles keep the test independent of the driver's indexing features.
        var type = PresenterUnderTest.PresenterType;
        var presenter = RuntimeHelpers.GetUninitializedObject(type);
        var pendingField = type.GetField("_pendingComputePipelines", PresenterUnderTest.InstanceMembers)!;
        var pending = Activator.CreateInstance(pendingField.FieldType, nonPublic: true)!;
        pendingField.SetValue(presenter, pending);
        var pendingType = pendingField.FieldType.GenericTypeArguments[1];
        var completed = Activator.CreateInstance(pendingType, nonPublic: true)!;
        pendingType.GetField("Compile")!.SetValue(completed, Task.FromResult(default(Pipeline)));
        pendingType.GetField("StartTimestamp")!.SetValue(completed, Stopwatch.GetTimestamp());
        ((IDictionary)pending).Add(1ul, completed);
        var entriesField = type.GetField("_pipelineEntries", PresenterUnderTest.InstanceMembers)!;
        var entries = Activator.CreateInstance(entriesField.FieldType, nonPublic: true)!;
        entriesField.SetValue(presenter, entries);
        var description = new ComputePipelineDescription
        {
            Input = new ComputeInputInfo(),
            Program = new ShaderProgram(1),
            Stage = new ShaderProgramInfo
            {
                Bindings = BindingLayout.Allocate(new ShaderResourceInfo(), [], false, false, false,
                    usesBindlessImages: usesBindlessImages),
            },
        };
        Assert.True(((IShaderPipelineHost)presenter).TryCreateComputePipeline(description, out var handle));
        var entry = ((IDictionary)entries)[handle.Pipeline]!;
        Assert.Equal(usesBindlessImages, (bool)entry.GetType().GetField("UsesBindlessImages")!.GetValue(entry)!);
        Assert.Empty((IDictionary)pending);
    }
}
