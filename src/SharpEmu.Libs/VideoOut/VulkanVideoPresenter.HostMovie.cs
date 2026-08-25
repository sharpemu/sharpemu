// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Media;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial manages host-decoded movie texture resources.

    internal static (int Luma, int Chroma) SelectRememberedHostMovieTextureBindings(
        IReadOnlyList<GuestDrawTexture> textures,
        ulong lumaAddress,
        ulong chromaAddress,
        uint hostWidth,
        uint hostHeight)
    {
        var lumaIndex = -1;
        ulong lumaArea = 0;
        for (var index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            if (texture.Address != lumaAddress ||
                !IsHostMovieLumaCandidateForFrame(texture, hostWidth, hostHeight))
            {
                continue;
            }

            var area = (ulong)texture.Width * texture.Height;
            if (lumaIndex < 0 || area > lumaArea)
            {
                lumaIndex = index;
                lumaArea = area;
            }
        }

        if (lumaIndex < 0)
        {
            return (-1, -1);
        }

        var luma = textures[lumaIndex];
        for (var index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            if (texture.Address == chromaAddress &&
                IsHostMovieChromaCandidateForLuma(luma, texture))
            {
                return (lumaIndex, index);
            }
        }

        return (-1, -1);
    }

    private static bool IsHostMovieLumaCandidateForFrame(
        GuestDrawTexture texture,
        uint hostWidth,
        uint hostHeight)
    {
        if (texture.Address == 0 ||
            texture.IsStorage ||
            texture.IsFallback ||
            texture.ArrayedView ||
            texture.ArrayLayers > 1 ||
            texture.Format != 1 ||
            texture.NumberType != 0 ||
            texture.Width < 1280 ||
            texture.Height < 720)
        {
            return false;
        }

        var guestAspect = (ulong)texture.Width * hostHeight;
        var hostAspect = (ulong)texture.Height * hostWidth;
        var difference = guestAspect > hostAspect
            ? guestAspect - hostAspect
            : hostAspect - guestAspect;
        return difference * 100 <= Math.Max(guestAspect, hostAspect) * 2;
    }

    private static bool IsHostMovieChromaCandidateForLuma(
        GuestDrawTexture luma,
        GuestDrawTexture chroma) =>
        chroma.Address != 0 &&
        chroma.Address != luma.Address &&
        !chroma.IsStorage &&
        !chroma.IsFallback &&
        !chroma.ArrayedView &&
        chroma.ArrayLayers <= 1 &&
        chroma.Format == 3 &&
        chroma.NumberType == 0 &&
        luma.Width == chroma.Width * 2 &&
        luma.Height == chroma.Height * 2;

    private sealed partial class Presenter
    {
        private Image _hostMovieImage;
        private DeviceMemory _hostMovieImageMemory;
        private ImageView _hostMovieImageView;
        private uint _hostMovieImageWidth;
        private uint _hostMovieImageHeight;
        private Format _hostMovieImageFormat;
        private uint _hostMovieImageDstSelect;
        private bool _hostMovieImageInitialized;
        private Image _hostMovieChromaImage;
        private DeviceMemory _hostMovieChromaImageMemory;
        private ImageView _hostMovieChromaImageView;
        private uint _hostMovieChromaImageWidth;
        private uint _hostMovieChromaImageHeight;
        private uint _hostMovieChromaImageDstSelect;
        private bool _hostMovieChromaImageInitialized;
        private byte[]? _hostMovieFramePixels;
        private byte[]? _hostMovieLumaPixels;
        private byte[]? _hostMovieChromaPixels;
        private uint _hostMovieFrameWidth;
        private uint _hostMovieFrameHeight;
        private long _hostMovieFrameSerial;
        private long _hostMovieConvertedFrameSerial = -1;
        private long _hostMovieLumaUploadedFrameSerial = -1;
        private long _hostMovieChromaUploadedFrameSerial = -1;
        private string? _hostMovieFramePath;
        private ulong _hostMovieLumaTextureAddress;
        private ulong _hostMovieChromaTextureAddress;
        private uint _hostMovieLumaDstSelect;
        private uint _hostMovieChromaDstSelect;
        private readonly HashSet<string> _tracedHostMovieTextureBindings =
            new(StringComparer.Ordinal);

        private void PumpHostMovieFrame()
        {
            if (!HostMovieBridge.TryDecodeNextFrame(
                    advanceClock: _hostMovieLumaTextureAddress != 0 &&
                                  _hostMovieChromaTextureAddress != 0,
                    out var pixels,
                    out var width,
                    out var height,
                    out var advanced,
                    out var frameSerial,
                    out var hostPath))
            {
                // Keep the last decoded image until a replacement arrives.
                // Movie sessions are queued asynchronously; clearing here
                // exposes the guest decoder's neutral surfaces between the
                // final frame of one movie and the first frame of the next.
                return;
            }

            if (!string.Equals(
                    _hostMovieFramePath,
                    hostPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _hostMovieFramePath = hostPath;
                _hostMovieLumaTextureAddress = 0;
                _hostMovieChromaTextureAddress = 0;
                _hostMovieLumaDstSelect = 0;
                _hostMovieChromaDstSelect = 0;
                _hostMovieConvertedFrameSerial = -1;
                _hostMovieLumaUploadedFrameSerial = -1;
                _hostMovieChromaUploadedFrameSerial = -1;
            }

            if (!advanced && _hostMovieFramePixels is not null)
            {
                return;
            }

            _hostMovieFramePixels = pixels;
            _hostMovieFrameWidth = width;
            _hostMovieFrameHeight = height;
            _hostMovieFrameSerial = frameSerial;
        }

        private readonly record struct HostMovieTextureBindings(int Luma, int Chroma)
        {
            public static HostMovieTextureBindings None { get; } = new(-1, -1);
        }

        private HostMovieTextureBindings FindHostMovieTextureBindings(
            IReadOnlyList<GuestDrawTexture> textures)
        {
            if (_hostMovieFramePixels is null ||
                _hostMovieFrameWidth == 0 ||
                _hostMovieFrameHeight == 0)
            {
                return HostMovieTextureBindings.None;
            }

            if (_hostMovieLumaTextureAddress != 0 &&
                _hostMovieChromaTextureAddress != 0)
            {
                var remembered = SelectRememberedHostMovieTextureBindings(
                    textures,
                    _hostMovieLumaTextureAddress,
                    _hostMovieChromaTextureAddress,
                    _hostMovieFrameWidth,
                    _hostMovieFrameHeight);
                if (remembered.Chroma >= 0)
                {
                    return RememberHostMovieTextureMappings(
                        textures,
                        remembered.Luma,
                        remembered.Chroma);
                }

                // Bluepoint alternates decoder output between multiple Y/UV
                // surface pairs. Fall through and discover the active pair
                // instead of sampling the stale guest surface on every other
                // movie draw.
            }

            var bestLumaIndex = -1;
            var bestChromaIndex = -1;
            ulong bestArea = 0;
            for (var lumaIndex = 0; lumaIndex < textures.Count; lumaIndex++)
            {
                var luma = textures[lumaIndex];
                if (!IsHostMovieLumaCandidate(luma))
                {
                    continue;
                }

                for (var chromaIndex = 0; chromaIndex < textures.Count; chromaIndex++)
                {
                    var chroma = textures[chromaIndex];
                    if (!IsHostMovieChromaCandidate(luma, chroma))
                    {
                        continue;
                    }

                    var area = (ulong)luma.Width * luma.Height;
                    if (bestLumaIndex < 0 || area > bestArea)
                    {
                        bestLumaIndex = lumaIndex;
                        bestChromaIndex = chromaIndex;
                        bestArea = area;
                    }
                }
            }

            if (bestLumaIndex < 0 || bestChromaIndex < 0)
            {
                return HostMovieTextureBindings.None;
            }

            var lumaTexture = textures[bestLumaIndex];
            var chromaTexture = textures[bestChromaIndex];
            _hostMovieLumaTextureAddress = lumaTexture.Address;
            _hostMovieChromaTextureAddress = chromaTexture.Address;
            _hostMovieLumaDstSelect = lumaTexture.DstSelect;
            _hostMovieChromaDstSelect = chromaTexture.DstSelect;
            var traceKey =
                $"{_hostMovieFramePath}|{lumaTexture.Address:X16}|{chromaTexture.Address:X16}";
            if (_tracedHostMovieTextureBindings.Add(traceKey))
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Bink2 YUV textures bound: " +
                    $"{Path.GetFileName(_hostMovieFramePath)} " +
                    $"y={bestLumaIndex}:0x{lumaTexture.Address:X16}:" +
                    $"{lumaTexture.Width}x{lumaTexture.Height}:dst=0x{lumaTexture.DstSelect:X} " +
                    $"uv={bestChromaIndex}:0x{chromaTexture.Address:X16}:" +
                    $"{chromaTexture.Width}x{chromaTexture.Height}:dst=0x{chromaTexture.DstSelect:X} " +
                    $"host={_hostMovieFrameWidth}x{_hostMovieFrameHeight}.");
            }

            return new HostMovieTextureBindings(bestLumaIndex, bestChromaIndex);
        }

        private HostMovieTextureBindings RememberHostMovieTextureMappings(
            IReadOnlyList<GuestDrawTexture> textures,
            int lumaIndex,
            int chromaIndex)
        {
            _hostMovieLumaDstSelect = textures[lumaIndex].DstSelect;
            _hostMovieChromaDstSelect = textures[chromaIndex].DstSelect;
            return new HostMovieTextureBindings(lumaIndex, chromaIndex);
        }

        private bool IsHostMovieLumaCandidate(GuestDrawTexture texture)
            => IsHostMovieLumaCandidateForFrame(
                texture,
                _hostMovieFrameWidth,
                _hostMovieFrameHeight);

        private static bool IsHostMovieChromaCandidate(
            GuestDrawTexture luma,
            GuestDrawTexture chroma) =>
            IsHostMovieChromaCandidateForLuma(luma, chroma);

        private TextureResource CreateHostMovieTextureResource(
            GuestDrawTexture texture,
            int plane)
        {
            EnsureHostMovieYuvFrame();
            var isLuma = plane == 0;
            var pixels = isLuma ? _hostMovieLumaPixels! : _hostMovieChromaPixels!;
            var width = isLuma
                ? _hostMovieFrameWidth
                : (_hostMovieFrameWidth + 1) / 2;
            var height = isLuma
                ? _hostMovieFrameHeight
                : (_hostMovieFrameHeight + 1) / 2;
            EnsureHostMovieImages(
                _hostMovieFrameWidth,
                _hostMovieFrameHeight,
                _hostMovieLumaDstSelect,
                (_hostMovieFrameWidth + 1) / 2,
                (_hostMovieFrameHeight + 1) / 2,
                _hostMovieChromaDstSelect);

            var uploadedFrameSerial = isLuma
                ? _hostMovieLumaUploadedFrameSerial
                : _hostMovieChromaUploadedFrameSerial;
            var needsUpload = uploadedFrameSerial != _hostMovieFrameSerial;
            VkBuffer stagingBuffer = default;
            DeviceMemory stagingMemory = default;
            if (needsUpload)
            {
                (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                    pixels,
                    $"Bink2 frame {_hostMovieFrameSerial} plane {plane} staging");
            }

            return new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = isLuma ? _hostMovieImage : _hostMovieChromaImage,
                View = isLuma ? _hostMovieImageView : _hostMovieChromaImageView,
                Width = width,
                Height = height,
                RowLength = width,
                DstSelect = texture.DstSelect,
                NeedsUpload = needsUpload,
                IsHostMovie = true,
                HostMoviePlane = plane,
                HostMovieFrameSerial = _hostMovieFrameSerial,
                SamplerState = texture.Sampler,
            };
        }

        private void EnsureHostMovieYuvFrame()
        {
            if (_hostMovieConvertedFrameSerial == _hostMovieFrameSerial)
            {
                return;
            }

            var bgra = _hostMovieFramePixels ??
                throw new InvalidOperationException("Host movie frame is unavailable.");
            var width = checked((int)_hostMovieFrameWidth);
            var height = checked((int)_hostMovieFrameHeight);
            var chromaWidth = (width + 1) / 2;
            var chromaHeight = (height + 1) / 2;
            if (_hostMovieLumaPixels?.Length != width * height)
            {
                _hostMovieLumaPixels = GC.AllocateUninitializedArray<byte>(width * height);
            }
            if (_hostMovieChromaPixels?.Length != chromaWidth * chromaHeight * 2)
            {
                _hostMovieChromaPixels =
                    GC.AllocateUninitializedArray<byte>(chromaWidth * chromaHeight * 2);
            }

            ConvertBgraToYuv420(
                bgra,
                width,
                height,
                _hostMovieLumaPixels,
                _hostMovieChromaPixels);
            _hostMovieConvertedFrameSerial = _hostMovieFrameSerial;
        }

        internal static void ConvertBgraToYuv420(
            ReadOnlySpan<byte> bgra,
            int width,
            int height,
            Span<byte> luma,
            Span<byte> chroma)
        {
            var chromaWidth = (width + 1) / 2;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = (y * width + x) * 4;
                    var b = bgra[source];
                    var g = bgra[source + 1];
                    var r = bgra[source + 2];
                    luma[y * width + x] = ClampByte(
                        (54 * r + 183 * g + 19 * b + 128) >> 8);
                }
            }

            for (var y = 0; y < height; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    var red = 0;
                    var green = 0;
                    var blue = 0;
                    var samples = 0;
                    for (var sampleY = y; sampleY < Math.Min(y + 2, height); sampleY++)
                    {
                        for (var sampleX = x; sampleX < Math.Min(x + 2, width); sampleX++)
                        {
                            var source = (sampleY * width + sampleX) * 4;
                            blue += bgra[source];
                            green += bgra[source + 1];
                            red += bgra[source + 2];
                            samples++;
                        }
                    }

                    red /= samples;
                    green /= samples;
                    blue /= samples;
                    var destination = ((y / 2) * chromaWidth + x / 2) * 2;
                    chroma[destination] = ClampByte(
                        ((128 * red - 116 * green - 12 * blue + 128) >> 8) + 128);
                    chroma[destination + 1] = ClampByte(
                        ((-29 * red - 99 * green + 128 * blue + 128) >> 8) + 128);
                }
            }
        }

        private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

        private void EnsureHostMovieImages(
            uint lumaWidth,
            uint lumaHeight,
            uint lumaDstSelect,
            uint chromaWidth,
            uint chromaHeight,
            uint chromaDstSelect)
        {
            if (_hostMovieImage.Handle != 0 &&
                _hostMovieImageWidth == lumaWidth &&
                _hostMovieImageHeight == lumaHeight &&
                _hostMovieImageFormat == Format.R8Unorm &&
                _hostMovieImageDstSelect == lumaDstSelect &&
                _hostMovieChromaImage.Handle != 0 &&
                _hostMovieChromaImageWidth == chromaWidth &&
                _hostMovieChromaImageHeight == chromaHeight &&
                _hostMovieChromaImageDstSelect == chromaDstSelect)
            {
                return;
            }

            if (_hostMovieImage.Handle != 0 || _hostMovieChromaImage.Handle != 0)
            {
                FlushBatchedGuestCommands();
                WaitForAllGuestSubmissions();
                DrainFrameSlots();
                DestroyHostMovieImage();
            }

            CreateHostMoviePlaneImage(
                lumaWidth,
                lumaHeight,
                Format.R8Unorm,
                lumaDstSelect,
                "luma",
                out _hostMovieImage,
                out _hostMovieImageMemory,
                out _hostMovieImageView);
            CreateHostMoviePlaneImage(
                chromaWidth,
                chromaHeight,
                Format.R8G8Unorm,
                chromaDstSelect,
                "chroma",
                out _hostMovieChromaImage,
                out _hostMovieChromaImageMemory,
                out _hostMovieChromaImageView);
            _hostMovieImageWidth = lumaWidth;
            _hostMovieImageHeight = lumaHeight;
            _hostMovieImageFormat = Format.R8Unorm;
            _hostMovieImageDstSelect = lumaDstSelect;
            _hostMovieChromaImageWidth = chromaWidth;
            _hostMovieChromaImageHeight = chromaHeight;
            _hostMovieChromaImageDstSelect = chromaDstSelect;
            _hostMovieImageInitialized = false;
            _hostMovieChromaImageInitialized = false;
            _hostMovieLumaUploadedFrameSerial = -1;
            _hostMovieChromaUploadedFrameSerial = -1;
        }

        private void CreateHostMoviePlaneImage(
            uint width,
            uint height,
            Format format,
            uint dstSelect,
            string planeName,
            out Image image,
            out DeviceMemory memory,
            out ImageView view)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out image),
                $"vkCreateImage(host movie {planeName})");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(
                    _device,
                    &allocationInfo,
                    null,
                    out memory),
                $"vkAllocateMemory(host movie {planeName})");
            Check(
                _vk.BindImageMemory(_device, image, memory, 0),
                $"vkBindImageMemory(host movie {planeName})");
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = ToVkComponentMapping(dstSelect),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(
                    _device,
                    &viewInfo,
                    null,
                    out view),
                $"vkCreateImageView(host movie {planeName})");
            SetDebugName(ObjectType.Image, image.Handle, $"SharpEmu Bink2 {planeName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"SharpEmu Bink2 {planeName} view");
        }

        private void DestroyHostMovieImage()
        {
            if (_hostMovieImageView.Handle != 0)
            {
                _vk.DestroyImageView(_device, _hostMovieImageView, null);
                _hostMovieImageView = default;
            }
            if (_hostMovieImage.Handle != 0)
            {
                _vk.DestroyImage(_device, _hostMovieImage, null);
                _hostMovieImage = default;
            }
            if (_hostMovieImageMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _hostMovieImageMemory, null);
                _hostMovieImageMemory = default;
            }
            if (_hostMovieChromaImageView.Handle != 0)
            {
                _vk.DestroyImageView(_device, _hostMovieChromaImageView, null);
                _hostMovieChromaImageView = default;
            }
            if (_hostMovieChromaImage.Handle != 0)
            {
                _vk.DestroyImage(_device, _hostMovieChromaImage, null);
                _hostMovieChromaImage = default;
            }
            if (_hostMovieChromaImageMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _hostMovieChromaImageMemory, null);
                _hostMovieChromaImageMemory = default;
            }
            _hostMovieImageWidth = 0;
            _hostMovieImageHeight = 0;
            _hostMovieImageFormat = Format.Undefined;
            _hostMovieChromaImageWidth = 0;
            _hostMovieChromaImageHeight = 0;
            _hostMovieImageInitialized = false;
            _hostMovieChromaImageInitialized = false;
            _hostMovieLumaUploadedFrameSerial = -1;
            _hostMovieChromaUploadedFrameSerial = -1;
        }
    }
}
