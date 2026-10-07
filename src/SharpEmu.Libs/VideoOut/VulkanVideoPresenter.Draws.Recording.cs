// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {

        private static long? _cachedGuestImageTraceInterval = long.MinValue;
        private static long _cachedGuestImageTraceStartAfter;

        // SHARPEMU_TRACE_GUEST_IMAGES=every:N[@M] — read back 1280x720+ guest
        // images every Nth draw into each, starting after M total such draws.
        private static long? GuestImageTraceInterval()
        {
            if (_cachedGuestImageTraceInterval == long.MinValue)
            {
                var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
                long? interval = null;
                if (mode is not null && mode.StartsWith("every:", StringComparison.Ordinal))
                {
                    var spec = mode["every:".Length..];
                    var at = spec.IndexOf('@');
                    var intervalText = at < 0 ? spec : spec[..at];
                    if (long.TryParse(intervalText, out var parsed) && parsed > 0)
                    {
                        interval = parsed;
                    }

                    if (at >= 0 && long.TryParse(spec[(at + 1)..], out var after) && after > 0)
                    {
                        _cachedGuestImageTraceStartAfter = after;
                    }
                }

                _cachedGuestImageTraceInterval = interval;
            }

            return _cachedGuestImageTraceInterval;
        }

        private static long GuestImageTraceStartAfter()
        {
            _ = GuestImageTraceInterval();
            return _cachedGuestImageTraceStartAfter;
        }

        // Diagnostics toggles are read once: these run per draw / per cached
        // texture hit, and env lookups plus string parsing are far too
        // expensive there (and non-trivially so under Rosetta 2).
        private static readonly bool _vulkanValidationEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_VK_VALIDATION"),
                "1",
                StringComparison.Ordinal);
        // Object names and command labels are useful in RenderDoc and validation
        // captures, but formatting them and calling the debug-utils driver hooks
        // for every draw is measurable overhead in normal gameplay.
        private static readonly bool _vulkanDebugUtilsEnabled =
            _vulkanValidationEnabled ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_VK_DEBUG_LABELS"),
                "1",
                StringComparison.Ordinal);
        private static readonly string? _traceGuestImagesMode =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        private static readonly bool _traceGuestImagesEnabled =
            string.Equals(_traceGuestImagesMode, "1", StringComparison.Ordinal);
        private static readonly bool _tracePresentedGuestImagesEnabled =
            _traceGuestImagesEnabled ||
            string.Equals(_traceGuestImagesMode, "present", StringComparison.OrdinalIgnoreCase);
        private static readonly bool _traceVulkanResourcesEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_VK_RESOURCES"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceVulkanShaderEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
                "1",
                StringComparison.Ordinal) ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceTextureBindingsEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TEXTURE_BINDINGS"),
                "1",
                StringComparison.Ordinal);
        private static readonly uint _traceGuestImageWidth =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_WIDTH"),
                out var traceGuestImageWidth)
                ? traceGuestImageWidth
                : 0;
        private static readonly uint _traceGuestImageHeight =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_HEIGHT"),
                out var traceGuestImageHeight)
                ? traceGuestImageHeight
                : 0;
        private static readonly string? _traceGuestImageFormat =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_FORMAT");
        private static readonly long _tracePresentedGuestImageOccurrence =
            long.TryParse(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_OCCURRENCE"),
                out var tracePresentedGuestImageOccurrence) &&
            tracePresentedGuestImageOccurrence > 0
                ? tracePresentedGuestImageOccurrence
                : 0;
        private static readonly bool _traceGuestImageAddressFilterEnabled =
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS"));
        private static readonly bool _forceFullscreenPipeline =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_FULLSCREEN_PIPELINE") == "1";
        private static readonly bool _forceFullscreenVertex =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_FULLSCREEN_VERTEX") == "1";
        private static readonly bool _forceTitleFullscreenVertex =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_FULLSCREEN_VERTEX") == "1";
        private static readonly bool _forceDefaultRasterState =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_DEFAULT_RASTER_STATE") == "1";
        private static readonly bool _forceTitleDefaultRasterState =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DEFAULT_RASTER_STATE") == "1";
        private static readonly bool _forceTitleSolidFragment =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_SOLID_FRAGMENT") == "1";
        private static readonly bool _forceSolidFragment =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_SOLID_FRAGMENT") == "1";
        private static readonly uint? _forceAttributeFragmentLocation =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_FORCE_ATTRIBUTE_FRAGMENT"),
                out var forceAttributeFragmentLocation)
                    ? forceAttributeFragmentLocation
                    : null;
        private static readonly string? _fixedFragmentDumpPath =
            Environment.GetEnvironmentVariable("SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT");
        private static readonly bool _chunkedDrawsEnabled =
            Environment.GetEnvironmentVariable("SHARPEMU_ENABLE_CHUNKED_DRAWS") == "1";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            (bool Wildcard, ulong[] Addresses)> _cachedAddressLists = new();

        private static bool ShouldTraceGuestImageContentsForDiagnostics() =>
            _traceGuestImagesEnabled;

        private static bool ShouldTraceGuestImageAddressForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS",
                address);
        }

        private static bool ShouldTraceGuestImageStateForDiagnostics(
            GuestImageResource image)
        {
            if (_traceGuestImageAddressFilterEnabled)
            {
                return ShouldTraceGuestImageAddressForDiagnostics(image.Address);
            }

            var hasShapeFilter = _traceGuestImageWidth != 0 ||
                _traceGuestImageHeight != 0 ||
                !string.IsNullOrWhiteSpace(_traceGuestImageFormat);
            if (!hasShapeFilter ||
                (_traceGuestImageWidth != 0 && image.Width != _traceGuestImageWidth) ||
                (_traceGuestImageHeight != 0 && image.Height != _traceGuestImageHeight))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(_traceGuestImageFormat) ||
                (Enum.TryParse<Format>(
                    _traceGuestImageFormat,
                    ignoreCase: true,
                    out var expectedFormat) &&
                 image.Format == expectedFormat);
        }

        private static bool ShouldTraceGuestImageWriteForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_WRITES",
                address);
        }

        private static bool AddressListContains(
            string environmentVariable,
            ulong address)
        {
            var (wildcard, addresses) = _cachedAddressLists.GetOrAdd(
                environmentVariable,
                static name => ParseAddressList(Environment.GetEnvironmentVariable(name)));
            return wildcard || Array.IndexOf(addresses, address) >= 0;
        }

        private static (bool Wildcard, ulong[] Addresses) ParseAddressList(string? addresses)
        {
            if (string.IsNullOrWhiteSpace(addresses))
            {
                return (false, []);
            }

            var parsedAddresses = new List<ulong>();
            foreach (var token in addresses.Split(
                         [',', ';', ' ', '\t'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token == "*")
                {
                    return (true, []);
                }

                var span = token.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(
                        span,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    parsedAddresses.Add(parsed);
                }
            }

            return (false, parsedAddresses.ToArray());
        }

        private static bool ShouldTracePresentedGuestImageContentsForDiagnostics() =>
            _tracePresentedGuestImagesEnabled;

        private bool ShouldTraceAddressedPresentedGuestImage(GuestImageResource image)
        {
            if (!AddressListContains(
                    "SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_ADDRS",
                    image.Address))
            {
                return false;
            }

            var count = _presentedGuestImageTraceCounts.TryGetValue(
                image.Address,
                out var previous)
                ? previous + 1
                : 1;
            _presentedGuestImageTraceCounts[image.Address] = count;
            return _tracePresentedGuestImageOccurrence == 0
                ? count == 1
                : count == _tracePresentedGuestImageOccurrence;
        }

        private static bool ShouldTraceVulkanResources() =>
            _traceVulkanResourcesEnabled;

        private static bool ShouldTraceTextureBindings() =>
            _traceTextureBindingsEnabled;

        private void RecycleSubmissionUploads(SubmissionUploadResources resources)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ResourceDestroy);
            foreach (var texture in resources.Textures)
            {
                if (texture is { StagingBuffer.Handle: not 0 })
                {
                    RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                }
            }

            foreach (var snapshot in resources.FeedbackSnapshots)
            {
                RetireFeedbackSnapshot(snapshot);
            }

            foreach (var (buffer, memory) in resources.OverflowBuffers ?? [])
            {
                RecycleHostBuffer(buffer, memory);
            }
        }
    }
}
