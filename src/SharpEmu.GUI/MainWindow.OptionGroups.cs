// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace SharpEmu.GUI;

/// <summary>
/// Settings groups draw a hairline above every line but the first visible one. Rows are
/// hidden per platform, added at run time and shown or hidden by other settings, so the
/// first line is worked out here whenever a line's visibility changes.
/// </summary>
public partial class MainWindow
{
    private void InitializeOptionGroupDividers()
    {
        foreach (var group in this.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("optionsGroup")).ToList())
        {
            var lines = GroupLines(group).ToList();
            foreach (var line in lines)
            {
                line.PropertyChanged += (_, args) =>
                {
                    if (args.Property == IsVisibleProperty)
                    {
                        MarkFirstGroupLine(group);
                    }
                };
            }

            MarkFirstGroupLine(group);
        }
    }

    private static IEnumerable<Control> GroupLines(Border group) =>
        group.GetLogicalDescendants().OfType<Control>()
            .Where(c => c is SettingRow || (c is Border border && border.Classes.Contains("optionsInfoRow")));

    private static void MarkFirstGroupLine(Border group)
    {
        var found = false;
        foreach (var line in GroupLines(group))
        {
            var visible = ShownInGroup(line, group);
            SetClass(line, "groupFirst", visible && !found);
            found |= visible;
        }
    }

    // A line counts only if it and every panel between it and the group are visible.
    private static bool ShownInGroup(Control line, Border group)
    {
        for (Control? control = line; control is not null && !ReferenceEquals(control, group); control = control.Parent as Control)
        {
            if (!control.IsVisible)
            {
                return false;
            }
        }

        return true;
    }
}
