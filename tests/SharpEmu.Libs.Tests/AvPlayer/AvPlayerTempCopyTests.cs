// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerTempCopyTests
{
    [Fact]
    public void CopiesOfEndedProcessesAreRemovedAndOthersKept()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpEmu-tests", Path.GetRandomFileName());
        var ended = Path.Combine(root, EndedProcessIds(1)[0].ToString(CultureInfo.InvariantCulture));
        var live = Path.Combine(root, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        var unrelated = Path.Combine(root, "not-a-process");
        foreach (var folder in new[] { ended, live, unrelated })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "copy"), [1, 2, 3]);
        }

        try
        {
            AvPlayerExports.RemoveCopiesOfEndedProcesses(root);

            Assert.False(Directory.Exists(ended));
            Assert.True(File.Exists(Path.Combine(live, "copy")));
            Assert.True(File.Exists(Path.Combine(unrelated, "copy")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A copy another process holds open (a virus scanner, say) keeps its folder; the sweep still
    // removes the other ended processes' folders.
    [Fact]
    public void AFolderThatCannotBeDeletedLeavesTheOthersToTheSweep()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "SharpEmu-tests", Path.GetRandomFileName());
        var folders = EndedProcessIds(2)
            .Select(id => Path.Combine(root, id.ToString(CultureInfo.InvariantCulture)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "copy"), [1, 2, 3]);
        }

        try
        {
            // The folder the sweep meets first stays locked.
            using (new FileStream(Path.Combine(folders[0], "copy"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                AvPlayerExports.RemoveCopiesOfEndedProcesses(root);
            }

            Assert.False(Directory.Exists(folders[1]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Odd ids above any id the hosts hand out in practice; checked, not assumed.
    private static int[] EndedProcessIds(int count)
    {
        var ids = new List<int>();
        for (var id = 3_999_999; id > 3_000_000 && ids.Count < count; id -= 2)
        {
            try
            {
                using var process = Process.GetProcessById(id);
            }
            catch (ArgumentException)
            {
                ids.Add(id);
            }
        }

        return ids.Count == count ? [.. ids] : throw new InvalidOperationException("No unused process id found.");
    }
}
