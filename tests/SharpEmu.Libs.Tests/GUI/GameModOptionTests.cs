// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Mods;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GameModOptionTests
{
    [Fact]
    public void TogglingPersistsAndAFailedSaveRestoresTheCheckbox()
    {
        var directory = Directory.CreateTempSubdirectory("sharpemu-mod-option-").FullName;
        try
        {
            var repository = new GameModRepository("CUSA00001", directory);
            var modPath = Directory.CreateDirectory(Path.Combine(repository.DirectoryPath, "Textures")).FullName;
            var errors = new List<string>();
            var option = new GameModOption(Assert.Single(repository.Load()), repository, errors.Add);
            option.IsChecked = true;
            Assert.True(option.IsChecked);
            Assert.True(Assert.Single(repository.Load()).Enabled);
            Directory.Delete(modPath);
            option.IsChecked = false;
            Assert.True(option.IsChecked);
            Assert.Single(errors);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
