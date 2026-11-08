// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ShaderPrewarmCompletionTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type Presenter = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;

    private static object CreateHost()
    {
        var host = RuntimeHelpers.GetUninitializedObject(Presenter);
        foreach (var name in new[] { "_runtimeComputeIdentities", "_prewarmedShaderIdentities" })
            Presenter.GetField(name, Members)!.SetValue(host, new ConcurrentDictionary<string, byte>());
        return host;
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    public void Completion_RequiresCacheSaveBeforeStamp(int failures, bool stopping, bool complete)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var list = ShaderPrewarmList.Open(directory)!;
            list.AppendProgress("stamp", [7UL]);
            var host = CreateHost();
            Presenter.GetField("_shaderPrewarmFailed", Members)!.SetValue(host, failures);
            Presenter.GetField("_shaderPrewarmStopping", Members)!.SetValue(host, stopping);
            Presenter.GetMethod("FinishShaderPrewarm", Members)!.Invoke(host, [list, "stamp", Stopwatch.GetTimestamp()]);
            Assert.False(list.IsStampCurrent("stamp"));
            Assert.Equal(complete, Presenter.GetField("_shaderPrewarmCompletion", Members)!.GetValue(host) is not null);
            Assert.Contains(7UL, list.ReadProgress("stamp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Skip_RequiresTheCertifiedCache(bool available, bool matching, bool expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var list = ShaderPrewarmList.Open(directory)!;
            var stamp = list.CoverageStamp("build");
            list.WriteStamp(ShaderPrewarmList.CacheStamp(stamp, new byte[] { 1, 2, 3 }));
            byte[] cache = !available ? [] : matching ? [1, 2, 3] : [1, 2, 4];
            var method = Presenter.GetMethod("CanSkipShaderPrewarm", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(expected, method.Invoke(null, [list, stamp, cache]));
            Assert.False((bool)method.Invoke(null, [list, list.CoverageStamp("other-build"), cache])!);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("_supportsShaderSignedZeroInfNanPreserveFloat32", true)]
    [InlineData("_canRequireComputeSubgroup32", true)]
    [InlineData("_canRequireComputeSubgroup64", true)]
    [InlineData("_maxComputeWorkgroupSubgroups", 4u)]
    public void Stamp_ChangesWithCompileCapabilities(string field, object value)
    {
        var host = CreateHost();
        var method = Presenter.GetMethod("BuildShaderPrewarmStamp", Members)!;
        var before = (string)method.Invoke(host, [typeof(ShaderPrewarmCompletionTests), "driver"])!;
        Presenter.GetField(field, Members)!.SetValue(host, value);
        var after = (string)method.Invoke(host, [typeof(ShaderPrewarmCompletionTests), "driver"])!;
        Assert.NotEqual(before, after);
        Assert.Contains(typeof(ShaderProgramCache).Assembly.ManifestModule.ModuleVersionId.ToString("N"), after);
        Assert.NotEqual(after, method.Invoke(host, [typeof(ShaderPrewarmCompletionTests), "other-driver"]));
    }
}
