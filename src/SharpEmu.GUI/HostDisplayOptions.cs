// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GUI;

internal sealed record HostDisplayOption(HostDisplayInfo Display)
{
    public int Index => Display.Index;

    public IReadOnlyList<HostDisplayMode> Modes => Display.Modes;

    public override string ToString() => $"{Index + 1}: {Display.Name}";
}

internal sealed record HostRefreshRateOption(int Value, string Label)
{
    public override string ToString() => Label;
}

internal static class HostDisplayOptions
{
    public static IReadOnlyList<HostDisplayOption> BuildDisplays(
        IReadOnlyList<HostDisplayInfo> detected,
        int selectedIndex)
    {
        selectedIndex = Math.Max(0, selectedIndex);
        var options = detected
            .Select(display => new HostDisplayOption(display))
            .ToList();
        if (options.Count == 0)
        {
            options.Add(new HostDisplayOption(new HostDisplayInfo(
                0,
                "Display 1",
                CreateFallbackModes())));
        }

        if (options.All(display => display.Index != selectedIndex))
        {
            options.Add(new HostDisplayOption(new HostDisplayInfo(
                selectedIndex,
                $"Display {selectedIndex + 1}",
                options[0].Modes)));
        }

        return options.OrderBy(display => display.Index).ToArray();
    }

    public static HostDisplayOption SelectDisplay(
        IReadOnlyList<HostDisplayOption> displays,
        int selectedIndex) =>
        displays.FirstOrDefault(display => display.Index == selectedIndex) ?? displays[0];

    public static IReadOnlyList<string> BuildResolutions(
        HostDisplayOption display,
        string? selectedResolution)
    {
        var resolutions = display.Modes
            .Where(mode => mode.Width > 0 && mode.Height > 0)
            .Select(mode => $"{mode.Width}x{mode.Height}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (TryParseResolution(selectedResolution, out var selectedWidth, out var selectedHeight))
        {
            var selected = $"{selectedWidth}x{selectedHeight}";
            if (!resolutions.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                resolutions.Add(selected);
            }
        }

        if (resolutions.Count == 0)
        {
            resolutions.Add("1920x1080");
        }

        return resolutions
            .OrderByDescending(resolution => ResolutionArea(resolution))
            .ThenByDescending(resolution => resolution, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<HostRefreshRateOption> BuildRefreshRates(
        HostDisplayOption display,
        string? resolution,
        int selectedRefreshRate,
        string automaticLabel)
    {
        TryParseResolution(resolution, out var width, out var height);
        var rates = display.Modes
            .Where(mode => mode.Width == width && mode.Height == height && mode.RefreshRate > 0)
            .Select(mode => mode.RefreshRate)
            .Distinct()
            .OrderByDescending(rate => rate)
            .ToList();
        if (selectedRefreshRate > 0 && !rates.Contains(selectedRefreshRate))
        {
            rates.Add(selectedRefreshRate);
            rates.Sort((left, right) => right.CompareTo(left));
        }

        return new[] { new HostRefreshRateOption(0, automaticLabel) }
            .Concat(rates.Select(rate => new HostRefreshRateOption(rate, $"{rate} Hz")))
            .ToArray();
    }

    public static bool TryParseResolution(string? value, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return separator > 0 &&
               int.TryParse(value.AsSpan(0, separator), out width) &&
               int.TryParse(value.AsSpan(separator + 1), out height) &&
               width > 0 &&
               height > 0;
    }

    private static long ResolutionArea(string resolution) =>
        TryParseResolution(resolution, out var width, out var height)
            ? (long)width * height
            : 0;

    private static IReadOnlyList<HostDisplayMode> CreateFallbackModes() =>
        [
            new HostDisplayMode(3840, 2160, 60),
            new HostDisplayMode(2560, 1440, 60),
            new HostDisplayMode(1920, 1080, 60),
            new HostDisplayMode(1280, 720, 60),
        ];
}
