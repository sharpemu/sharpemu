// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ResourcePreparationAllocationTests
{
    private sealed class NormalizingFrontend : IShaderFrontend
    {
        public ulong CacheDomain => 1;
        public readonly uint[] Descriptor = new uint[8];
        public uint[] NormalizeImageForResolution(ReadOnlySpan<uint> words, ShaderStage stage, ulong shaderHash, string source, bool allowPreparation) => Descriptor;
        public RegisteredShader ReadShader(CpuContext context, ulong address, string label, ReadOnlySpan<uint> userData = default) => throw new NotSupportedException();
        public bool TryDecode(CpuContext context, RegisteredShader shader, out Gen5ShaderProgram program, out string error) => throw new NotSupportedException();
    }

    [Fact]
    public void RenderThreadImageConversionDoesNotAllocateDispatchCallbacks()
    {
        var host = (IShaderPipelineHost)RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.PresenterType);
        var frontend = new NormalizingFrontend();
        for (var index = 0; index < 100; index++) host.NormalizeImageDescriptor(frontend, frontend.Descriptor, ShaderStage.Pixel, 1, "test");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10000; index++) host.NormalizeImageDescriptor(frontend, frontend.Descriptor, ShaderStage.Pixel, 1, "test");
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedPublicationChecksDoNotAllocateCallbacks(bool pendingWrite)
    {
        var type = PresenterUnderTest.PresenterType;
        var presenter = RuntimeHelpers.GetUninitializedObject(type);
        var pending = new PendingGuestPublications();
        if (pendingWrite) pending.Add(0, 0x10000, 8);
        type.GetField("_guestPublications", PresenterUnderTest.InstanceMembers)!.SetValue(presenter, pending);
        var complete = type.GetMethod("CompleteOverlappingPublications", PresenterUnderTest.InstanceMembers)!
            .CreateDelegate<Action<ulong, ulong, bool, bool>>(presenter);
        for (var index = 0; index < 100; index++) complete(0x2000, 16, false, false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10000; index++) complete(0x2000, 16, false, false);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
