// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

// This partial resolves the descriptors that shaders read at run time (MemoryAccessInfo.RuntimeDescriptor).
//
// Such a shader hashes the image and sampler words in its registers and looks them up in a table this host
// builds (RuntimeDescriptorTable), which maps them to a slot of the persistent descriptor heap. The host
// learns which descriptors exist from the shader itself: a lookup that fails records the words in the miss
// buffer of the tick, and reads slot 0 (null image, default sampler) for that draw. When the tick completes
// its misses are read; the next draw of the shader that is prepared registers them, creates their views and
// samplers, and rebuilds the table. A descriptor shows its real texture from the first draw recorded after
// the miss was read, so the first draws that reach a new material render it as the null texture.
//
// A registered image goes through the ordinary texture path on every later draw of its shader: it is looked
// up in the image cache, synchronized with guest memory and transitioned with the draw's other textures, so
// its heap slot always names a view in the layout the draw reads it in. The cost of a draw therefore grows
// with the number of descriptors its shader has registered, not with the number the draw reads.
internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private sealed class RuntimeDescriptorSet
        {
            // The entries and the pending misses of the shader.
            public readonly RuntimeDescriptorRegistry<TextureResource> Registry = new();
            public RuntimeDescriptorBuffers? Buffers;
        }

        // Feedback belongs to the shader that generated it, not to the process: a new scene must not
        // acquire the textures of every older shader.
        private readonly Dictionary<(ShaderStageKind Stage, ulong Hash), RuntimeDescriptorSet> _runtimeDescriptorSets = new();

        // The buffers whose miss buffer the tick being recorded has bound; copied out just before it is submitted.
        private readonly List<RuntimeDescriptorBuffers> _runtimeMissBuffersThisTick = [];

        private void RecordRuntimeMissCopies()
        {
            // A presenter built without its constructor (the tests do) has no list.
            var pending = _runtimeMissBuffersThisTick;
            if (pending is not { Count: > 0 })
            {
                return;
            }

            foreach (var buffers in pending)
            {
                buffers.RecordMissCopies();
            }

            pending.Clear();
        }

        private RuntimeDescriptorSet RuntimeDescriptorsFor(ShaderProgramInfo program)
        {
            var key = (program.Stage, program.Hash);
            if (!_runtimeDescriptorSets.TryGetValue(key, out var descriptors))
            {
                _runtimeDescriptorSets.Add(key, descriptors = new RuntimeDescriptorSet());
            }

            return descriptors;
        }

        // The texture path of a registered image. The view class is part of the key because a Vulkan image
        // load needs a view whose numeric type matches the SPIR-V image type.
        private static ImageResource RuntimeImageResource(RuntimeImageEntry<TextureResource> entry) => new()
        {
            ResourceClass = ShaderCompiler.Resources.ImageResourceClass.Sampled,
            NumericClass = entry.NumericClass,
            Dimension = entry.Dimension,
            R128 = entry.R128,
        };

        // Registers hold whatever a lane computed, so a runtime descriptor can describe no valid view (the
        // hardware reads the null texture then), name memory that is not mapped, or no longer is. An image
        // there would have no guest bytes to track.
        private bool IsBackedRuntimeTexture(RuntimeImageEntry<TextureResource> entry)
        {
            if (!ImageRequestBuilders.DescribesHostTexture(entry.Key.AsSpan(1)) ||
                !ImageRequestBuilders.TryTexture(entry.Key.AsSpan(1), ShapeOf(RuntimeImageResource(entry)), out var resolution))
            {
                return false;
            }

            var data = resolution.Request.Description.Data;
            return ImageDescription.IsEmptyRange(data) || _guestMemory.CanRead(data.Address, data.Size);
        }

        // Called while a stage's resources are prepared: registers what earlier ticks missed and takes this
        // shader's registered images into the draw.
        private void PrepareRuntimeDescriptors(PreparedStageBindings prepared)
        {
            var descriptors = RuntimeDescriptorsFor(prepared.Program);
            var heap = _runtimeDescriptorHeap ?? throw SubmissionScheduler.Fatal(
                $"The program reads its image descriptors at run time, but the device has no runtime descriptor heap: hash=0x{prepared.Program.Hash:X16}.");
            descriptors.Registry.RegisterMisses(
                _scheduler.CurrentTick,
                IsBackedRuntimeTexture,
                sampler => heap.GetOrCreateSamplerSlot(_samplerStore.GetSampler(new SamplerDescriptorWords(sampler), integerView: false)));
            List<RuntimeDescriptorKey>? dropped = null;
            foreach (var (key, entry) in descriptors.Registry.Images)
            {
                if (!TryBindRuntimeImage(entry, prepared.Program, out var binding))
                {
                    (dropped ??= []).Add(key);
                    continue;
                }

                prepared.RuntimeImages.Add(binding);
                prepared.RuntimeEntries.Add(entry);
            }

            if (dropped is not null)
            {
                descriptors.Registry.Remove(dropped);
            }
        }

        // An entry keeps the texture its words resolved to while the cache keeps that image; each draw binds
        // its own copy. Only a new entry, or one whose image the cache retired, is resolved again, and a
        // retired one whose memory the title freed leaves the table, so a shader that still reads the
        // descriptor misses it again.
        private bool TryBindRuntimeImage(RuntimeImageEntry<TextureResource> entry, ShaderProgramInfo program, out TextureResource binding)
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

            entry.Resolved = ResolveRuntimeImage(entry, program);
            binding = CopyRuntimeBinding(entry.Resolved);
            return true;
        }

        // Render-state discovery of a registered image. The result is owned by the entry, not by a draw.
        private TextureResource ResolveRuntimeImage(RuntimeImageEntry<TextureResource> entry, ShaderProgramInfo program)
        {
            var rented = ResolveImageBinding(RuntimeImageResource(entry), entry.Key[1..], program, -1);
            var resolved = new TextureResource
            {
                Address = rented.Address,
                ImageIdentifier = rented.ImageIdentifier,
                Request = rented.Request,
                DestinationSelect = rented.DestinationSelect,
                Width = rented.Width,
                Height = rented.Height,
            };
            ReturnTextureResource(rented);
            return resolved;
        }

        // The resolved part of a binding; the acquisition fields belong to one draw.
        private TextureResource CopyRuntimeBinding(TextureResource resolved)
        {
            var copy = RentTextureResource();
            copy.Address = resolved.Address;
            copy.ImageIdentifier = resolved.ImageIdentifier;
            copy.Request = resolved.Request;
            copy.IsStorage = false;
            copy.DestinationSelect = resolved.DestinationSelect;
            copy.Width = resolved.Width;
            copy.Height = resolved.Height;
            return copy;
        }

        // Called with the stage's other images, once their views are acquired: acquires each registered view.
        private void BindRuntimeDescriptors(PreparedStageBindings prepared)
        {
            for (var index = 0; index < prepared.RuntimeImages.Count; index++)
            {
                var entry = prepared.RuntimeEntries[index];
                var binding = prepared.RuntimeImages[index];
                if (IsStaleImage(binding.ImageIdentifier, out var stale))
                {
                    if (stale is not null)
                    {
                        stale.Binding = default;
                    }

                    ReturnTextureResource(binding);
                    entry.Resolved = ResolveRuntimeImage(entry, prepared.Program);
                    prepared.RuntimeImages[index] = binding = CopyRuntimeBinding(entry.Resolved);
                }

                // Acquisition refreshes CPU writes of the guest memory and prepares the image for this draw.
                var image = _imageCache.GetImage(binding.ImageIdentifier);
                binding.View = _imageCache.AcquireTextureView(binding.ImageIdentifier, binding.Request);
                binding.MipViews = [];
                image.Uses.Texture = true;
                binding.CachedImage = image;
                binding.Image = image.Backing.Handle;
            }
        }

        // Called while the stage is committed, after its textures were transitioned: the layout of each view is
        // final, so the heap slot, which carries that layout, can be taken. Binds the table and the miss buffer.
        private void CommitRuntimeDescriptors(PreparedStageBindings prepared)
        {
            var descriptors = RuntimeDescriptorsFor(prepared.Program);
            var heap = _runtimeDescriptorHeap ?? throw SubmissionScheduler.Fatal("Runtime image descriptors need the runtime descriptor heap.");
            for (var index = 0; index < prepared.RuntimeImages.Count; index++)
            {
                var entry = prepared.RuntimeEntries[index];
                var binding = prepared.RuntimeImages[index];
                var slot = binding.View.Handle == 0 ? 0 : heap.GetOrCreateImageSlot(binding.View, binding.Layout);
                if (slot != entry.Slot)
                {
                    entry.Slot = slot;
                    descriptors.Registry.TableDirty = true;
                }
            }

            var buffers = descriptors.Buffers ??= new RuntimeDescriptorBuffers(_deviceInfo, _scheduler, _runtimeMissBuffersThisTick);
            var table = buffers.Table(descriptors.Registry);
            prepared.Descriptors.RuntimeTable = new BufferView(table.Handle, 0, table.Size);
            // The tick the draw records in is fixed by now, so the buffer is read when that tick completes.
            var misses = buffers.Misses(descriptors.Registry);
            prepared.Descriptors.RuntimeMisses = new BufferView(misses.Handle, 0, misses.Size);
        }

        private void DestroyRuntimeDescriptors()
        {
            foreach (var descriptors in _runtimeDescriptorSets.Values)
            {
                descriptors.Buffers?.Dispose();
            }

            _runtimeDescriptorSets.Clear();
        }
    }
}
