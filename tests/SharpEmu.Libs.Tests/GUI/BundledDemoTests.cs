// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class BundledDemoTests
{
    [Fact]
    public void FindEbootRequiresDemoAssetsButNotPreviewMusic()
    {
        var directory = Directory.CreateTempSubdirectory("bundled-demo-test-");
        try
        {
            var root = Path.Combine(directory.FullName, BundledDemo.FolderName);
            var assets = Directory.CreateDirectory(Path.Combine(root, "sce_sys")).FullName;
            var eboot = Path.Combine(root, "eboot.bin");
            File.WriteAllBytes(eboot, []);
            foreach (var name in new[] { "param.json", "icon0.png", "pic0.png" })
            {
                File.WriteAllBytes(Path.Combine(assets, name), []);
            }

            Assert.Equal(eboot, BundledDemo.FindEboot(directory.FullName));
            File.Delete(Path.Combine(assets, "param.json"));
            Assert.Null(BundledDemo.FindEboot(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
