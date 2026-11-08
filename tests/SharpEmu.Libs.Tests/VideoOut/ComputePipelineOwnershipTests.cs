// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;
using BindingLayout = SharpEmu.ShaderCompiler.Resources.BindingLayout;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ComputePipelineOwnershipTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Presenter = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;

    [Fact]
    public void FailedCompile_RemainsOwnedByPendingCleanup()
    {
        var host = RuntimeHelpers.GetUninitializedObject(Presenter);
        var field = Presenter.GetField("_pendingComputePipelines", Members)!;
        var pending = RuntimeHelpers.GetUninitializedObject(field.FieldType.GenericTypeArguments[1]);
        var failure = new InvalidOperationException("Compile failed.");
        pending.GetType().GetField("Compile", Members)!.SetValue(pending, Task.FromException<Pipeline>(failure));
        var entries = (IDictionary)Activator.CreateInstance(field.FieldType)!;
        entries.Add(0UL, pending);
        field.SetValue(host, entries);
        var method = Presenter.GetMethod("TryCreateComputePipeline", Members)!;
        var thrown = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(host, [RuntimeHelpers.GetUninitializedObject(typeof(ComputePipelineDescription)), default(PipelineHandle)]));
        Assert.Same(failure, thrown.InnerException);
        Assert.Same(pending, entries[0UL]);
    }

    // A linked program compiles one module at a time: while one of its modules compiles, another module of the same
    // program waits instead of taking a compiler slot.
    [Fact]
    public void LinkedProgram_WaitsForItsModuleInFlight()
    {
        var host = RuntimeHelpers.GetUninitializedObject(Presenter);
        var field = Presenter.GetField("_pendingComputePipelines", Members)!;
        var entries = (IDictionary)Activator.CreateInstance(field.FieldType)!;
        field.SetValue(host, entries);
        var running = new TaskCompletionSource();
        Presenter.GetField("_linkedComputeCompiles", Members)!.SetValue(host, new Dictionary<ulong, Task> { [0x1234] = running.Task });
        var description = new ComputePipelineDescription
        {
            Input = new ComputeInputInfo(),
            Program = new ShaderProgram(0x55),
            Stage = new ShaderProgramInfo { Hash = 0x1234, Bindings = new BindingLayout { CallTargetCount = 1 } },
        };
        var method = Presenter.GetMethod("TryCreateComputePipeline", Members)!;
        Assert.False((bool)method.Invoke(host, [description, default(PipelineHandle)])!);
        Assert.Empty(entries);
    }

    [Fact]
    public void Registration_TransfersOwnershipAfterInsertion()
    {
        var host = RuntimeHelpers.GetUninitializedObject(Presenter);
        var field = Presenter.GetField("_pipelineEntries", Members)!;
        var entries = (IDictionary)Activator.CreateInstance(field.FieldType)!;
        field.SetValue(host, entries);
        var entry = RuntimeHelpers.GetUninitializedObject(field.FieldType.GenericTypeArguments[1]);
        var transferred = false;
        Action transfer = () =>
        {
            Assert.Single(entries);
            transferred = true;
        };
        Presenter.GetMethod("RegisterPipeline", Members)!.Invoke(host, [entry, transfer]);
        Assert.True(transferred);
        Assert.Single(entries);
    }
}
