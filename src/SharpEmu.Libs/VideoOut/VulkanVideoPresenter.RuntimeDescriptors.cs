// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

// This partial resolves the descriptors shaders read at run time (MemoryAccessInfo.RuntimeDescriptor).
// Such a shader looks its image and sampler words up in a table this host builds; a missed
// lookup lands in the miss buffer, which is read when the tick completes. The next draw that
// uses runtime descriptors creates what was missed and rebuilds the table.
//
// Every image registered by this shader goes through the normal texture path on its draws: it is found
// again in the image cache, synchronized with guest memory and transitioned with the draw's
// own textures, so its heap slot always names a current view.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // Up to nine key dwords (view class + image words, or the sampler words) with value equality.
        private readonly record struct RuntimeDescriptorKey(ulong Words01, ulong Words23, ulong Words45, ulong Words67, uint Word8, int Count)
        {
            public static RuntimeDescriptorKey From(ReadOnlySpan<uint> words)
            {
                Span<uint> padded = stackalloc uint[9];
                padded.Clear();
                words.CopyTo(padded);
                static ulong Pair(Span<uint> values, int index) => values[index] | ((ulong)values[index + 1] << 32);
                return new RuntimeDescriptorKey(Pair(padded, 0), Pair(padded, 2), Pair(padded, 4), Pair(padded, 6), padded[8], words.Length);
            }
        }

        private sealed class RuntimeImageEntry(uint[] key)
        {
            // The view class followed by the eight image words, as the shader hashes them.
            public uint[] Key { get; } = key;
            public ImageDimension Dimension => RuntimeDescriptorTable.ViewDimension(Key[0]);
            public ImageNumericClass NumericClass => RuntimeDescriptorTable.ViewNumericClass(Key[0]);
            public uint Slot { get; set; }
            // The texture its words resolved to, kept while the cache keeps its image.
            public TextureResource? Resolved { get; set; }
        }

        private sealed class RuntimeSamplerEntry(uint[] key, uint slot)
        {
            public uint[] Key { get; } = key;
            public uint Slot { get; } = slot;
        }

        private sealed class RuntimeDescriptorSet
        {
            public readonly Dictionary<RuntimeDescriptorKey, RuntimeImageEntry> Images = new();
            public readonly Dictionary<RuntimeDescriptorKey, RuntimeSamplerEntry> Samplers = new();
            public readonly ConcurrentQueue<(bool Image, uint[] Key)> Misses = new();
            public readonly TickOwnedResources<GpuBuffer> MissBuffers = new();
            public GpuBuffer? Table;
            public bool TableDirty = true;
            public ulong MissScanTick = ulong.MaxValue;
        }

        // Feedback is owned by the shader that generated it, not by the process.
        // In particular, a new scene must not acquire every older shader's textures.
        private readonly Dictionary<(ShaderStageKind Stage, ulong Hash), RuntimeDescriptorSet> _runtimeDescriptorSets = new();

        private RuntimeDescriptorSet RuntimeDescriptorsFor(ShaderProgramInfo program)
        {
            var key = (program.Stage, program.Hash);
            if (!_runtimeDescriptorSets.TryGetValue(key, out var descriptors))
                _runtimeDescriptorSets.Add(key, descriptors = new RuntimeDescriptorSet());
            return descriptors;
        }

        // The texture path of a registered image: the view class is part of the key because
        // Vulkan image loads need a view whose numeric type matches the SPIR-V image type.
        private static ImageResource RuntimeImageResource(RuntimeImageEntry entry) => new()
        {
            ResourceClass = ShaderCompiler.Resources.ImageResourceClass.Sampled,
            NumericClass = entry.NumericClass,
            Dimension = entry.Dimension,
        };

        // Registers hold whatever a lane computed, so a runtime descriptor can describe no valid
        // view (the hardware reads the null texture then) or name memory that is not mapped, or no
        // longer is. An image there would have no guest bytes to track.
        private bool IsBackedRuntimeTexture(RuntimeImageEntry entry)
        {
            if (!ImageRequestBuilders.TryTexture(entry.Key.AsSpan(1), ShapeOf(RuntimeImageResource(entry)), out var resolution))
            {
                return false;
            }

            var data = resolution.Request.Description.Data;
            return ImageDescription.IsEmptyRange(data) || _guestMemory.CanRead(data.Address, data.Size);
        }

        // Called while a stage's textures are resolved: registers what earlier draws missed and
        // binds this shader's registered images with the draw.
        private void PrepareRuntimeDescriptors(PreparedStageBindings prepared)
        {
            var descriptors = RuntimeDescriptorsFor(prepared.Program);
            RegisterRuntimeDescriptorMisses(descriptors);
            List<RuntimeDescriptorKey>? freed = null;
            foreach (var (key, entry) in descriptors.Images)
            {
                if (!TryBindRuntimeImage(entry, prepared.Program, out var binding))
                {
                    (freed ??= []).Add(key);
                    continue;
                }

                prepared.RuntimeImages.Add(binding);
                prepared.RuntimeEntries.Add(entry);
            }

            if (freed is not null)
            {
                foreach (var key in freed) descriptors.Images.Remove(key);
                descriptors.TableDirty = true;
            }
        }

        // An entry keeps the texture its words resolved to while the cache keeps that image; each
        // draw binds its own copy. Only a new entry, or one whose image the cache retired, is
        // resolved again - and a retired one whose memory the title freed leaves the table, so a
        // shader that still reads the descriptor misses it again.
        private bool TryBindRuntimeImage(RuntimeImageEntry entry, ShaderProgramInfo program, out TextureResource binding)
        {
            CachedImage? stale = null;
            if (entry.Resolved is { } resolved && !IsStaleImage(resolved.ImageIdentifier, out stale))
            {
                BindImage(resolved.ImageIdentifier, storage: false);
                binding = CopyRuntimeBinding(resolved);
                return true;
            }

            if (stale is not null)
            {
                stale.Binding = default;
            }

            entry.Resolved = null;
            if (!IsBackedRuntimeTexture(entry))
            {
                binding = null!;
                return false;
            }

            entry.Resolved = ResolveImageBinding(RuntimeImageResource(entry), entry.Key[1..], program, -1);
            binding = CopyRuntimeBinding(entry.Resolved);
            return true;
        }

        // The resolved part of a binding; the acquisition fields belong to one draw.
        private static TextureResource CopyRuntimeBinding(TextureResource resolved) => new()
        {
            Address = resolved.Address,
            ImageIdentifier = resolved.ImageIdentifier,
            Request = resolved.Request,
            IsStorage = resolved.IsStorage,
            IsResident = resolved.IsResident,
            DestinationSelect = resolved.DestinationSelect,
            Width = resolved.Width,
            Height = resolved.Height,
        };

        // Called with the stage's other images: acquires each registered view, refreshes its heap
        // slot and binds the current table and the miss buffer.
        private void BindRuntimeDescriptors(PreparedStageBindings prepared)
        {
            var descriptors = RuntimeDescriptorsFor(prepared.Program);
            var heap = _bindlessImageHeap ?? throw SubmissionScheduler.Fatal("Runtime image descriptors need the bindless image heap.");
            for (var index = 0; index < prepared.RuntimeImages.Count; index++)
            {
                var entry = prepared.RuntimeEntries[index];
                var binding = prepared.RuntimeImages[index];
                var resource = RuntimeImageResource(entry);
                if (IsStaleImage(binding.ImageIdentifier, out var stale))
                {
                    if (stale is not null)
                    {
                        stale.Binding = default;
                    }

                    entry.Resolved = ResolveImageBinding(resource, entry.Key[1..], prepared.Program, -1);
                    prepared.RuntimeImages[index] = binding = CopyRuntimeBinding(entry.Resolved);
                }

                // Acquisition refreshes CPU writes and transitions the image for this draw.
                var image = _imageCache.GetImage(binding.ImageIdentifier);
                binding.View = _imageCache.AcquireTextureView(binding.ImageIdentifier, binding.Request);
                binding.MipViews = [];
                image.Uses.Texture = true;
                binding.CachedImage = image;
                binding.Image = image.Backing.Handle;
                var kind = ImageDescriptorBinding.ForImage(resource) ??
                    throw SubmissionScheduler.Fatal($"A runtime image has no binding class: dimension={entry.Dimension}.");
                var slot = heap.GetOrCreateSlot(kind, entry.Key.AsSpan(1), binding.View, binding.Layout);
                if (slot != entry.Slot)
                {
                    entry.Slot = slot;
                    descriptors.TableDirty = true;
                }
            }

            var table = RuntimeDescriptorTableBuffer(descriptors);
            prepared.Descriptors.RuntimeTable = new BufferView(table.Handle, 0, table.Size);
        }

        private void CommitRuntimeDescriptorBuffers(PreparedStageBindings prepared)
        {
            // Image allocation can complete an earlier submission while this
            // draw is being prepared. Bind feedback storage to the tick that
            // will actually execute the draw, after all image allocations.
            var descriptors = RuntimeDescriptorsFor(prepared.Program);
            var table = RuntimeDescriptorTableBuffer(descriptors);
            var misses = RuntimeDescriptorMissBuffer(descriptors);
            prepared.Descriptors.RuntimeTable = new BufferView(table.Handle, 0, table.Size);
            prepared.Descriptors.RuntimeMisses = new BufferView(misses.Handle, 0, misses.Size);
            ScanRuntimeDescriptorMissesAfterTick(descriptors);
        }

        private void RegisterRuntimeDescriptorMisses(RuntimeDescriptorSet descriptors)
        {
            while (descriptors.Misses.TryDequeue(out var miss))
            {
                var key = RuntimeDescriptorKey.From(miss.Key);
                if (miss.Image)
                {
                    // A descriptor that describes no host texture (a null base or an invalid format)
                    // stays out of the table, so the shader keeps reading the null descriptor for
                    // it, as the hardware does.
                    if (!descriptors.Images.ContainsKey(key) && RuntimeDescriptorTable.SupportsRuntimeView(RuntimeDescriptorTable.ViewDimension(miss.Key[0])) &&
                        ImageRequestBuilders.DescribesHostTexture(miss.Key.AsSpan(1)) && new RuntimeImageEntry(miss.Key) is var entry &&
                        IsBackedRuntimeTexture(entry))
                    {
                        descriptors.Images.Add(key, entry);
                        descriptors.TableDirty = true;
                    }

                    continue;
                }

                if (!descriptors.Samplers.ContainsKey(key))
                {
                    var sampler = _samplerStore.GetSampler(new SamplerDescriptorWords(miss.Key), integerView: false);
                    var slot = _bindlessImageHeap!.GetOrCreateSamplerSlot(sampler);
                    descriptors.Samplers.Add(key, new RuntimeSamplerEntry(miss.Key, slot));
                    descriptors.TableDirty = true;
                }
            }
        }

        // The table buffer of the current entries. A change builds a new buffer: draws already
        // recorded keep reading the one they were bound with until their tick completes.
        private GpuBuffer RuntimeDescriptorTableBuffer(RuntimeDescriptorSet descriptors)
        {
            var current = descriptors.Table;
            if (current is not null && !descriptors.TableDirty)
            {
                return current;
            }

            var images = descriptors.Images.Values.Where(entry => entry.Slot != 0).Select(entry => (entry.Key, entry.Slot)).ToList();
            var samplers = descriptors.Samplers.Values.Select(entry => (entry.Key, entry.Slot)).ToList();
            var words = RuntimeDescriptorTable.Build(images, samplers);
            var buffer = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Upload, 0, BufferUsageFlags.StorageBufferBit, (ulong)words.Length * sizeof(uint));
            buffer.Write(0, MemoryMarshal.AsBytes(words.AsSpan()));
            if (!buffer.IsCoherent)
            {
                buffer.Flush(0, buffer.Size);
            }

            if (current is not null)
            {
                _scheduler.QueueCompletionAction(current.Dispose);
            }

            descriptors.Table = buffer;
            descriptors.TableDirty = false;
            return buffer;
        }

        private GpuBuffer RuntimeDescriptorMissBuffer(RuntimeDescriptorSet descriptors)
        {
            return descriptors.MissBuffers.Acquire(_scheduler.CurrentTick, CreateRuntimeDescriptorMissBuffer);
        }

        private GpuBuffer CreateRuntimeDescriptorMissBuffer()
        {
            var buffer = new GpuBuffer(_deviceInfo, _scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.StorageBufferBit,
                RuntimeDescriptorTable.MissBufferDwords * sizeof(uint));
            buffer.Mapped.Clear();
            if (!buffer.IsCoherent)
            {
                buffer.Flush(0, buffer.Size);
            }

            return buffer;
        }

        // Once per tick that binds runtime descriptors: the misses its draws recorded are read
        // when it completes. Each tick owns separate storage until that read finishes.
        private void ScanRuntimeDescriptorMissesAfterTick(RuntimeDescriptorSet descriptors)
        {
            var tick = _scheduler.CurrentTick;
            if (descriptors.MissScanTick == tick)
            {
                return;
            }

            descriptors.MissScanTick = tick;
            _scheduler.QueueCompletionAction(() =>
                descriptors.MissBuffers.Complete(tick, buffer => ReadRuntimeDescriptorMisses(descriptors, buffer)));
        }

        private void ReadRuntimeDescriptorMisses(RuntimeDescriptorSet descriptors, GpuBuffer buffer)
        {
            if (!buffer.IsCoherent)
            {
                buffer.Invalidate(0, buffer.Size);
            }

            var words = MemoryMarshal.Cast<byte, uint>(buffer.Mapped);
            var imageCount = Math.Min(words[(int)RuntimeDescriptorTable.ImageMissCountDword], RuntimeDescriptorTable.ImageMissCapacity);
            var samplerCount = Math.Min(words[(int)RuntimeDescriptorTable.SamplerMissCountDword], RuntimeDescriptorTable.SamplerMissCapacity);
            for (uint record = 0; record < imageCount; record++)
            {
                var start = (int)(RuntimeDescriptorTable.ImageMissOffset + record * RuntimeDescriptorTable.ImageMissRecordDwords);
                descriptors.Misses.Enqueue((true, words.Slice(start, (int)RuntimeDescriptorTable.ImageMissRecordDwords).ToArray()));
            }

            for (uint record = 0; record < samplerCount; record++)
            {
                var start = (int)(RuntimeDescriptorTable.SamplerMissOffset + record * RuntimeDescriptorTable.SamplerMissRecordDwords);
                descriptors.Misses.Enqueue((false, words.Slice(start, (int)RuntimeDescriptorTable.SamplerMissRecordDwords).ToArray()));
            }

        }

        private void DestroyRuntimeDescriptors()
        {
            foreach (var descriptors in _runtimeDescriptorSets.Values)
            {
                descriptors.Table?.Dispose();
                descriptors.MissBuffers.Dispose();
            }
            _runtimeDescriptorSets.Clear();
        }
    }
}
