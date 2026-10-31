// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class ShaderCacheClearTests
{
    [Fact]
    public void AnalysisCacheDefaultsToLocalApplicationData()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(
            Path.Combine(localData, "SharpEmu", "shader-analysis"),
            MainWindow.ShaderAnalysisCacheDirectory(_ => null));
    }

    [Fact]
    public void AnalysisCacheFollowsTheConfiguredPathAndItsOffSwitch()
    {
        Assert.Equal("/tmp/graphs", MainWindow.ShaderAnalysisCacheDirectory(_ => "/tmp/graphs"));
        Assert.Null(MainWindow.ShaderAnalysisCacheDirectory(_ => "0"));
    }

    [Fact]
    public void ClearingTheAnalysisCacheKeepsUnrelatedFiles()
    {
        var directory = Directory.CreateTempSubdirectory("sharpemu-analysis-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "abc.graph"), "");
            File.WriteAllText(Path.Combine(directory, "abc.graph.0123.tmp"), "");
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "");

            MainWindow.ClearShaderAnalysisCache(directory);

            Assert.Equal(["notes.txt"], Directory.GetFiles(directory).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
