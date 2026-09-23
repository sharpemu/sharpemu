<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu: environment variables that are not on the GUI options pages

## Scope

This list contains each `SHARPEMU_*` environment variable in the repository that the GUI does not show.
The GUI shows 14 variables on its **Options** and **Game options** pages. This list does not contain them:
`SHARPEMU_BTHID_UNAVAILABLE`, `SHARPEMU_CRASH_CAPTURE`, `SHARPEMU_DEFAULT_PROFILE`,
`SHARPEMU_DISABLE_IMPORT_LOOP_GUARD`, `SHARPEMU_DUMP_SPIRV`, `SHARPEMU_LOG_DIRECT_MEMORY`, `SHARPEMU_LOG_IO`,
`SHARPEMU_LOG_NP`, `SHARPEMU_PROFILE_PERFORMANCE`, `SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE`,
`SHARPEMU_RENDERDOC`, `SHARPEMU_VK_DISABLE_IMPLICITS`, `SHARPEMU_STRICT_COMPUTE`, `SHARPEMU_VK_VALIDATION`, `SHARPEMU_WRITABLE_APP0`.

The list contains 252 variables. The source was examined on 2026-09-19, branch `dev`.
The descriptions come from the code that reads each variable. The emulator was not started for this list.

## How to use a variable

1. Set the variable in the shell before you start the emulator or the GUI.
2. You can also enter additional env vars using the button next to the "Play" button from GUI.
3. The GUI also accepts a variable in the `EnvironmentToggles` list of its settings file. Write `NAME` or `NAME=value`. An entry without a value gets the value `1`.
4. The emulator reads most variables one time, when it starts. Start the game again after a change.
5. If the **Value** column shows `1`, only the value `1` turns the function on, unless the row gives other values.

## Number of variables in each group

| Group | Variables |
| --- | --- |
| Behavior switches | 16 |
| GPU and Vulkan | 50 |
| Metal | 10 |
| Audio and video | 7 |
| Input | 5 |
| Memory and CPU | 17 |
| Loader | 11 |
| Debugger | 1 |
| Log channels | 72 |
| Traces | 22 |
| Data dumps | 12 |
| Performance measurement | 16 |
| Internal | 1 |
| Tests and tools | 12 |

## Behavior switches

These variables change how the emulator operates.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_AGC_VERSION13_DEFAULTS` | text (`legacy`, `generic` or `0`) | Selects the register defaults for AGC version 13 requests. The default uses the version 11 compact tables. The values `legacy`, `generic` and `0` use the older generic layout. | `AgcExports.Init.cs` |
| `SHARPEMU_DISABLE_LLE_LIBC` | `1` | Stops the use of the guest libc module for libc exports. The emulator then uses its HLE handlers for these exports. | `DirectExecutionBackend.cs` |
| `SHARPEMU_HOLD_FIRST_FLIP_MS` | number | Stops the thread that submits the flip for this number of milliseconds on one guest flip. The range is 0 to 60000 and the default is 0 (off). `SHARPEMU_HOLD_FLIP_NUMBER` selects the flip. | `VideoOutExports.cs` |
| `SHARPEMU_HOLD_FLIP_NUMBER` | number | Selects the guest flip that `SHARPEMU_HOLD_FIRST_FLIP_MS` holds. The minimum is 1 and the default is 1 (the first flip). | `VideoOutExports.cs` |
| `SHARPEMU_IGNORE_GUEST_EXCEPTIONS` | `1` | `sceKernelRaiseException` returns OK and does not call the installed guest exception handler. Default is off. | `KernelExceptionCompatExports.cs` |
| `SHARPEMU_IMPORT_LOOP_GUARD_SECONDS` | number | Sets the time that an import call pattern can repeat before the import loop guard forces a guest exit. The default is 5 seconds. The value 0 turns the guard off. | `DirectExecutionBackend.Imports.cs`, `DirectExecutionBackend.cs` |
| `SHARPEMU_NET_REDIRECT` | IP address | Sends guest `connect` and `sendto` traffic to this host IP address. When unset, the emulator permits outbound guest traffic only to loopback addresses. If the value is not a valid IP address, the emulator keeps the original destination and permits the traffic. | `KernelSocketCompatExports.cs` |
| `SHARPEMU_NO_FLIP_PACING` | `1` | Stops flip pacing. The emulator completes each flip immediately and does not wait for the display refresh time. The default is off. | `VideoOutExports.cs`, `VideoOutExports.FlipRequests.cs` |
| `SHARPEMU_OVERLAY` | `0` | Hides the performance overlay at start, even when the video options have the overlay on. All other values use the video options. | `PerfOverlay.cs`, `PerformanceOverlayState.cs` |
| `SHARPEMU_RETAIN_SUBMITTED_INDEX_DATA` | `0` | The default is on. The emulator keeps a copy of submitted index data until the translated draws run. Set to `0` to stop this copy. Use `0` only for a comparison test. | `AgcExports.SubmittedGeometry.cs` |
| `SHARPEMU_RETAIN_SUBMITTED_VERTEX_DATA` | `0` | The default is on. The emulator keeps a copy of submitted vertex data until the translated draws run. Set to `0` to stop this copy. Use `0` only for a comparison test. | `AgcExports.SubmittedGeometry.cs` |
| `SHARPEMU_SAVEDATA_DIR` | path | Set the root directory for save data. The default is `user/savedata` next to the executable. When this variable is set, the emulator does not move save data from the old layout. | `SaveDataStorage.cs`, `SaveDataExports.cs` |
| `SHARPEMU_STALL_WATCHDOG_SECONDS` | number (seconds) | Sets the time without import progress before the stall watchdog operates. The default is 20. The watchdog writes a snapshot and stops the process with exit code 4. It does not operate when a guest thread is ready or a known wait is in progress. `0` stops the watchdog. | `DirectExecutionBackend.cs` |
| `SHARPEMU_STRICT_RWLOCK_WRITER_PREFERENCE` | `1` | Set to `1` to make a guest thread take a free pthread rwlock as the exclusive writer. By default, the thread gets a compatibility writer record and the lock has no writer thread. | `KernelPthreadExtendedCompatExports.cs` |
| `SHARPEMU_TEMP0_DIR` | path | Set the host directory for the guest `temp0` mount. The default is `user/temp/<app name>/temp0` next to the executable. When unset, the kernel code sets this variable to the default path. | `AppContentExports.cs`, `KernelMemoryCompatExports.cs` |
| `SHARPEMU_TRACE_TITLE_SHADER_STATE` | `1` | The SPIR-V translator changes the pixel shader at address 0x0000000500781200 for diagnosis. The first float output then shows the execution state, the wave mask state, and the export state as colors. Default is off. | `Gen5SpirvTranslator.cs` |

## GPU and Vulkan

These variables change the GPU path or the Vulkan presenter.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_CAPTURE_PIXEL_EXEC_PCS` | list (`pc:register` pairs) | At each given instruction PC, the pixel shader writes the execution flag to the given VGPR register. The value is 1.0 when the flag is set and 0.0 when it is not. `SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS` must match the shader address. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_IMAGE_ADDRESS` | hex address | Selects the pixel shader for the image capture by its program address. The default is no capture. Use it with `SHARPEMU_CAPTURE_PIXEL_IMAGE_PC`. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_IMAGE_PC` | number (instruction PC) | Sets the image instruction whose result the SPIR-V translator copies to four VGPR registers. `SHARPEMU_CAPTURE_PIXEL_IMAGE_ADDRESS` must match the shader address. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_IMAGE_VGPR_BASE` | number (0 to 252) | Sets the first VGPR register that receives the captured image components. The default is 248. Values above 252 use the default. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS` | hex address | Selects the pixel shader for the VGPR capture, execution capture and path marker functions by its program address. The default is no capture. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_VGPR_DEST_BASE` | number (0 to 252) | Sets the first destination VGPR register for `SHARPEMU_CAPTURE_PIXEL_VGPR_SOURCES`. The default is 248. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_VGPR_IGNORE_EXEC` | `1` | Makes the VGPR capture copies occur without the execution flag guard. The default applies the guard. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_VGPR_PC` | number (instruction PC) | Sets the instruction PC at which the translator copies the source VGPR registers. `SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS` must match the shader address. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_VGPR_POINTS` | list (`pc:source:destination` triples) | At each given instruction PC, the pixel shader copies the source VGPR register to the destination register. Register numbers must be less than 256. `SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS` must match the shader address. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_CAPTURE_PIXEL_VGPR_SOURCES` | list (one to four register numbers) | Gives the source VGPR registers that the translator copies at `SHARPEMU_CAPTURE_PIXEL_VGPR_PC`. The copies go to the registers that start at the destination base. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_DETILE` | `1` or `0` | Controls the detile step for tiled surfaces. The default detiles only the trusted swizzle modes. `1` also detiles the approximate modes and `0` uploads all surfaces as raw data. | `GnmTiling.cs` |
| `SHARPEMU_DISABLE_FILL_CLEAR` | `1` | Stops the adaptation that treats a transparent premultiplied fill draw as a target overwrite. The default keeps the adaptation on. | `ShaderPipelineCache.Adaptations.cs` |
| `SHARPEMU_ENABLE_CHUNKED_DRAWS` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_ATTRIBUTE_FRAGMENT` | number (attribute location) | The Vulkan presenter reads the number into a field. No code uses the field, so the variable has no effect. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_DEFAULT_RASTER_STATE` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_FULLSCREEN_PIPELINE` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_FULLSCREEN_VERTEX` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_PACKED_EXPORT_ONE` | `1` | The SPIR-V translator replaces each packed half-float export component with the constant 1.0. This applies to all shaders. Default is off. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_PACKED_EXPORT_STORE_ONE` | `1` | The SPIR-V translator writes (1.0, 1.0) to the packed half-float pair before the export reads it. This applies only to the pixel shader at guest address 0x0000000500781200. Default is off. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_PACKED_STORE_EXEC_VALUES` | `1` | Each packed half-float store writes 1.0 when the EXEC mask is active and 0.5 when it is not. This shows the EXEC state as a color. This applies only to the pixel shader at guest address 0x0000000500781200. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_PIXEL_EXPORT_ADDRESS` | hex address | Sets the shader address filter for the pixel export debug overrides. The value is a hex address, and the `0x` prefix is optional. When unset, the overrides apply to all pixel shaders. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_PIXEL_EXPORT_PACK_VGPR_BASE` | number | Float pixel outputs read four vector registers that start at this base plus four times the export target. The translator packs the values to half-float and unpacks them again. The address filters select the shaders. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_PIXEL_EXPORT_VGPR_ADDRESS` | hex address | Sets the shader address filter for the two VGPR base overrides. The `0x` prefix is optional. When unset, the filter from `SHARPEMU_FORCE_PIXEL_EXPORT_ADDRESS` applies. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_PIXEL_EXPORT_VGPR_BASE` | number | Float pixel outputs read four vector registers that start at this base plus four times the export target. These values replace the decoded export sources. The address filters select the shaders. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_PIXEL_MAGENTA` | `1` | Each selected pixel shader export writes opaque magenta. The control flow, EXEC mask, geometry and raster state stay the same. Use `SHARPEMU_FORCE_PIXEL_EXPORT_ADDRESS` to select one shader. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_SOLID_FRAGMENT` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect at this time. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_TITLE_COMPARE_4D4` | `1` | The compare instruction at shader PC 0x4D4 always gives the result true. This applies only to the shader at guest address 0x0000000500781200. Default is off. | `Gen5SpirvTranslator.Alu.cs` |
| `SHARPEMU_FORCE_TITLE_COMPARE_540` | `1` | The compare instruction at shader PC 0x540 always gives the result true. This applies only to the shader at guest address 0x0000000500781200. Default is off. | `Gen5SpirvTranslator.Alu.cs` |
| `SHARPEMU_FORCE_TITLE_DEFAULT_RASTER_STATE` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect at this time. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_TITLE_EARLY_COLOR` | `1` | The pixel shader at guest address 0x0000000500781200 writes a constant to its first output and returns at once. A float output gets opaque magenta. An integer output gets zero. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_TITLE_EXPORT_EXEC` | `1` | The translator sets the EXEC mask to true and writes 1 to 64-bit register 126 before each pixel export store. This applies only to the pixel shader at guest address 0x0000000500781200. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_TITLE_FULLSCREEN_VERTEX` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect at this time. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_TITLE_SINGLE_MRT` | `1` | The translator declares only the first pixel output for the pixel shader at guest address 0x0000000500781200. It does not declare the other render target outputs. Default is off. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_FORCE_TITLE_SOLID_FRAGMENT` | `1` | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect at this time. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_FORCE_TITLE_VERTEX_OUTPUTS_ONE` | `1` | Vertex export targets 32 to 35 write the vector (1, 1, 1, 1). This applies only to the vertex shader at guest address 0x0000000500780000. Default is off. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_GPU_BACKEND` | `vulkan` or `metal` | Selects the guest GPU backend. The default is Vulkan. The value `metal` works only on macOS, and other hosts show a warning and use Vulkan. | `GuestGpu.cs` |
| `SHARPEMU_GRAPHICS_SUBGROUPS` | `0` or `1` | Set `1` to use native Vulkan subgroup operations in graphics shaders. Set `0` to use the fallback path. When unset, the presenter uses them only if the subgroup size is 32 and the vertex and fragment stages support subgroups. | `VulkanVideoPresenter.cs`, `VulkanVideoPresenter.Device.Setup.cs` |
| `SHARPEMU_MARK_PIXEL_PCS` | list of `pc:register` pairs | Makes the SPIR-V pixel shader write the float value 1.0 to a shader register when execution reaches the given instruction PC. Use decimal numbers, a register below 256, and a comma between pairs. It has an effect only for the shader address in `SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS`. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_MAX_GUEST_WORK_PER_RENDER` | number | Sets the maximum number of guest command stream slices that the Vulkan presenter runs in one render pass. The default is 1024, or 256 on macOS. The value must be more than 0. | `VulkanVideoPresenter.cs`, `VulkanVideoPresenter.CommandStream.cs` |
| `SHARPEMU_RENDERDOC_CAPTURE_TIMEOUT_SECONDS` | number (seconds) | Set the maximum time for a RenderDoc frame capture. The emulator discards a capture that does not end in this time. The range is 1 to 120. The default is 15. | `RenderDocCapture.cs` |
| `SHARPEMU_RENDERDOC_DLL` | path | Set the path of the RenderDoc library file. The emulator tries this path first. When unset or when the load fails, it searches the default library name and the known install paths. | `RenderDocCapture.cs` |
| `SHARPEMU_RENDERDOC_WAIT` | number (seconds) or `enter` | Set to make the emulator wait before it starts Vulkan, so that you can attach RenderDoc. `enter` waits until you press Enter. A number waits that many seconds, from 1 to 300. Other text waits 15 seconds. | `VulkanVideoPresenter.Device.Setup.cs` |
| `SHARPEMU_RENDER_WORK_BUDGET_MS` | number (milliseconds) | Set the maximum time that one render call uses for queued guest work in the Vulkan presenter. Work that remains stays in the queue for the next frame. `0` removes the limit. The default is 12 on macOS and 0 on other hosts. | `VulkanVideoPresenter.cs`, `VulkanVideoPresenter.RenderLoop.cs` |
| `SHARPEMU_SHADER_MAX_STEPS` | number | Set the maximum number of dispatcher loop steps in each translated shader invocation. The limit makes sure that an incorrect loop stops. `0` removes the limit. The default is 100000. | `Gen5SpirvTranslator.cs`, `Gen5MslTranslator.cs` |
| `SHARPEMU_SKIP_ALL_COMPUTE` | `1` | Set to `1` to make the Metal presenter skip all compute dispatches. The Vulkan presenter reads the value but does not use it. The default is off. | `MetalVideoPresenter.Compute.cs`, `VulkanVideoPresenter.cs` |
| `SHARPEMU_SKIP_TALL_COMPUTE_Z` | number | Effect not clear from the code. The Vulkan presenter reads the number into a field for a minimum Z group count. No code uses that field. The default is 0. | `VulkanVideoPresenter.cs` |
| `SHARPEMU_VK_DEBUG_LABELS` | `1` | Turns on Vulkan object names and command labels for capture tools. `SHARPEMU_VK_VALIDATION=1` also turns them on. Default is off because the labels add overhead to each draw. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_VK_DEVICE` | text (part of a device name) | Selects the Vulkan device whose name contains this text. The comparison ignores case. When unset, the presenter gives a score to each device and prefers a discrete GPU. | `VulkanVideoPresenter.Device.Setup.cs`, `VulkanVideoPresenter.cs` |
| `SHARPEMU_VK_PIPELINE_CACHE_PATH` | path | Sets the path of the Vulkan pipeline cache file. The path can contain environment variables. Default is `user/pipeline_cache/<title id>/` below the application directory. | `VulkanVideoPresenter.Device.Setup.cs`, `VulkanPipelineCacheStorage.cs` |
| `SHARPEMU_VK_PIPELINE_CACHE` | `0` | `0` stops the save and the load of the Vulkan pipeline cache file. The cache then stays in memory only. Default is a persistent cache file. | `VulkanVideoPresenter.Device.Setup.cs` |

## Metal

These variables apply to the Metal backend.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_GPU_DETILE` | `0` | The Metal backend decodes supported tiled texture data on the GPU by default. Set `0` to decode all tiled textures on the CPU. | `MetalTextureSnapshots.cs`, `GuestGpuTypes.cs` |
| `SHARPEMU_LOG_GPU_DETILE` | `1` | Writes each texture tile mode and each GPU detile decision one time to stderr. It applies to the Metal texture path only. The emulator reads the value one time at start. | `MetalTextureSnapshots.cs` |
| `SHARPEMU_METAL_CAP_DRAWABLE` | `1` | Limits the long edge of the Metal drawable to 1920 pixels and keeps the aspect ratio. The default is off, so the drawable uses the full window pixel size. | `MetalVideoPresenter.cs` |
| `SHARPEMU_METAL_DBG` | `solid` or `uv` | Replaces the Metal present fragment shader with a test shader. `solid` shows a green frame. `uv` shows the value of vertex attribute 0. All other values use the normal present shader. | `MetalVideoPresenter.cs` |
| `SHARPEMU_METAL_FULL_DRAWABLE` | `1` | Cancels `SHARPEMU_METAL_CAP_DRAWABLE=1`, so the Metal drawable uses the full window pixel size. It has no effect when the cap is off. | `MetalVideoPresenter.cs` |
| `SHARPEMU_NO_TEXTURE_SKIP` | `1` | Stops the reuse of cached texture snapshots in the Metal backend, so each texture is copied again. Use it to make sure that a snapshot is not stale. The default is off. | `MetalTextureSnapshots.cs` |
| `SHARPEMU_REUSE_GUEST_TEXTURE_SNAPSHOTS` | `0` | The default is on. The Metal backend uses a guest texture snapshot again when the snapshot key is the same. Set to `0` to make a new snapshot each time. | `MetalTextureSnapshots.cs` |
| `SHARPEMU_TEXTURE_DUMP_DIR` | path | Set a directory to make the Metal backend write raw texture source bytes to `.bin` files. The file name contains the address, size, pitch, format and tile mode. The limit is 200 files. | `MetalTextureSnapshots.cs` |
| `SHARPEMU_TEXTURE_LINEAR_DUMP_DIR` | path | Set a directory to make the Metal backend write the linear texture bytes to `.linear.bin` files. The file name contains the address, size, pitch, format and tile mode. The limit is 200 files. | `MetalTextureSnapshots.cs` |
| `SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS` | hex address | The Metal backend writes an `agc.storage_initial_data` trace line when it makes the texture at this guest address. The line shows the upload state and the read result of the initial data. | `MetalTextureSnapshots.cs` |

## Audio and video

These variables apply to audio output and video playback.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_ALSA_DEVICE` | text (ALSA device name) | Sets the ALSA playback device on Linux. The default is `default`. | `PosixAlsaAudioStream.cs` |
| `SHARPEMU_AUDIO_LATENCY_MS` | number (milliseconds) | Sets the target depth of the SDL audio queue, which is the playback latency. The default is 60. Values of zero or less use the default. | `SdlHostAudio.cs` |
| `SHARPEMU_AUDIO_OUT2_STACK_WRITES` | `1` or list (`portstate`, `systemstate`, `speaker`) | Controls writes of AudioOut2 results to output buffers on the guest stack. The default `1` permits all writes. A list permits only the named outputs and the other calls return success without a write. | `AudioOut2Exports.cs` |
| `SHARPEMU_BINK_MODE` | text (`native`, `ffmpeg`, `dummy`, `skip`, `guest`) | Selects how the emulator handles Bink movies. `native` or `ffmpeg` decodes on the host and is the default. `dummy` shows a placeholder frame, `skip` skips the movie, and `guest` lets the guest decode. | `HostMovieBridge.cs` |
| `SHARPEMU_LOG_MOVIE_SYNC` | `1` | Records the difference between the movie clock and the guest audio position during movie playback. The emulator reads the value one time at start. | `MediaFramePlayback.cs` |
| `SHARPEMU_MOVIE_CLOCK` | `wall` | Sets the time base for host-decoded movie playback. By default, playback follows the guest audio clock and uses the wall clock when no guest audio flows. `wall` makes playback always use the wall clock. | `MediaFramePlayback.cs` |
| `SHARPEMU_TRACE_AVPLAYER_IMAGES` | `1` | Set to `1` to write `[AVPLAYER][TRACE]` lines for video buffer addresses and the first 16 video frame payloads. It also marks the video buffer ranges for image traces in other components. The default is off. | `AvPlayerExports.cs` |

## Input

These variables apply to the controller and input libraries.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_AUTO_CROSS` | list (seconds, for example `40,52,64`) | Presses the Cross button for 0.4 seconds at each time in the list. The times are seconds from process start. The default is no automatic press. | `PadExports.cs` |
| `SHARPEMU_BTHID_CB_FAIL` | text (`nf` or `ni`) | Makes `sceBluetoothHidRegisterCallback` return an error. `nf` returns NOT_FOUND and `ni` returns NOT_IMPLEMENTED. The default returns the usual result. | `BluetoothHidExports.cs` |
| `SHARPEMU_BTHID_EVENT_CODE` | number (decimal or `0x` hex) | Sets the event code of the synthetic Bluetooth HID callback event. The default is 0. It has an effect only with `SHARPEMU_BTHID_FIRE_CALLBACK=1`. | `BluetoothHidExports.cs` |
| `SHARPEMU_BTHID_EVENT_SIZE` | number (bytes, decimal or `0x` hex) | Sets the size of the zeroed event structure for the synthetic Bluetooth HID callback. The default is 256. It has an effect only with `SHARPEMU_BTHID_FIRE_CALLBACK=1`. | `BluetoothHidExports.cs` |
| `SHARPEMU_BTHID_FIRE_CALLBACK` | `1` | Calls the registered Bluetooth HID guest callback one time with a zeroed event structure. The call occurs at the first `sceBluetoothHidRegisterDevice`. The default is off. | `BluetoothHidExports.cs` |

## Memory and CPU

These variables apply to guest memory and guest code execution.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_DISABLE_GUEST_ALLOCATOR_HOLE_RECOVERY` | `1` | Stops the Windows fault recovery for a guest allocator that reads an empty pool node. The default recovers the fault and continues at the allocator fallback code. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_DISABLE_MITIGATION_RELAUNCH` | `1` | Stops the Windows relaunch of the emulator as a child process with Control Flow Guard and CET shadow stacks off. The default starts the child process. | `Program.cs`, `SelfLoader.cs` |
| `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS` | `1` | Stops the use of native worker threads for guest thread execution on Windows. The default uses the native workers. | `DirectExecutionBackend.NativeWorker.cs` |
| `SHARPEMU_DISABLE_POSIX_SIGNALS` | `1` | Does not install the POSIX signal handlers on Linux and macOS. The emulator then cannot recover guest faults. | `DirectExecutionBackend.PosixSignals.cs` |
| `SHARPEMU_DISABLE_RAW_HANDLER` | `1` | Does not install the raw vectored exception handler on Windows. On POSIX hosts it stops the raw sentinel recovery. The default keeps the two functions on. | `DirectExecutionBackend.Exceptions.cs`, `DirectExecutionBackend.PosixSignals.cs` |
| `SHARPEMU_IGNORE_INT41` | `0` or `false` | The fault handler skips guest `int 0x41` instructions and continues at the next instruction by default. Set `0` or `false` to stop this recovery. The emulator shows a warning for the first 16 traps and then for each 65536th trap. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_IGNORE_STACK_CHK` | `1` | Recovers from a guest stack-protector failure call (NID `Ou3iL1abvng`) when a UD2 instruction follows the call. The emulator sets the return address to the function epilogue 20 bytes before. It does this only when the code bytes match the known pattern. | `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LAZY_RESERVE_PRIME_MB` | number | Sets the number of megabytes that the emulator commits at the start of each reserve-only memory region. The default is 64 and the maximum is 4096. The value 0 turns the initial commit off. | `PhysicalVirtualMemory.cs` |
| `SHARPEMU_NATIVE_WORKER_MAX_CONCURRENT` | number | Sets the maximum number of native guest worker runs that execute at the same time. The default is 2. The emulator limits the value to the range 1 to 64. | `DirectExecutionBackend.NativeWorker.cs`, `DirectExecutionBackend.cs` |
| `SHARPEMU_RESERVED_HOST_LANES` | number | Set the number of host logical processors that guest thread affinity does not use. The emulator keeps these processors for its own threads. The default is three eighths of the processor count, with a minimum of 2. | `DirectExecutionBackend.cs` |
| `SHARPEMU_TSC_FREQ_HZ` | number (Hz, minimum 1000000) | Sets the TSC frequency that the kernel library reports to the guest. When unset, the emulator uses the calibrated RDTSC frequency, then the CPUID frequency, then the host stopwatch frequency. | `KernelRuntimeCompatExports.cs` |
| `SHARPEMU_WATCH_BULK_DEST_HI` | hex number | Limits the `SHARPEMU_WATCH_BULK_TORN` scan to writes whose upper 32 address bits equal this value. When unset, the scan uses the direct-memory address band. | `GuestWriteWatch.cs` |
| `SHARPEMU_WATCH_BULK_TORN` | `1` | Examines each aligned 8-byte value in managed bulk writes to guest memory. It reports torn values and shifted pointer values with a stack trace. It writes a maximum of 64 reports for each kind. | `GuestWriteWatch.cs` |
| `SHARPEMU_WATCH_POOL_HEADER` | `1` | Monitors the header slot at offset 0x40 of each pool mapping with size 0x10000 and protection 0xF2. It reports each managed write to a monitored slot with a stack trace. It monitors a maximum of 64 slots. | `GuestWriteWatch.cs` |
| `SHARPEMU_WATCH_VALUE1` | `1` | Reports managed writes of 1 to 8 bytes that have the value 1 and go to the direct-memory address band. Each report has a stack trace. It writes a maximum of 128 reports. | `GuestWriteWatch.cs` |
| `SHARPEMU_WATCH_VALUE_PATTERN` | `1` | Reports each managed 8-byte write whose low half is 1 and whose high half is between 1 and 0xFFFF. Each report has a stack trace. | `GuestWriteWatch.cs` |
| `SHARPEMU_WATCH_WRITE` | hex address | Monitors the 8 bytes at this guest address. It reports each managed write that touches these bytes with the address, the length, the first value, and a stack trace. Default is off. | `GuestWriteWatch.cs` |

## Loader

These variables apply to the program loader and the module system.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_AMPR_INDEX_CACHE` | path (directory) | Sets the directory for the AMPR file index cache. The default is `SharpEmu/ampr-index` in the local application data folder. | `AmprFileRegistry.cs` |
| `SHARPEMU_AMPR_REINDEX` | `1` | Ignores the AMPR index cache on disk and scans the application file tree again. Use it after manual changes to the application files. | `AmprFileRegistry.cs` |
| `SHARPEMU_APP0_DIR` | path (directory) | Sets the host directory for the guest `/app0` mount. The default is the directory of the loaded executable. The directory name also identifies the per-application data folders. | `SharpEmuRuntime.cs`, `KernelMemoryCompatExports.cs`, `AppContentExports.cs` |
| `SHARPEMU_DEVLOG_APP_DIR` | path (directory) | Sets the host directory for the guest devlog application path. The default is `user/game_logs/<title id>/devlog/app` below the emulator directory. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_DISABLE_RED_ZONE_PATCH` | `1` | Stops the load-time patch that protects the guest stack red zone on Windows and macOS. The default applies the patch. The vector store split for Rosetta stays on when it is necessary. | `GuestRedZonePatcher.cs` |
| `SHARPEMU_DOWNLOAD0_DIR` | path (directory) | Sets the host directory for the guest `/download0` mount. The default is a `download0` folder in the per-application writable directory. The emulator sets the variable to the default when it is not set. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_GUEST_ARGS` | text | Adds arguments to the guest process entry frame after the image name. White space divides the arguments. The emulator uses only the first two. | `CpuDispatcher.cs` |
| `SHARPEMU_HOSTAPP_DIR` | path | Sets the host directory for the guest `/hostapp` mount. The default is `user/game_logs/<title id>/hostapp` below the emulator directory. The emulator makes the directory if it is absent. | `KernelMemoryCompatExports.cs`, `KernelGameLogPathTests.cs` |
| `SHARPEMU_LLE_LIBC_ALL` | `1` | Sends all libc imports to the guest libc module and not to the HLE handlers. `SHARPEMU_DISABLE_LLE_LIBC=1` and `SHARPEMU_LLE_LIBC_SAFE_ONLY=off` have priority. Default is off. | `DirectExecutionBackend.cs` |
| `SHARPEMU_LLE_LIBC_SAFE_ONLY` | `1`, `0`, `off`, `false` or `none` | Controls which libc imports use the guest libc module. Unset or `1` uses guest code only for exports on the safe list. `0` uses guest code for all libc exports, and `off`, `false` or `none` uses HLE for all of them. | `DirectExecutionBackend.cs` |
| `SHARPEMU_PRELOAD_ALL_SCE_MODULES` | `1` | Set to `1` to preload all system modules. When unset, the runtime does not preload the modules in its internal skip list. | `SharpEmuRuntime.cs` |

## Debugger

These variables apply to the debug server.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_SENTINEL_PROBE` | `1` | Set to `1` to call the native entry stub with the unresolved sentinel address 0xFFFE before the guest entry. The emulator writes a log line before and after the call. The default is off. | `DirectExecutionBackend.cs`, `Gen5NativeReturnSmokeTests.cs` |

## Log channels

Each variable adds one group of messages to the log. The default is off.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_LOG_ACM` | `1` | Writes `acm.` trace lines from the Acm library handlers to stderr. Default is off. | `AcmExports.cs` |
| `SHARPEMU_LOG_AGC_SHADER` | `1` | Writes only the AGC shader trace lines and the Vulkan shader trace lines. `SHARPEMU_LOG_AGC=1` also turns this on. Default is off. | `AgcExports.cs`, `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_LOG_AGC` | `1` | Starts the full AGC trace: command submissions, shader creation, draws and dispatches. It also turns on the AGC shader trace and the Vulkan shader trace. Default is off. | `AgcExports.cs`, `RenderExecutor.Trace.cs`, `AgcExports.ShaderState.cs` |
| `SHARPEMU_LOG_AJM` | `1` | Writes `ajm.` trace lines to stderr: context initialization, codec registration, split buffer descriptors and ATRAC9 decode state. Default is off. | `AjmExports.cs`, `Atrac9DecodeState.cs` |
| `SHARPEMU_LOG_ALLOC_IMPORTS` | `1` | Writes more trace lines for `sceKernelGetGPI` (the returned mask) and for the PRT aperture set call (the raw register values). Default is off. | `KernelRuntimeCompatExports.cs` |
| `SHARPEMU_LOG_ALL_IMPORTS` | `1` | Writes a debug line for each import stub during setup. The lines show if the import uses a direct bridge, a trampoline, HLE or a runtime symbol. Default is off. | `DirectExecutionBackend.cs` |
| `SHARPEMU_LOG_AMPR_READS` | `1` | Writes only the AMPR file read trace lines and the pak directory tracker lines. `SHARPEMU_LOG_AMPR=1` also turns this on. Default is off. | `AmprExports.cs`, `PakDirectoryTracker.cs` |
| `SHARPEMU_LOG_AMPR` | `1` | Writes trace lines for AMPR command buffers, APR submissions and waits, and the pak directory tracker. It also turns on the AMPR read trace. Default is off. | `AmprExports.cs`, `KernelAprCompatExports.cs`, `PakDirectoryTracker.cs` |
| `SHARPEMU_LOG_APP_CONTENT` | `1` | Writes an `app_content` trace line to stderr for each AppContent call. The default is off. | `AppContentExports.cs` |
| `SHARPEMU_LOG_AUDIO_OUT2` | `1` | Writes an `audio_out2` trace line to stderr for each AudioOut2 operation. The default is off. | `AudioOut2Exports.cs` |
| `SHARPEMU_LOG_AUDIO_OUT` | `1` | Records `sceAudioOutOutput` calls and the peak amplitude of the submitted samples. It writes the first 8 calls and then every 200th call. The emulator reads the value one time at start. | `AudioOutExports.cs` |
| `SHARPEMU_LOG_BOOTSTRAP` | `1` | Writes a trace line for each call through the bootstrap bridge import. The line shows the handle, the symbol name and the output address. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_CONTEXT` | `1` | Writes `guest_context` trace lines to stderr. Each import dispatch shows the NID, the return address, the managed thread, the guest thread and the fiber. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_DATA_REBIND` | `1` | Writes one `[RUNTIME]` line for each imported data relocation. The line shows if the relocation is rebound, unresolved or write-failed. | `SharpEmuRuntime.cs` |
| `SHARPEMU_LOG_DISCMAP` | `1` | Writes a trace line for each DiscMap call. The line shows the export name, the path, the offset and the size. | `DiscMapExports.cs` |
| `SHARPEMU_LOG_DISCORD` | `1` | Writes `[DISCORD]` messages from the rich presence client of the GUI to stderr. The default is off. | `DiscordRichPresence.cs` |
| `SHARPEMU_LOG_DLSYM` | `1` | Writes a trace line for each successful `sceKernelDlsym` call. The line shows the module handle, the symbol name and the resolved address. | `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_EQUEUE` | `1` | Writes trace lines for kernel event queue operations. The emulator reads the value one time at start. | `KernelEventQueueCompatExports.cs` |
| `SHARPEMU_LOG_EVENT_FLAG` | `1` | Writes trace lines for kernel event flag operations. The emulator reads the value one time at start. | `KernelEventFlagCompatExports.cs` |
| `SHARPEMU_LOG_EXPECTED_IMPORT_RESULTS` | `1` | Shows import error results that the emulator knows as usual, for example file-not-found probes, timeouts and busy trylocks. It shows the first 8 results for each NID and result, and then every 10000th. The default hides these results. | `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_FIBER` | `1` | Writes `fiber` trace lines for the Fiber library calls. It also writes a line for each fiber context transfer in the CPU backend. | `FiberExports.cs`, `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_FILE` | path | Writes all log entries to this file with timestamps and appends to an existing file. The file receives every log level. `SHARPEMU_LOG_LEVEL` then limits only the console output. | `SharpEmuLog.cs` |
| `SHARPEMU_LOG_FMOD` | `1` | Writes a trace line for the FMOD `system_set_output` compatibility call. The line shows the system address, the output type and the call count. | `FmodCompatExports.cs` |
| `SHARPEMU_LOG_GUARDS` | `1` | Writes a trace line for each C++ static guard operation. The line shows the guard address, the guard word, the state flags and the owner thread. | `CxxAbiExports.cs` |
| `SHARPEMU_LOG_GUEST_EXCEPTIONS` | `1` | Writes `guest_exception` trace lines when the emulator raises and delivers a guest exception. The lines show the target thread and the exception type. | `DirectExecutionBackend.cs`, `KernelExceptionCompatExports.cs` |
| `SHARPEMU_LOG_GUEST_THREADS` | `1` | Writes guest thread scheduler messages to stderr. The messages show thread state changes, yields, wakes, exits, nested callbacks and affinity failures. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs`, `KernelRuntimeCompatExports.cs` |
| `SHARPEMU_LOG_GUEST_THREAD_SNAPSHOTS` | `1` | Writes one snapshot line for each guest thread every second. The line shows the state, the import count, the last NID, the last return address and the block reason. The ready-dispatch thread then wakes every second. | `DirectExecutionBackend.cs` |
| `SHARPEMU_LOG_HTTP2` | `1` | Writes an `http2` trace line for each Http2 library call. The line shows the operation, the id and four arguments. | `Http2Exports.cs` |
| `SHARPEMU_LOG_HTTP` | `1` | Writes an `http` trace line for each Http library call. The line shows the operation, the id and four arguments. | `HttpExports.cs` |
| `SHARPEMU_LOG_IO_FILTER` | text | Limits the `SHARPEMU_LOG_IO` output to paths that contain this text. The comparison ignores case. When unset, the emulator shows all paths. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_LOG_JSON` | `1` | Writes a `json` trace line for each Json library call. Text values show a maximum of 128 characters. | `JsonExports.cs` |
| `SHARPEMU_LOG_LAZY_COMMIT` | `1` | Writes a trace line for every lazy memory commit fault. The default shows only the first 16 faults and then every 256th fault. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_LOG_LEVEL` | text | Sets the minimum log level. The values are `Trace`, `Debug`, `Info`, `Warning` or `Warn`, `Error`, `Critical` or `Fatal`, and `None`. The default is `Info`, and the comparison ignores case. | `SharpEmuLog.cs` |
| `SHARPEMU_LOG_LIBC_ALLOC` | `1` | Writes a trace line for each guest libc allocation call. The line shows the size, the alignment, the result address and the return address of the caller. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_LOG_NET` | `1` | Writes `net` trace lines for Net library calls and debug lines for kernel socket operations. It also writes each payload that the guest sends on a connected socket to stdout as text. | `NetExports.cs`, `KernelSocketCompatExports.cs` |
| `SHARPEMU_LOG_NGS2` | `1` | Writes trace lines for Ngs2 audio library calls. The default is off. | `Ngs2Exports.cs` |
| `SHARPEMU_LOG_NO_COLOR` | `1` | Stops the use of colors in the console log. The values `1`, `true`, `yes` and `on` are accepted. The console log has no colors when the output is redirected. | `SharpEmuLog.cs` |
| `SHARPEMU_LOG_NP_WEB_API2` | `1` | Writes an `npwebapi2` trace line for each NpWebApi2 call. The line shows the operation, the id, the first argument and the initialized state. | `NpWebApi2Exports.cs` |
| `SHARPEMU_LOG_OPEN` | `1` | Writes trace lines for guest file open operations to stderr. The default is off. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_LOG_PLAYGO` | `1` | Writes `playgo` trace lines for PlayGo calls. For single-entry locus queries it shows the first 32 calls and then every 1000th call. | `PlayGoExports.cs` |
| `SHARPEMU_LOG_POINTER_WINDOW_SIZE` | hex number | Sets the byte size of each memory window that `SHARPEMU_LOG_POINTER_WINDOWS` shows after a guest fault. The value is hexadecimal, with or without `0x`. The default is 0x80. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_LOG_POSIX_SIGNALS` | `1` | Writes a trace line for every POSIX signal that the fault handler receives. When unset, the log shows only the first 16 signals and then every 1024th signal. | `DirectExecutionBackend.PosixSignals.cs` |
| `SHARPEMU_LOG_PROC_PARAM_PTRS` | `1` | Shows the pointer fields in the process parameter block. This dump is part of the `SHARPEMU_LOG_PROC_PARAM` trace, so set that variable also. | `KernelRuntimeCompatExports.cs` |
| `SHARPEMU_LOG_PROC_PARAM` | `1` | Shows the address and the contents of the process parameter block of the guest program. The default is off. | `KernelRuntimeCompatExports.cs` |
| `SHARPEMU_LOG_PS5_USER_SLOTS` | `1` | Adds four 0x60-byte memory dumps to the IL2CPP exception diagnostic. The dumps start at the fixed guest address 0x801A73110 and have a step of 0x51C8 bytes. | `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_PSML` | `1` | Writes `psml.` trace lines for the PSML library calls to stderr. The default is off. | `PsmlExports.cs` |
| `SHARPEMU_LOG_PTHREADS` | `1` | Writes trace lines for the pthread calls of the guest. It also starts the condition variable trace of `SHARPEMU_LOG_PTHREAD_CONDS`. The default is off. | `KernelPthreadCompatExports.cs`, `KernelExports.cs` |
| `SHARPEMU_LOG_PTHREAD_CONDS` | `1` | Writes a `pthread_cond_` trace line for each condition variable operation. The line shows the waiter count, the signal epoch, and the result. | `KernelPthreadCompatExports.cs` |
| `SHARPEMU_LOG_PTHREAD_FASTPATH` | `1` | Writes trace lines for the mutex fast path. It shows the first 16 fast-path unlocks with the mutex object words. It shows one busy result for each mutex address. | `KernelPthreadCompatExports.cs` |
| `SHARPEMU_LOG_PTHREAD_MUTEX_FILTER` | list of hex addresses | Limits the mutex trace to the given mutex addresses. Use a comma, a semicolon, or a space between the hexadecimal addresses. The listed mutexes are in the trace even if `SHARPEMU_LOG_PTHREADS` is not set. | `KernelPthreadCompatExports.cs` |
| `SHARPEMU_LOG_REFSCAN_ADDRS` | list of hex addresses | After a guest fault, scans executable guest memory from 0x800000000 to 0x810000000 for instructions that refer to the given addresses. It shows a maximum of 24 hits for each address. Use a comma between the hexadecimal addresses. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_LOG_REGISTER_WINDOWS` | `1` | After a guest fault, shows a 0x80-byte memory window at each general register value that is 0x10000 or more. The default is off. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_LOG_SAVEDATA` | `1` | Writes `savedata.` and `save_data_dialog.` trace lines for the save data calls to stderr. The default is off. | `SaveDataExports.cs`, `SaveDataDialogExports.cs` |
| `SHARPEMU_LOG_SEMA` | `1` | Writes trace lines for the kernel semaphore operations. The emulator reads the value one time at start. The default is off. | `KernelSemaphoreCompatExports.cs` |
| `SHARPEMU_LOG_SHARE` | `1` | Writes `share.` trace lines for the Share library calls to stderr. The default is off. | `ShareExports.cs` |
| `SHARPEMU_LOG_SSL` | `1` | Writes an `ssl.` trace line for each SSL library call. The line shows the operation, the identifier, and the first argument. | `SslExports.cs` |
| `SHARPEMU_LOG_STACK_CHK` | `1` | Writes diagnostic lines when the guest calls the stack check failure import. The lines show the return address, the guard registers, and the stack slots from rbp-0x10 to rbp-0x80. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_STDIO` | `1` | Writes trace lines for the libc stdio calls of the guest. The emulator reads the value one time at start. The default is off. | `LibcStdioExports.cs` |
| `SHARPEMU_LOG_STRLEN_BURSTS` | `1` | Writes a warning when the guest calls `strlen` 24 times in sequence. The warning shows the return address and the last five different imports before the burst. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_STRLEN` | `1` | Keeps `strlen` calls in the import trace. When unset, the import trace does not show `strlen` calls. It also starts the burst detection of `SHARPEMU_LOG_STRLEN_BURSTS`. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_THREAD_MODE` | `1` | Writes `[THREADMODE]` lines when a native guest worker thread is created, starts a guest run, and stops a guest run. Each line shows the host thread identifiers. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.NativeWorker.cs` |
| `SHARPEMU_LOG_USER_SERVICE` | `1` | Writes trace lines for the user service library calls. Each line includes the guest return address of the call. | `UserServiceExports.cs` |
| `SHARPEMU_LOG_USLEEP` | `1` | Writes trace lines for `usleep` calls: the first 32 calls and then every 10000th call. It also stops the native inline `usleep` stub and the leaf import path, so each call goes through the traced handler. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs`, `KernelRuntimeCompatExports.cs` |
| `SHARPEMU_LOG_VIDEOOUT_FPS` | `1` | Writes periodic frame rate lines with flip, present, draw, pipeline, and buffer pool counts. `SHARPEMU_PROFILE_PERFORMANCE=1` starts the same output. | `VideoOutExports.cs` |
| `SHARPEMU_LOG_VIDEOOUT` | `1` | Writes `videoout.` trace lines for the video output calls, such as flip events and frame dumps. The default is off. | `VideoOutExports.cs` |
| `SHARPEMU_LOG_VK_RESOURCES` | `1` | Writes a `vk.global_buffer` trace line one time for each new guest buffer that the Vulkan presenter binds. The line shows the base address and the size. | `VulkanVideoPresenter.Draws.Recording.cs`, `VulkanVideoPresenter.GuestBuffers.cs` |
| `SHARPEMU_LOG_VMEM` | `1` | Writes debug lines for guest virtual memory operations and `[HOSTMEM]` lines for host memory operations. The default is off. | `PhysicalVirtualMemory.cs`, `HostMemory.cs`, `PosixHostMemory.cs` |
| `SHARPEMU_LOG_WIDE_PRINTF_ARGS` | `1` | Writes trace lines for the arguments of printf format calls, such as string arguments. Use `SHARPEMU_LOG_WIDE_PRINTF_FILTER` to limit the output. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_LOG_WIDE_PRINTF_FILTER` | text | Limits the `SHARPEMU_LOG_WIDE_PRINTF_ARGS` trace to format strings that contain this text. The comparison is case-sensitive. When unset, the trace includes all format strings. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_LOG_WIDE_PRINTF` | `1` | Writes a trace line for each wide-character printf call. The line shows the first 160 characters of the format and of the result. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_LOG_WIDE` | `1` | Shows the first 32 bytes at the string address for each `wcslen` call. The default is off. | `KernelMemoryCompatExports.cs` |
| `SHARPEMU_PERIODIC_SNAPSHOT_SECONDS` | number (seconds) | Set a number of seconds to write the stall watchdog snapshot at that interval. The default is 0, which writes no periodic snapshot. The stall watchdog must be on. | `DirectExecutionBackend.cs` |

## Traces

Each variable records a detailed sequence of events. Traces can be large.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_LOG_IMPORT_FILTER` | text | Writes a trace line for each import that contains this text. The emulator compares the text with the library name, the export name and the NID, and ignores case. When unset, the filter selects no imports. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_IMPORT_FRAMES` | `1` | Adds the guest frame chain to each traced import. It applies only to imports that `SHARPEMU_LOG_ALL_IMPORTS` or `SHARPEMU_LOG_IMPORT_FILTER` selects. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_IMPORT_PERIODIC` | `1` | Starts the periodic import trace. It shows the early import bands and every 100000th import dispatch. The default is off because the stderr output decreases the speed. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_IMPORT_RECENT` | `1` | Writes the recent import history after each traced import. It applies only to imports that `SHARPEMU_LOG_ALL_IMPORTS` or `SHARPEMU_LOG_IMPORT_FILTER` selects. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_PROBE_IMPORT_RET_ADDRESS` | hex address | Set a guest return address. When an import call returns to this address, the emulator writes an `import-return-address-probe` line with the thread, NID, stack and frame values. The limit is 2048 lines. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_PROBE_IMPORT_RET` | text (NID or `*`) | Set an import NID, or `*` for all imports. For the first 8 applicable import calls, the emulator writes the bytes and the disassembly before the return address. The default is off. | `DirectExecutionBackend.cs`, `DirectExecutionBackend.Imports.cs`, `DirectExecutionBackend.Diagnostics.cs` |
| `SHARPEMU_RTC_PROBE_RANGE` | hex address range (`start-end`) | Set a range of guest addresses. When a caller of `sceRtcGetCurrentTick` returns into this range, the emulator writes 0x100 bytes of code near that return address. It does this one time only. | `RtcExports.cs` |
| `SHARPEMU_TRACE_AGC_EQ_ACCESSORS` | `1` | Set to `1` to write an `agc.eq_accessor` line for calls to the AGC event accessor functions. Each line shows the accessor, the event address, the result and the event bytes. After 64 lines, the emulator writes only at power-of-two counts. | `AgcExports.Events.cs` |
| `SHARPEMU_TRACE_FOCUSED_CONTINUATION` | `1` | Set to `1` to write `focused_continuation` lines for guest thread continuations. The emulator writes a line only when the stack pointer is in the range 0x6FFFAC000000 to 0x6FFFAC200000. | `DirectExecutionBackend.cs` |
| `SHARPEMU_TRACE_FRAME_PACKETS` | `1` | Writes a `[FRAMEPKT]` line at a flip with the draw count and the dispatch count of the frame. It shows the first 8 flips, each 60th flip, and each flip that has no draws. Default is off. | `AgcExports.CommandStream.cs` |
| `SHARPEMU_TRACE_GPU_MEMORY_ADDRESS` | hex address or `auto` | Writes `[GPU][MEMORY_TRACE]` lines for GPU memory events that touch the page of the given address. The value `auto` selects the page of the first device-address fault. Default is off. | `GuestGpuMemoryHook.cs` |
| `SHARPEMU_TRACE_GUEST_IMAGES` | `1`, `present` or `every:N[@M]` | `1` and `present` write Vulkan present trace lines (`vk.present_taken`, `vk.present_dropped`, `vk.present_sample`) and up to 64 Metal `agc.texture_fallback` lines. The code reads the `every:N[@M]` form, but no code uses the result. Default is off. | `VulkanVideoPresenter.Draws.Recording.cs`, `VulkanVideoPresenter.Present.cs`, `MetalTextureSnapshots.cs` |
| `SHARPEMU_TRACE_GUEST_IMAGE_ADDRS` | list of hex addresses or `*` | The guest image write tracker writes `[WT][LIFETIME]` lines for each tracked range that contains a listed address. `*` selects all ranges. The Vulkan presenter also reads the list, but no code uses that filter. | `GuestImageWriteTracker.cs`, `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_TRACE_GUEST_IMAGE_FORMAT` | text (Vulkan format name) | Effect not clear from the code. The Vulkan presenter keeps the name for an image filter that compares the image format, but no code calls the filter. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_TRACE_GUEST_IMAGE_HEIGHT` | number | Effect not clear from the code. The Vulkan presenter keeps the number for an image filter that compares the image height, but no code calls the filter. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_TRACE_GUEST_IMAGE_WIDTH` | number | Effect not clear from the code. The Vulkan presenter keeps the number for an image filter that compares the image width, but no code calls the filter. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_TRACE_GUEST_MEMORY_LIFETIME` | list of source names or `*` | The guest image write tracker writes `[WT][LIFETIME]` lines for each tracked range that has a listed source name. Use a comma or a semicolon between names. `*` selects all sources. | `GuestImageWriteTracker.cs` |
| `SHARPEMU_TRACE_GUEST_WRITES` | list of hex addresses or `*` | Effect not clear from the code. The Vulkan presenter reads the list in an address filter for guest image writes, but no code calls the filter. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_TRACE_PACKED_EXPORT` | `1` | The SPIR-V translator writes `[AGC][PACKED-EXPORT]` and `[AGC][TITLE-IR]` lines for packed export analysis. It does this only for the shader program at address 0x0000000500781200. Default is off. | `Gen5SpirvTranslator.cs` |
| `SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_ADDRS` | list of hex addresses or `*` | Writes a `vk.present_sample` trace line when the presenter shows a guest image at a listed address. It records the first presentation of each address unless `SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_OCCURRENCE` is set. | `VulkanVideoPresenter.Draws.Recording.cs`, `VulkanVideoPresenter.RenderLoop.cs` |
| `SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_OCCURRENCE` | number | Selects which presentation of an address in `SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_ADDRS` the presenter records. Default is 0, which selects the first presentation. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_TRACE_TITLE_INTERFACE` | `1` | The SPIR-V translator writes `[AGC][TITLE-INTERFACE]` lines with the export and interpolation instructions of a shader. It does this only for the programs at addresses 0x0000000500780000 and 0x0000000500781200. | `Gen5SpirvTranslator.cs` |

## Data dumps

Each variable writes data to files.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_DUMP_FAULT_STACK_WINDOW` | `1` | Writes the full stack window from RSP-0x300 to RSP+0x100 to the error output when a guest fault report occurs. The default writes only the short stack list. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT` | path | The Vulkan presenter reads the value into a field. No code uses the field, so the variable has no effect. | `VulkanVideoPresenter.Draws.Recording.cs` |
| `SHARPEMU_DUMP_SPIRV_ADDRESS` | hex address | Limits the shader dump of `SHARPEMU_DUMP_SPIRV=1` to the shader at this guest address. The default writes all shaders. | `ShaderProgramCache.cs` |
| `SHARPEMU_DUMP_VIDEOOUT` | `1` | Writes submitted VideoOut frame buffers as BMP files with a text metadata file. It writes a maximum of 8 frames and skips a frame that did not change. | `VideoOutExports.cs`, `VideoOutExports.FlipRequests.cs` |
| `SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS` | `1` | Writes a swapchain dump for each presented frame, not only for the first one. It needs `SHARPEMU_GUEST_IMAGE_DUMP_DIR` and an active presented-image trace. Each dump waits for the GPU, so the frame rate decreases. | `VulkanVideoPresenter.Present.Frames.cs` |
| `SHARPEMU_GUEST_IMAGE_DUMP_DIR` | path | Sets the directory for raw swapchain dumps. The presenter writes `present-NNNN-WxH-format.bgra` files there when `SHARPEMU_TRACE_GUEST_IMAGES` is `1` or `present`. The presenter makes the directory if it is absent. | `VulkanVideoPresenter.Present.Frames.cs` |
| `SHARPEMU_LOG_DISASM_ADDRS` | list of hex addresses | Adds more addresses to the fault disassembly dump. Use a comma between the addresses. The emulator uses this list only when `SHARPEMU_LOG_DISASM` is `1`, and it shows 48 instructions for each address. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_LOG_DISASM` | `1` | Writes guest disassembly to stderr when a guest fault, an abort or an illegal instruction occurs. It shows the code before the fault address, before the stack return address and before three frame return addresses. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_LOG_IL2CPP_EXCEPTION` | `1` | Writes a diagnostic dump when the guest calls the unresolved import with NID `cfwBSQyr5Ys`. The dump shows the registers, the code pointers on the stack and the frame pointer chain. The emulator writes a maximum of 4 dumps. | `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_LOG_POINTER_WINDOWS` | list of hex addresses | Writes a memory window for each address when a guest access fault occurs. Use a comma between the addresses. The window size is 0x80 bytes unless `SHARPEMU_LOG_POINTER_WINDOW_SIZE` sets a different size. | `DirectExecutionBackend.Exceptions.cs` |
| `SHARPEMU_SHADER_SPIRV_DUMP_DIR` | path | Set a directory for shader dumps. When set, the Vulkan presenter writes each SPIR-V module to a numbered `.spv` file. The `SHARPEMU_DUMP_SPIRV=1` dumps also use this directory. Their default is `shader-dumps` next to the executable. | `VulkanVideoPresenter.Pipelines.cs`, `ShaderProgramCache.cs`, `VulkanVideoPresenter.Device.Setup.cs` |
| `SHARPEMU_SWAPCHAIN_DUMP_EVERY` | number (frames) | Set a number N to trace the presented swapchain image at each Nth present. This operates only when `SHARPEMU_TRACE_GUEST_IMAGES` is `1` or `present`. The default is 0, which traces only the first presented image. | `VulkanVideoPresenter.Present.Frames.cs`, `VulkanVideoPresenter.Draws.Recording.cs` |

## Performance measurement

Each variable records timings or counts.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_LOG_AUDIO_QUEUE` | `1` | Writes one `[PERF][AUDIO]` line per second for each SDL audio stream. The line shows the queue depth in milliseconds, the submit rate, the fill percentage and the drops. The emulator reads the value one time at start. | `SdlHostAudio.cs` |
| `SHARPEMU_PERF_HLE_NODICT` | `1` | Use with `SHARPEMU_PERF_HLE=1`. Set to `1` to stop the call count dictionary and the top call count report. The dispatch time report stays on. | `DirectExecutionBackend.Diagnostics.cs` |
| `SHARPEMU_PERF_HLE` | `1` | Set to `1` to measure the time of each HLE import dispatch. After each 500000 calls, the emulator writes `[PERF][HLE]` lines with the call counts and the cost of the top exports. The default is off. | `DirectExecutionBackend.Diagnostics.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_PERF_MEM` | `1` | Set to `1` to count the POSIX fault signals that the signal handler receives. The emulator writes a `[PERF][MEM] posix_faults` line after each 100000 signals. This has an effect only on POSIX hosts. | `DirectExecutionBackend.PosixSignals.cs` |
| `SHARPEMU_PROFILE_DCB_PARSE` | `1` | Set to `1` to measure the time to parse draw command buffers. The emulator writes `[PERF][DCB_PARSE]` lines. `SHARPEMU_PROFILE_PERFORMANCE=1` also starts this profile. | `DcbParseProfile.cs` |
| `SHARPEMU_PROFILE_DCB_SUBMISSION` | `1` | Set to `1` to measure command buffer submission and geometry snapshot work. The emulator writes `[PERF][DCB_SUBMIT]`, `[PERF][VERTEX_SNAPSHOT]` and `[PERF][SNAPSHOT_PREPASS]` lines. `SHARPEMU_PROFILE_PERFORMANCE=1` also starts this profile. | `DcbSubmissionProfile.cs` |
| `SHARPEMU_PROFILE_GUEST_IMAGE_TRACKER` | `1` | Set to `1` to record counters for the guest image write tracker. The emulator writes `[PERF][GUEST_IMAGE_TRACKER]` and `[PERF][GUEST_IMAGE_TRACKER_RANGE]` lines. `SHARPEMU_PROFILE_PERFORMANCE=1` also starts this profile. | `GuestImageWriteTracker.cs` |
| `SHARPEMU_PROFILE_GUEST_RIP_INTERVAL_MS` | number (milliseconds) | Set the time between samples of the guest code profiler. The value must be more than 0. The default is 2. | `DirectExecutionBackend.GuestSampler.cs` |
| `SHARPEMU_PROFILE_GUEST_RIP_REPORT_S` | number (seconds) | Set the time between reports of the guest code profiler. The value must be more than 0. The default is 15. | `DirectExecutionBackend.GuestSampler.cs` |
| `SHARPEMU_PROFILE_GUEST_RIP_THREAD` | text | Set a text filter for the guest code profiler. The profiler samples only guest threads whose name contains this text. The comparison ignores case. When unset, the profiler samples all guest threads. | `DirectExecutionBackend.GuestSampler.cs` |
| `SHARPEMU_PROFILE_GUEST_RIP` | `1` | Set to `1` to start a sample profiler for guest code. The profiler records the instruction address of each guest thread and writes `[PERF][GUEST]` reports. The default is off. | `DirectExecutionBackend.GuestSampler.cs`, `DirectExecutionBackend.Imports.cs` |
| `SHARPEMU_PROFILE_GUEST_SCHEDULER` | `1` | Set to `1` to record guest thread scheduler statistics. The emulator writes `[PERF][GUEST_SCHED]` lines. With `SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE=1`, it also records guest thread flow events. `SHARPEMU_PROFILE_PERFORMANCE=1` also starts this profile. | `DirectExecutionBackend.GuestSchedulerProfile.cs`, `GuestThreadFlowProfile.cs` |
| `SHARPEMU_PROFILE_RENDER_REPORT_S` | number (seconds) | Set the time between render profile reports. The value can be a decimal number and must be more than 0. The default is 5. | `RenderPhaseProfile.cs` |
| `SHARPEMU_PROFILE_RENDER` | `1` | Set to `1` to measure the time of each render thread phase. The emulator writes `[PERF][RENDER]`, `[PERF][RENDER_MS]` and related lines. With `SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE=1`, it also records GPU memory access counters. `SHARPEMU_PROFILE_PERFORMANCE=1` also starts this profile. | `RenderPhaseProfile.cs`, `GpuMemoryAccessProfile.cs` |
| `SHARPEMU_PROFILE_SYNC_ON_ADDRESS` | `1` | Set to `1` to start the detailed sync-on-address profile. The emulator also records the guest call stack for each wait and wake call. It writes `[PERF][SYNC_ADDR]` and `[PERF][SYNC_THREAD]` lines. `SHARPEMU_PROFILE_PERFORMANCE=1` starts the profile without the call stack. | `KernelSyncOnAddressProfile.cs`, `KernelSyncOnAddressCompatExports.cs` |
| `SHARPEMU_PROFILE_TEXTURE_PREPARATION` | `1` | Set to `1` to measure texture preparation work. The emulator writes `[PERF][TEXTURE_PREP]` lines. `SHARPEMU_PROFILE_PERFORMANCE=1` also starts this profile. | `TexturePreparationProfile.cs` |

## Internal

The emulator sets these variables for its own processes. Do not set them manually.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_MITIGATED_CHILD` | `1` | The Windows launcher sets this variable to 1 when it starts the child process with CFG and CET mitigations off. The child accepts the `--sharpemu-mitigated-child` argument only when the value is 1. Do not set it manually. | `Program.cs`, `EmulatorProcess.cs` |

## Tests and tools

Only the tests, the scripts or the build tools read these variables.

| Variable | Value | Function | Code that reads it |
| --- | --- | --- | --- |
| `SHARPEMU_BACKING_VIEW_FAULT_WORKER` | `1` | The backing view fault test sets this for its child process. The value `1` tells the child to do the fault sequence. Do not set it manually. | `BackingViewFaultTests.cs`, `GuestFaultWorker.cs` |
| `SHARPEMU_BUFFER_FAULT_WORKER` | `1` | The guest buffer fault test sets this for its child process. The value `1` tells the child to do the fault sequence. Do not set it manually. | `GuestBufferFaultTests.cs`, `GuestFaultWorker.cs` |
| `SHARPEMU_FAULT_WORKER_REPORT` | path | The fault test parent sets this to a temporary file path for its child process. The child writes its result (`completed` or `skipped`) to this file. When unset, the child writes the result to the error stream. | `GuestFaultWorker.cs` |
| `SHARPEMU_IMAGE_FAULT_WORKER` | `1` | The guest image fault test sets this for its child process. The value `1` tells the child to do the fault sequence. Do not set it manually. | `GuestImageFaultTests.cs`, `GuestFaultWorker.cs` |
| `SHARPEMU_NATIVE_RETURN_SMOKE_WORKER` | `1` | The native return smoke test sets this for its child process. The value `1` tells the child to run the synthetic guest entry. Do not set it manually. | `Gen5NativeReturnSmokeTests.cs` |
| `SHARPEMU_SOCKET_CONNECTION_TEST_WORKER` | `1` | The kernel socket connection test sets this for its child process. The value `1` tells the child to do the connect sequence. Do not set it manually. | `KernelSocketConnectionTests.cs` |
| `SHARPEMU_TEST_REFERENCE_SPIRV_DIR` | path | Sets the directory that contains the reference compiled shader files for the GPU image tests. The tests make sure that each file has the recorded SHA-256 hash. When unset, these tests skip the reference comparison. | `ImageTestHarness.cs` |
| `SHARPEMU_TEST_REQUIRE_DEVICE` | `1` | Makes a GPU test fail when a Vulkan or Metal device or a device feature is missing. Default is to skip the test. | `ImageTestHarness.cs`, `MetalRuntimeTests.cs` |
| `SHARPEMU_TEST_REQUIRE_REFERENCE_SPIRV` | `1` | Makes a test fail when a reference shader file, its directory, or a necessary device is missing. Default is to skip the test. | `ImageTestHarness.cs`, `GuestFaultWorker.cs` |
| `SHARPEMU_TEST_VK_VALIDATION` | `1` | Loads the Vulkan validation layer in the headless test device and prints the validation messages. Default is off. | `HeadlessVulkan.cs` |
| `SHARPEMU_THREAD_CONTEXT_TEST_WORKER` | `1` | The Windows thread context capture test sets this for its child process. The value `1` tells the child to do the capture sequence. Do not set it manually. | `WindowsThreadContextCaptureTests.cs`, `GuestFaultWorker.cs` |
| `SHARPEMU_UPDATE_GOLDENS` | `1` | Makes the Metal golden tests write the generated shader text to the golden files in the source tree. Default is to compare the generated text with the golden files. | `MslGoldenTests.cs` |

## Variables that the code reads but does not use

The code reads these variables into a field or a filter. No other code uses that field or filter.
Thus they have no effect, or only a part of their function operates. The rows above give the details.

- `SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT`
- `SHARPEMU_ENABLE_CHUNKED_DRAWS`
- `SHARPEMU_FORCE_ATTRIBUTE_FRAGMENT`
- `SHARPEMU_FORCE_DEFAULT_RASTER_STATE`
- `SHARPEMU_FORCE_FULLSCREEN_PIPELINE`
- `SHARPEMU_FORCE_FULLSCREEN_VERTEX`
- `SHARPEMU_FORCE_SOLID_FRAGMENT`
- `SHARPEMU_FORCE_TITLE_DEFAULT_RASTER_STATE`
- `SHARPEMU_FORCE_TITLE_FULLSCREEN_VERTEX`
- `SHARPEMU_FORCE_TITLE_SOLID_FRAGMENT`
- `SHARPEMU_SKIP_TALL_COMPUTE_Z`
- `SHARPEMU_TRACE_GUEST_IMAGE_FORMAT`
- `SHARPEMU_TRACE_GUEST_IMAGE_HEIGHT`
- `SHARPEMU_TRACE_GUEST_IMAGE_WIDTH`
- `SHARPEMU_TRACE_GUEST_WRITES`
