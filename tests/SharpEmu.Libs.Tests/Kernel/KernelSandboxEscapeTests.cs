// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// The kernel path resolver is the guest->host sandbox boundary: every real
// file syscall (open/stat/unlink/mkdir/rmdir/rename/chmod) maps a guest path
// to a host path through KernelMemoryCompatExports.ResolveGuestPath and then
// hands the result straight to the host filesystem. These tests pin the
// containment guarantees: an unmapped or absolute guest path must resolve to
// nothing (default-deny), and a mount-relative path must never escape its root.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelSandboxEscapeTests : IDisposable
{
    private readonly string? _originalApp0;
    private readonly string _tempRoot;
    private readonly string _app0Root;

    public KernelSandboxEscapeTests()
    {
        _originalApp0 = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-sandbox-{Guid.NewGuid():N}");
        _app0Root = Path.Combine(_tempRoot, "app0");
        Directory.CreateDirectory(_app0Root);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _app0Root);

        // ResolveApp0Root caches _cachedApp0Root once, so a per-test env var is
        // ignored after an earlier test populates it. Registering an explicit
        // mount routes /app0 through the updatable mount table instead, which is
        // the pattern KernelPathCaseSensitivityTests uses for the same reason.
        KernelMemoryCompatExports.RegisterGuestPathMount("/app0", _app0Root);
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.UnregisterGuestPathMount("/app0");
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _originalApp0);
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    // Finding #1: an absolute guest path that matches no mount prefix used to be
    // returned verbatim, letting the guest open arbitrary host files. These forms
    // are absolute (or a UNC/backslash root) on every host, so they are denied
    // everywhere.
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/etc/shadow")]
    [InlineData("/root/.ssh/id_rsa")]
    [InlineData("\\\\server\\share\\secret")]
    [InlineData("/proc/self/mem")]
    public void ResolveGuestPath_UnmappedAbsolutePathIsDenied(string guestPath)
    {
        Assert.Equal(string.Empty, KernelMemoryCompatExports.ResolveGuestPath(guestPath));
    }

    // A Windows drive-qualified path (e.g. "C:\Windows\...") is only absolute on
    // Windows. On Unix "C:" is an ordinary relative filename, so the resolver
    // legitimately contains it under app0 rather than denying it; this escape is
    // therefore Windows-specific.
    [Fact]
    public void ResolveGuestPath_UnmappedWindowsDrivePathIsDenied()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            string.Empty,
            KernelMemoryCompatExports.ResolveGuestPath("C:\\Windows\\System32\\drivers\\etc\\hosts"));
    }

    // A recognized mount prefix must still resolve to a path under its root, so
    // the default-deny does not regress legitimate access.
    [Fact]
    public void ResolveGuestPath_App0PathResolvesUnderApp0Root()
    {
        var resolved = KernelMemoryCompatExports.ResolveGuestPath("/app0/game.bin");

        Assert.False(string.IsNullOrEmpty(resolved));
        var rootWithSep = Path.TrimEndingDirectorySeparator(_app0Root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(rootWithSep, Path.GetFullPath(resolved));
    }

    // Drive-letter injection: NormalizeMountRelativePath clamps "." / ".." but
    // splits only on separators, so a "C:" token survives as a segment.
    // Path.Combine then discards the mount root because the tail is drive-rooted,
    // yielding a raw host path. A resolved path outside the mount root must be
    // denied. On non-Windows hosts "C:" is an ordinary directory name and stays
    // contained, so this specifically pins the Windows escape.
    [Theory]
    [InlineData("app0/C:/Windows/Temp/evil.dll")]
    [InlineData("/app0/C:/Windows/Temp/evil.dll")]
    [InlineData("download0/C:/Windows/Temp/evil.dll")]
    [InlineData("/temp0/C:/Windows/Temp/evil.dll")]
    public void ResolveGuestPath_DriveLetterInjectionCannotEscapeMount(string guestPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var resolved = KernelMemoryCompatExports.ResolveGuestPath(guestPath);

        // Either denied outright, or (defensively) still under a SharpEmu mount
        // root — never a bare "C:\Windows\..." host path.
        if (!string.IsNullOrEmpty(resolved))
        {
            Assert.DoesNotContain("Windows", Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase);
        }
    }

    // A "C:"-style token under app0 must resolve inside the app0 root, not to the
    // host drive root.
    [Fact]
    public void ResolveGuestPath_DriveTokenStaysUnderApp0OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var resolved = KernelMemoryCompatExports.ResolveGuestPath("/app0/C:/data.bin");

        Assert.False(string.IsNullOrEmpty(resolved));
        var rootWithSep = Path.TrimEndingDirectorySeparator(_app0Root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(rootWithSep, Path.GetFullPath(resolved));
    }

    // Finding #3: lexical containment does not follow symlinks/junctions. A dump
    // that plants a reparse point inside app0 pointing outside it must not let a
    // guest path through it escape onto the host filesystem. Creating a symlink
    // can require privilege (Windows without Developer Mode); skip if it fails.
    [Fact]
    public void ResolveGuestPath_ReparsePointInsideMountIsDenied()
    {
        var outsideDir = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllBytes(Path.Combine(outsideDir, "secret.bin"), [1, 2, 3]);

        var linkPath = Path.Combine(_app0Root, "link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, outsideDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unprivileged host cannot create the link; nothing to assert.
            return;
        }

        // Sanity: the link genuinely redirects outside the mount, so a resolver
        // that followed it would reach the planted secret.
        Assert.True(File.Exists(Path.Combine(linkPath, "secret.bin")));

        var resolved = KernelMemoryCompatExports.ResolveGuestPath("/app0/link/secret.bin");

        Assert.Equal(string.Empty, resolved);
    }

    // The reparse defense must not break a legitimate real (non-link) file that
    // happens to sit deep under the mount root.
    [Fact]
    public void ResolveGuestPath_RealNestedFileUnderMountResolves()
    {
        var nested = Path.Combine(_app0Root, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "c.bin"), [9]);

        var resolved = KernelMemoryCompatExports.ResolveGuestPath("/app0/a/b/c.bin");

        Assert.False(string.IsNullOrEmpty(resolved));
        var rootWithSep = Path.TrimEndingDirectorySeparator(_app0Root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(rootWithSep, Path.GetFullPath(resolved));
    }

    // The containment guard resolves paths via Path.GetFullPath / File.GetAttributes,
    // which throw on a crafted over-long or invalid path. Because ResolveGuestPath
    // runs outside the callers' try blocks, an uncaught throw would crash the
    // syscall on untrusted input. The resolver must instead fail closed: return an
    // empty host path (which callers map to NOT_FOUND), never throw.
    [Theory]
    [InlineData("/app0/")]     // filled with a long segment below
    [InlineData("/app0/bad\0name")]
    public void ResolveGuestPath_MalformedPathUnderMountFailsClosed(string prefix)
    {
        var guestPath = prefix.EndsWith('/')
            ? prefix + new string('a', 40_000)
            : prefix;

        // Fail closed: the resolver must return an empty host path (never throw,
        // never a non-empty resolution). A 40k-char segment exceeds NAME_MAX on
        // Linux and a NUL trips GetFullPath on Windows; both must deny.
        var resolved = KernelMemoryCompatExports.ResolveGuestPath(guestPath);

        Assert.Equal(string.Empty, resolved);
    }
}
