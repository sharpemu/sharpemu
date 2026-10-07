// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void MeshTraceFollowsTheNativeDrawThroughQueueSubmission()
    {
        if (!GatePrerequisites.Ready(_vulkan, shaderInt64: true)) return;
        if (!_vulkan.SupportsMeshShaders || !_vulkan.SupportsDynamicRendering || _vulkan.SubgroupSize != 32)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh rendering with 32-lane subgroups.");
            return;
        }
        var previous = Console.Error;
        using var output = new StringWriter();
        Console.SetError(TextWriter.Synchronized(output));
        try
        {
            // Use a distinct shader identity so other tests cannot suppress these trace records.
            RenderMeshDraw(32, false, traceShaderAddress: 0x7_1310_0000);
        }
        finally
        {
            Console.SetError(previous);
        }
        var text = output.ToString();
        if (!MeshDrawTrace.Enabled)
        {
            Assert.DoesNotContain("[MESH][TRACE]", text);
            return;
        }
        Assert.Contains("operation=stage-binding stage=Mesh", text);
        Assert.Contains("operation=image-take-ownership", text);
        var previousPosition = -1;
        foreach (var operation in new[] { "color-request", "color-acquire", "command-buffer", "queue-submit" })
        {
            var position = text.IndexOf($"operation={operation}", StringComparison.Ordinal);
            Assert.True(position > previousPosition, $"The {operation} record is missing or out of order.");
            previousPosition = position;
        }
        var submission = Assert.Single(text.Split('\n'), line => line.Contains("operation=queue-submit", StringComparison.Ordinal));
        Assert.Contains("accepted=True", submission);
        Assert.Contains("meshDraws=1", submission);
    }
}
