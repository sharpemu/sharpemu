// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut.Upscaling;

public enum SuperResolutionUpscaler
{
    Auto,
    Dlss,
    Basic,
    Fsr,
    Xess,
}

public enum SuperResolutionBackend
{
    Dlss,
    Fsr,
    Basic,
    Xess,
}

public static class SuperResolutionSettings
{
    public const string UpscalerVariable = "SHARPEMU_PSSR_UPSCALER";

    public static SuperResolutionUpscaler Requested => Parse(Environment.GetEnvironmentVariable(UpscalerVariable));

    public static SuperResolutionUpscaler Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dlss" => SuperResolutionUpscaler.Dlss,
        "fsr" => SuperResolutionUpscaler.Fsr,
        "xess" => SuperResolutionUpscaler.Xess,
        "basic" or "off" => SuperResolutionUpscaler.Basic,
        _ => SuperResolutionUpscaler.Auto,
    };

    public static SuperResolutionBackend Choose(SuperResolutionUpscaler requested, SharpEmu.Logging.HostGpuVendor vendor) => requested switch
    {
        SuperResolutionUpscaler.Dlss => SuperResolutionBackend.Dlss,
        SuperResolutionUpscaler.Fsr => SuperResolutionBackend.Fsr,
        SuperResolutionUpscaler.Xess => SuperResolutionBackend.Xess,
        SuperResolutionUpscaler.Basic => SuperResolutionBackend.Basic,
        _ => vendor is SharpEmu.Logging.HostGpuVendor.Amd or SharpEmu.Logging.HostGpuVendor.Intel
            ? SuperResolutionBackend.Fsr
            : SuperResolutionBackend.Dlss,
    };

    public static SuperResolutionBackend Backend => Choose(Requested, SharpEmu.Logging.HostSystemInfo.GpuVendor);

    public static bool WantsDlss => Backend == SuperResolutionBackend.Dlss;

    public static bool WantsFsr => Backend == SuperResolutionBackend.Fsr;
}

public static class SuperResolutionStartup
{
    public static void Begin()
    {
        if (!SharpEmu.Libs.Kernel.ConsoleHardware.IsPro)
        {
            return;
        }

        var gpu = SharpEmu.Logging.HostSystemInfo.GpuName;
        switch (SuperResolutionSettings.Backend)
        {
            case SuperResolutionBackend.Dlss:
                Console.Error.WriteLine($"[PSSR][INFO] PSSR upscaler: DLSS ({SuperResolutionSettings.Requested}, GPU: {gpu}).");
                Streamline.StreamlineRuntime.BeginStart(DlssRuntimeFiles.DefaultDirectory);
                break;
            case SuperResolutionBackend.Fsr:
                Console.Error.WriteLine($"[PSSR][INFO] PSSR upscaler: FSR ({SuperResolutionSettings.Requested}, GPU: {gpu}).");
                break;
            case SuperResolutionBackend.Xess:
                Console.Error.WriteLine($"[PSSR][INFO] PSSR upscaler: XeSS ({SuperResolutionSettings.Requested}, GPU: {gpu}).");
                break;
            default:
                Console.Error.WriteLine("[PSSR][INFO] PSSR upscaler: basic (set in the PSSR settings).");
                break;
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
    public const string StreamlineVersion = "2.14.16";
    public const string StreamlineReleasesUrl = "https://github.com/NVIDIA-RTX/Streamline/releases/tag/v" + StreamlineVersion;
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

public static class FsrRuntimeFiles
{
    public const string SdkVersion = "2.4.0";
    public const string SdkReleaseUrl = "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/tag/v" + SdkVersion;
    public const string Loader = "amd_fidelityfx_loader_vk.dll";
    public const string Upscaler = "amd_fidelityfx_upscaler_vk.dll";

    public static IReadOnlyList<string> Required { get; } = [Loader, Upscaler];

    public static string DirectoryFor(string executableDirectory) => UpscalerFolders.For(executableDirectory, "fsr");

    public static string DefaultDirectory => DirectoryFor(AppContext.BaseDirectory);

    public static IReadOnlyList<string> Missing(string directory) =>
        Required.Where(file => !File.Exists(Path.Combine(directory, file))).ToArray();
}

public static class XessRuntimeFiles
{
    public const string SdkVersion = "3.0.2";
    public const string SdkReleaseUrl = "https://github.com/intel/xess/releases/tag/v" + SdkVersion;
    public const string Library = "libxess.dll";

    public static IReadOnlyList<string> Required { get; } = [Library];

    public static string DirectoryFor(string executableDirectory) => UpscalerFolders.For(executableDirectory, "xess");
    public static string DefaultDirectory => DirectoryFor(AppContext.BaseDirectory);
    public static IReadOnlyList<string> Missing(string directory) =>
        Required.Where(file => !File.Exists(Path.Combine(directory, file))).ToArray();
}
