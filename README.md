# ScreenAI.Net
A **.NET Standard 2.1** wrapper for Chrome's local Screen AI OCR engine, thread-safe with disposable workers.

Executes Chrome's Screen AI managed code directly, no need for a browser, chromedriver or cloud services.

Accepts image buffers and returns recognized text with coordinates and confidence scores. Image decoding and PDF rendering aren't included.

**Currently only Windows x64 support (chrome_screen_ai.dll)** 

## Setup

You need the .NET 8 SDK and a Screen AI component folder: `chrome_screen_ai.dll`, `files_list_ocr.txt`, and the matching models. Chrome usually keeps these at:

```text
%LOCALAPPDATA%\Google\Chrome\User Data\screen_ai\<version>\
```

Tested with **153.2 and 153.3**. Other versions are rejected by default. The DLL and models aren't bundled; check their terms before redistributing them.

Add a project reference to `src/ScreenAI.Net/ScreenAI.Net.csproj`. For IIS or services, put the component somewhere the service account can read.

## Use

`pixels` is a top-down, premultiplied BGRA32 buffer, not PNG/JPEG bytes. Use alpha 255 for opaque images. `stride` is bytes per row, usually `width * 4`.

### 1. Call it directly

No worker executable needed. OCR runs inside your application process.

```csharp
using System;
using ScreenAI;

var engine = new ScreenAiEngine(new ScreenAiOptions(@"C:\screen_ai\153.3"));

var result = engine.RecognizeBgra(
    pixels, width, height, stride,
    options: new RecognitionOptions
    {
        OversizedImageBehavior = OversizedImageBehavior.ResizeToFit
    });

Console.WriteLine(result.Text);
// result.Lines -> Words -> Symbols, with bounds and available confidence values.
```

`ResizeToFit` shrinks oversized images before OCR and maps coordinates back to the original image. Without it, oversized images are rejected.

The native library has global state and no supported shutdown API. Direct calls are serialized, the runtime stays loaded until your app exits, and a native crash can take the app down. There's no `Dispose` on the engine.

### 2. Use a worker

Same OCR, in a child process. Publish the worker from the repo root:

```powershell
dotnet publish src/ScreenAI.Net.Worker -c Release -r win-x64 --self-contained false -o artifacts/worker
```

Deploy the **whole output folder**, not just the exe. The server needs the .NET 8 x64 runtime; use `--self-contained true` to bundle it.

```csharp
using System;
using System.IO;
using ScreenAI;

var options = new ScreenAiWorkerOptions(
    componentDirectory: @"C:\screen_ai\153.3",
    workerPath: Path.GetFullPath("artifacts/worker/ScreenAI.Net.Worker.exe"));

await using var worker = new ScreenAiWorker(options);

var result = await worker.RecognizeBgraAsync(
    pixels, width, height, stride,
    options: new RecognitionOptions
    {
        OversizedImageBehavior = OversizedImageBehavior.ResizeToFit
    },
    cancellationToken: cancellationToken);

Console.WriteLine(result.Text);
```

Reuse the worker across calls. Don't modify or return the pixel buffer to a pool until the call finishes.

**Dispose stops the child process**; it doesn't dispose an independent native engine inside your app.

Workers start on first use. Active OCR cancellation or a timeout stops the child; the next request can start a fresh one. Failed requests aren't retried automatically.

For parallel OCR, use `ScreenAiWorkerPool` ([example](docs/usage.md#concurrency-cancellation-and-recovery)). Each worker loads its own models.

## More

- [Usage guide](docs/usage.md): direct calls, configuration, deployment, and sample commands.
- [Native interop notes](docs/native-interop.md): ABI details and implementation limits.
- [clv-locro](https://github.com/sergiocorreia/clv-locro): the Python reference this started from.
