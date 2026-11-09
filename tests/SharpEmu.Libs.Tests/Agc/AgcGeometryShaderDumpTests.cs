// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(SchedulingStateCollection.Name)]
public sealed class AgcGeometryShaderDumpTests
{
    [Fact]
    public void DifferentShaderPairsKeepSeparateDumpFiles()
    {
        const string variable = "SHARPEMU_SHADER_SPIRV_DUMP_DIR";
        var previous = Environment.GetEnvironmentVariable(variable);
        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-geometry-dump-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(variable, directory);
            var first = AgcExports.GetGeometryShaderDumpBasePath(0x1000, 0x2000);
            var second = AgcExports.GetGeometryShaderDumpBasePath(0x1000, 0x3000);
            var third = AgcExports.GetGeometryShaderDumpBasePath(0x4000, 0x2000);
            Assert.Equal(first, AgcExports.GetGeometryShaderDumpBasePath(0x1000, 0x2000));
            Assert.Equal(3, new HashSet<string> { first, second, third }.Count);
            foreach (var suffix in new[] { ".front.bin", ".back.bin", ".front.header.bin", ".back.header.bin", ".txt" })
            {
                File.WriteAllText(first + suffix, "first");
                File.WriteAllText(second + suffix, "second");
                File.WriteAllText(third + suffix, "third");
                Assert.Equal("first", File.ReadAllText(first + suffix));
                Assert.Equal("second", File.ReadAllText(second + suffix));
                Assert.Equal("third", File.ReadAllText(third + suffix));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
