// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut.Upscaling;

public enum SuperResolutionUpscaler
{
    Auto,
    Dlss,
    Basic,
}

public static class SuperResolutionSettings
{
    public const string UpscalerVariable = "SHARPEMU_PSSR_UPSCALER";

    public static SuperResolutionUpscaler Requested => Parse(Environment.GetEnvironmentVariable(UpscalerVariable));

    public static SuperResolutionUpscaler Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dlss" => SuperResolutionUpscaler.Dlss,
        "basic" or "off" => SuperResolutionUpscaler.Basic,
        _ => SuperResolutionUpscaler.Auto,
    };

    public static bool WantsDlss => Requested is SuperResolutionUpscaler.Auto or SuperResolutionUpscaler.Dlss;
}

public static class SuperResolutionStartup
{
    public static void Begin()
    {
        if (SharpEmu.Libs.Kernel.ConsoleHardware.IsPro && SuperResolutionSettings.WantsDlss)
        {
            Streamline.StreamlineRuntime.BeginStart(DlssRuntimeFiles.DefaultDirectory);
        }
    }
}

public static class UpscalerFolders
{
    public const string Root = "upscalers";

    public static string For(string executableDirectory, string backend) => Path.Combine(executableDirectory, Root, backend);
}

public readonly record struct DlssRuntimeFile(string Name, string Source, string Url);

public static class DlssRuntimeFiles
{
    public const string StreamlineReleasesUrl = "https://github.com/NVIDIA-RTX/Streamline/releases";
    public const string Interposer = "sl.interposer.dll";
    public const string FolderName = "streamline";

    public static IReadOnlyList<DlssRuntimeFile> Required { get; } =
    [
        new(Interposer, "Streamline SDK (bin/x64)", StreamlineReleasesUrl),
        new("sl.common.dll", "Streamline SDK (bin/x64)", StreamlineReleasesUrl),
        new("sl.dlss.dll", "Streamline SDK (bin/x64)", StreamlineReleasesUrl),
        new("nvngx_dlss.dll", "Streamline SDK (bin/x64)", StreamlineReleasesUrl),
    ];

    public static string DirectoryFor(string executableDirectory) => UpscalerFolders.For(executableDirectory, FolderName);

    public static string DefaultDirectory => DirectoryFor(AppContext.BaseDirectory);

    public static IReadOnlyList<DlssRuntimeFile> Missing(string directory) =>
        Required.Where(file => !File.Exists(Path.Combine(directory, file.Name))).ToArray();
}
