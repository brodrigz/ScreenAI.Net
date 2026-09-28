# Native interop contract

The platform-neutral engine delegates to an internal native-runtime abstraction. Windows x64 is implemented. Linux and macOS have
separate `libchromescreenai.so` placeholders that deliberately throw `NotImplementedException` until their loading, dependencies,
ABI and component discovery have been validated on those platforms.

The Windows baseline bitmap layout comes from clv-locro's component 140.20 investigation;
that reference alone does not establish compatibility with other versions. ScreenAI.Net must validate each supported component.

## Initialization

1. Validate platform/architecture and the component's OCR asset inventory.
2. Load the DLL by absolute path using `LoadLibraryExW`, restricting dependency search to that directory and System32.
3. Read its version, enforce the compatibility allowlist, and bind exported C-declared functions.
4. Keep the runtime and callback delegates rooted for process lifetime.
5. Register file-size/file-content callbacks, select light mode, initialize OCR, and query the maximum image dimension.

Callbacks catch managed exceptions before returning to native code. Relative paths must remain inside the component directory;
the directory itself must be trusted, including any filesystem links. Read-only model file handles are cached because native
initialization/recognition may request the same data repeatedly. Copies use a pooled 64 KiB scratch buffer, not whole-file managed
arrays. Missing optional size probes return zero; content buffers may be larger than the file but must not be smaller. Listed
mandatory assets are still validated before native initialization. No exception can make a broken native component safe to execute.

## Bitmap ABI

The 56-byte bitmap contains these x64 fields:

| Offset | Field |
| --- | --- |
| 0 | synthetic pixel-reference pointer |
| 8 | BGRA pixel pointer |
| 16 | row stride, 64-bit |
| 24 | color-space pointer, null |
| 32 | color type, 6 (BGRA8888) |
| 36 | alpha type, 2 (premultiplied) |
| 40 | width, 32-bit |
| 44 | height, 32-bit |
| 48 | flags, zero |

The synthetic pixel reference is 104 bytes with a non-null pointer to a zeroed virtual-method table, reference count 1,
dimensions, pixels, and row stride. This is NOT a general-purpose C++ SkBitmap/SkPixelRef implementation.
The reference's OCR path does not invoke those virtual methods; a changed native implementation could crash immediately.
Never assume a SkiaSharp handle has this ABI, nor mark a version compatible just because its exports load.

Caller buffers with padded rows are normalized to tightly packed BGRA before native invocation because the tested 153.x OCR path
did not honor a wider advertised row stride consistently. Each call owns unmanaged image/bitmap buffers until synchronous `PerformOCR` returns. The result is copied to managed memory and
released with `FreeLibraryAllocatedCharArray` in a finally block, never with `FreeHGlobal`. Protobuf decoding uses Google.Protobuf's
wire reader, projects the fields we expose, and skips unknown fields. Empty valid annotations are distinct from null native results.

## Operational boundaries

- No supported native shutdown exists. The runtime, module, callbacks and model handles stay loaded until process exit.
- Concurrent native OCR calls have not been established safe; the wrapper serializes them.
- Changing component directory within a native process is rejected. Light-mode changes run under the same serialization lock and
  reinitialize the native pipeline; a failed reinitialization poisons that process until exit.
- The DLL is executable code supplied by the caller; path validation is not a sandbox or authenticity check.
- `ScreenAiWorker` owns a lazy child host with current-user named-pipe IPC. It serializes requests, supports bounded admission,
  kills the child on active cancellation/timeout/disposal, shuts down idle children, and restarts on a subsequent request.
- `ScreenAiWorkerPool` leases independent workers for real parallel native OCR; it never invokes the native singleton concurrently.
- Startup/request deadlines are separate, recovery has capped exponential backoff, and failed documents are not replayed.
- The host retains its parent process handle and checks for parent exit once a second. Termination of the child is the native cleanup
  mechanism; neither parent nor child calls `FreeLibrary` after initialization.
- Enabling an unverified version is explicit and dangerous; use a disposable process and synthetic input first.
- Keep the DLL and model files together. Do not mix versions or automatically follow Chrome component updates.

## Validation record

See the repository README for the repeatable synthetic smoke command. A smoke pass establishes the tested calling path, not universal
accuracy or a supported Google API.

| Date | OS/runtime | Component | Mode | Result |
| --- | --- | --- | --- | --- |
| 2026-09-28 | Windows x64 / .NET 8 | 153.2 | Standard | Passed complete synthetic smoke |
| 2026-09-28 | Windows x64 / .NET 8 | 153.3 | Standard | Passed complete synthetic smoke |
| 2026-09-28 | Windows x64 / .NET 8 | 153.3 | Light | Passed complete synthetic smoke |

The worker smoke additionally covers disposal isolation, parallel processes, resize coordinate mapping, live mode switching,
crash/idle restart, cancellation, startup/execution deadlines, pending-work disposal, and orphan cleanup. These are functional
checks, not sustained-load/memory benchmarks or a proof of compatibility with other component versions.

## Chromium references informing lifecycle design

- [Screen AI service implementation](https://chromium.googlesource.com/chromium/src/+/main/services/screen_ai/screen_ai_service_impl.cc):
  process-based idle/watchdog shutdown and per-request mode selection.
- [ModelDataHolder in the service](https://chromium.googlesource.com/chromium/src/+/main/services/screen_ai/screen_ai_service_impl.cc):
  file-backed model callbacks and destination-buffer bounds.
- [Service handler](https://chromium.googlesource.com/chromium/src/+/main/chrome/browser/screen_ai/screen_ai_service_handler_base.cc):
  service launch, crash handling, and restart backoff.

ScreenAI.Net adopts those principles, not Chromium's exact service implementation or timing defaults. Its host runs as the caller's
OS account without Chromium's utility-process sandbox. The native bitmap ABI remains unofficial and version-specific.
