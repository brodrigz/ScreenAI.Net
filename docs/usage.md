# Usage guide

Detailed reference for ScreenAI.Net. For the short version, see the [README](../README.md).

## Scope

- Input: a top-down BGRA32 pixel buffer with premultiplied alpha (use alpha 255 for opaque images).
- Output: text lines, words, symbols, bounding boxes, language, direction, content type, and available confidence values.
- Not included: image decoding in the core library, PDF rendering, searchable-PDF generation, table reconstruction, or LLM integration.
- The library target and public API are portable. Windows x64 is currently implemented; Linux and macOS runtime boundaries are
  scaffolded for `libchromescreenai.so` and currently throw `NotImplementedException`.
- .NET Framework does not support .NET Standard 2.1. Modern .NET applications can reference the library.

## Component setup

Supply a trusted Screen AI component directory containing the DLL, `files_list_ocr.txt`, and all listed OCR assets, from the same version.
A Chrome installation commonly stores these under:

```text
%LOCALAPPDATA%\Google\Chrome\User Data\screen_ai\<version>\
```

Use an explicit versioned path; the wrapper never selects the newest version automatically. A service account may not have access
to your interactive user's Chrome profile. Provision an appropriate component location after reviewing the component's terms.
Treat the component directory as executable code, not untrusted user input, and do not modify it while OCR is running.

The initially verified component versions are **153.2** and **153.3**. Other versions are rejected unless the caller explicitly enables
`AllowUnverifiedNativeVersion` for an isolated experiment. This opt-in is not a compatibility guarantee and can crash the process.

## Use: independently owned worker (recommended)

Publish the companion host once and deploy its **entire output directory** alongside your application:

```powershell
dotnet publish src/ScreenAI.Net.Worker -c Release -r win-x64 --self-contained false -o artifacts/worker/win-x64
```

This host requires the .NET 8 x64 runtime (not the Windows desktop runtime). To deploy without an installed runtime, publish with
`--self-contained true`. The client library remains `netstandard2.1`; the host is a separate executable, not a library dependency.
The DLL and its models are supplied separately and are never copied into the host output or NuGet package.

```csharp
using ScreenAI;

var options = new ScreenAiWorkerOptions(componentDirectory, workerExecutablePath)
{
    QueueCapacity = 4,
    StartupTimeout = TimeSpan.FromSeconds(30),
    RequestTimeout = TimeSpan.FromSeconds(30),
    IdleTimeout = TimeSpan.FromMinutes(1)
};

await using var worker = new ScreenAiWorker(options);
var result = await worker.RecognizeBgraAsync(pixels, width, height, stride,
    options: new RecognitionOptions
    {
        OversizedImageBehavior = OversizedImageBehavior.ResizeToFit,
        UseLightMode = false
    },
    cancellationToken: cancellationToken);
Console.WriteLine(result.Text);
```

`WorkerPath` accepts the published `.exe` or `.dll`. A DLL is launched through `DotNetHostPath` (default `dotnet`). Set an explicit
trusted host path for production. Keep the library and worker versions together; their IPC protocol is versioned but internal.

Each worker starts lazily, owns **one independent child process**, and serializes requests within that process. Different worker
instances can use different component directories. Both `Dispose()` and `DisposeAsync()` cancel pending work, terminate that child,
and wait for it to exit, reclaiming its native memory, threads, callbacks, and model handles. Other workers are unaffected.
The host watches its original parent's process handle and exits if that parent dies, even without disposal.

### Concurrency, cancellation, and recovery

```csharp
await using var pool = new ScreenAiWorkerPool(options, workerCount: 2, queueCapacity: 4);
// Concurrent callers share the pool; at most two native OCR requests execute simultaneously.
var result = await pool.RecognizeBgraAsync(pixels, width, height, cancellationToken: cancellationToken);
```

- Queue capacity limits admitted waiting requests, in addition to active workers. Further callers asynchronously wait for capacity;
  admission is not a FIFO guarantee. Pixel buffers are not copied until execution. Callers must retain them, must not modify them
  before completion, and should avoid creating unbounded numbers of pending tasks.
- Cancelling while queued cancels only that request. Cancelling active IPC/native OCR terminates its child. The next request starts
  a fresh one; the cancelled or failed document is **not automatically retried**.
- Startup and request timeouts are separate. Request timeout starts after startup and image preparation; queue wait is excluded.
  A caller cancellation token can bound the whole operation, although managed image preparation is synchronous.
- A native crash/disconnect fails the current request without crashing the caller. Subsequent requests can restart the child, with
  exponential backoff (250 ms initially, capped at 10 seconds by default). Successful OCR resets the failure count.
- Idle workers shut down after `IdleTimeout` and restart on demand. Use `Timeout.InfiniteTimeSpan` to keep them warm.
- Disposal is idempotent, cancels queued/active work, and rejects new calls. If process termination cannot be confirmed within five
  seconds, disposal reports an error instead of claiming resources were released.
- Each worker loads its own native models. Start with a small pool and measure memory/CPU usage before increasing it.

Named pipes restrict access to the current OS user. Process isolation provides fault/lifetime isolation, **not a security sandbox**.
The wrapper does not enable native debug image dumps. Child stdout/stderr are drained into a bounded diagnostic tail, included on
worker errors; treat error diagnostics as potentially sensitive.

### Image sizing and mode selection

Input is top-down premultiplied BGRA32, not encoded PNG/JPEG bytes. Positive padded strides are supported. Input storage is limited
to 128 MiB and dimensions to 32,768 pixels each, independently of the native processing threshold.

`GetMaxImageDimension` is the native downsampling threshold, not a guarantee that larger images are invalid. The wrapper defaults to
rejecting oversized input to avoid hidden scaling. With `ResizeToFit`, it resizes proportionally before OCR and maps line, word,
symbol, and whitespace boxes back to the **original input coordinates**. `Width`/`Height` describe the input; `ProcessedWidth` and
`ProcessedHeight` show the actual OCR dimensions. Integer rounding can introduce small coordinate differences; scaling is not tiling.

`RecognitionOptions.UseLightMode` overrides the worker default per request. Switching modes is serialized and reinitializes the
native pipeline without restarting the worker. Frequent switching can add initialization overhead.

## Advanced: direct in-process use

```csharp
using ScreenAI;

var engine = new ScreenAiEngine(new ScreenAiOptions(componentDirectory));

// pixels: top-down premultiplied BGRA32, row stride in bytes; no encoded PNG/JPEG bytes.
OcrResult result = engine.RecognizeBgra(pixels, width, height, stride);
Console.WriteLine(result.Text);

foreach (var line in result.Lines)
foreach (var word in line.Words)
    Console.WriteLine($"{word.Text}: ({word.Bounds?.X}, {word.Bounds?.Y}), confidence {word.Confidence}");
```

The optional final `RecognitionOptions` parameter provides the same mode/resize choices. Plain text preserves native line order,
not a guaranteed table reading order.

### Lifetime and concurrency

`ScreenAiEngine` remains one shared native runtime per process (use the default assembly load context). Engine instances with the
same component directory share it; each instance has its own default mode. Recognition/mode changes are serialized. Model file
handles stay open, and callbacks copy data through a small pooled buffer instead of retaining whole managed model byte arrays.
Per-call image/result buffers are released after recognition.

There is deliberately **no `Dispose`/native unload API**: the component exposes no supported shutdown, and may retain worker threads
and callbacks. Native initialization failure requires a process restart. Never call this wrapper alongside another independent
wrapper for the same DLL in the same process. Do not load it in a collectible assembly load context.

Direct recognition is synchronous and cannot safely interrupt native inference. Use `ScreenAiWorker` or `ScreenAiWorkerPool` for
disposal, cancellation, isolation, and hard native timeouts. In-process native crashes cannot reliably be turned into managed exceptions.
The wrapper does not redirect process-global logs or enable the native debug-dump mode; the component may write diagnostics to stderr.

## Build and try locally

Requires the .NET 8 SDK for the solution/sample, plus the Windows desktop runtime for sample image decoding.
The packaged library itself targets only `netstandard2.1` and does not depend on System.Drawing.

```powershell
dotnet build ScreenAI.Net.sln -c Release

# Local synthetic image only: no real document is uploaded or needed.
dotnet run --project samples/ScreenAI.Net.Sample -c Release -- "C:\path\to\screen_ai\153.3" --smoke

# Disposable workers, real parallel OCR, and synthetic lifecycle/fault checks.
dotnet run --project samples/ScreenAI.Net.Sample -c Release -- "C:\path\to\screen_ai\153.3" --worker-smoke "C:\path\to\artifacts\worker\win-x64\ScreenAI.Net.Worker.exe"

# Decode an image and print the structured result as JSON.
dotnet run --project samples/ScreenAI.Net.Sample -c Release -- "C:\path\to\screen_ai\153.3" "C:\path\to\scan.png"

dotnet pack src/ScreenAI.Net -c Release -o artifacts
```

The sample is a separate console process. `--smoke` checks exact synthetic tokens, coordinates/confidence, symbol parsing, repeat
calls across GC, serialized concurrent callers, padded stride, input validation, mode switching, and a blank image.
`--worker-smoke` checks real independent native workers, disposal isolation, resizing/coordinates, a concurrent pool, mode switches,
crash recovery, idle shutdown/restart, and parent-death cleanup. A test-only protocol fixture inside the sample checks queue versus
active cancellation, disposal with pending work, startup/request timeouts, and recovery. It is not shipped as the worker host.
It is a compatibility smoke check, not an accuracy benchmark on scanned business documents.

## Publishing

`dotnet pack` produces the client/library package only. Distribute a separately published matching worker host when using the worker
APIs. No Chrome binaries or models are redistributed. Linux and macOS native implementations remain explicit `NotImplementedException`
placeholders; portable APIs do not imply native support on those platforms. This is an unofficial, version-sensitive preview.

See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

## References

- [clv-locro](https://github.com/sergiocorreia/clv-locro): Python reference and reverse-engineered x64 bitmap layout.
- [Chromium Screen AI wrapper](https://github.com/chromium/chromium/blob/main/services/screen_ai/screen_ai_library_wrapper_impl.h): native function signatures.
- [VisualAnnotation schema](https://github.com/chromium/chromium/blob/main/services/screen_ai/proto/chrome_screen_ai.proto): OCR result wire format.
- [Native implementation notes](native-interop.md).
