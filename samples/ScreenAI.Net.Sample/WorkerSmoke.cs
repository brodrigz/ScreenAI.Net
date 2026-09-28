using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScreenAI;
using ScreenAI.Workers;

// Lifecycle fault injection lives only in this sample, never in the production worker.
internal static class WorkerSmoke
{
    internal static async Task<int> Run(string component, string host)
    {
        using var bitmap = new Bitmap(2400, 720, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 72, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("SCREEN AI 12345", font, Brushes.Black, 60, 60);
            graphics.DrawString("PN ABC-123 QTY 25", font, Brushes.Black, 60, 240);
        }
        var pixels = Program.ReadPixels(bitmap);
        var resize = new RecognitionOptions { OversizedImageBehavior = OversizedImageBehavior.ResizeToFit };
        var options = new ScreenAiWorkerOptions(component, host) { IdleTimeout = Timeout.InfiniteTimeSpan };
        await using (var first = new ScreenAiWorker(options))
        await using (var second = new ScreenAiWorker(options))
        {
            var results = await Task.WhenAll(first.RecognizeBgraAsync(pixels, 2400, 720, options: resize),
                second.RecognizeBgraAsync(pixels, 2400, 720, options: resize));
            Check(first.ProcessId.HasValue && second.ProcessId.HasValue && first.ProcessId != second.ProcessId, "Workers must own distinct processes.");
            foreach (var result in results)
            {
                Check(result.Text.Contains("ABC-123") && result.Text.Contains("12345"), "Worker OCR tokens missing.");
                Check(result.Width == 2400 && result.Height == 720 && result.ProcessedWidth == 2048 && result.ProcessedHeight < 720, "Resize metadata.");
                var bounds = result.Lines.First().Bounds!;
                Check(bounds.X > 55 && bounds.X < 90 && bounds.Y > 50 && bounds.Width > 400, "Mapped coordinates.");
            }
            int firstId = first.ProcessId!.Value, secondId = second.ProcessId!.Value;
            await first.DisposeAsync();
            Check(!Alive(firstId) && Alive(secondId), "Disposal must only terminate the owned child.");
            resize.UseLightMode = true;
            Check((await second.RecognizeBgraAsync(pixels, 2400, 720, options: resize)).Text.Contains("12345"), "Per-request light mode.");
            resize.UseLightMode = false;
            Check((await second.RecognizeBgraAsync(pixels, 2400, 720, options: resize)).Text.Contains("12345"), "Restore full mode.");
            Check(second.ProcessId == secondId, "Mode change unnecessarily restarted worker.");
            using (var process = Process.GetProcessById(secondId)) { process.Kill(); await process.WaitForExitAsync(); }
            Check((await second.RecognizeBgraAsync(pixels, 2400, 720, options: resize)).Text.Contains("12345"), "Restart after crash.");
            Check(second.ProcessId != secondId, "Crash recovery did not restart.");
        }
        Console.WriteLine("PASS: real native workers, concurrent independent processes, independent disposal, resize/coordinates, light/full switching, crash restart.");

        await using (var pool = new ScreenAiWorkerPool(options, workerCount: 2, queueCapacity: 1))
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => pool.RecognizeBgraAsync(pixels, 2400, 720, options: resize)));
            Check(results.All(r => r.Text.Contains("ABC-123")), "Pooled results.");
        }
        Console.WriteLine("PASS: bounded worker pool completes concurrent callers.");

        options.IdleTimeout = TimeSpan.FromMilliseconds(200);
        await using (var idle = new ScreenAiWorker(options))
        {
            await idle.RecognizeBgraAsync(pixels, 2400, 720, options: resize);
            int oldId = idle.ProcessId!.Value;
            await Until(() => idle.ProcessId == null);
            Check(!Alive(oldId), "Idle worker still alive.");
            await idle.RecognizeBgraAsync(pixels, 2400, 720, options: resize);
            Check(idle.ProcessId.HasValue && idle.ProcessId != oldId, "Idle restart.");
        }
        Console.WriteLine("PASS: idle shutdown and lazy restart.");
        await FaultChecks(component);
        await ParentExitCheck(component, host);
        return 0;
    }

    internal static async Task<int> RunParentProbe(string component, string host)
    {
        // Intentionally no Dispose: the outer smoke process kills this parent to exercise orphan cleanup.
        var worker = new ScreenAiWorker(new ScreenAiWorkerOptions(component, host) { IdleTimeout = Timeout.InfiniteTimeSpan });
        var blank = Enumerable.Repeat((byte)255, 400 * 200 * 4).ToArray();
        await worker.RecognizeBgraAsync(blank, 400, 200);
        Console.WriteLine(worker.ProcessId!.Value);
        await Task.Delay(Timeout.Infinite);
        GC.KeepAlive(worker);
        return 0;
    }

    private static async Task ParentExitCheck(string component, string host)
    {
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(WorkerSmoke).Assembly.Location, ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--parent-probe"); start.ArgumentList.Add(component); start.ArgumentList.Add(host);
        using var parent = Process.Start(start)!;
        var errors = parent.StandardError.ReadToEndAsync();
        try
        {
            string? line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(int.TryParse(line, out int child), "Parent probe did not report child PID.");
            Check(Alive(child), "Parent probe child missing.");
            parent.Kill(); await parent.WaitForExitAsync();
            await Until(() => !Alive(child));
        }
        finally { if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); } await errors; }
        Console.WriteLine("PASS: worker exits when its parent is terminated without disposal.");
    }

    private static async Task FaultChecks(string component)
    {
        var fakeHost = Path.ChangeExtension(typeof(WorkerSmoke).Assembly.Location, ".exe");
        var options = new ScreenAiWorkerOptions(component, fakeHost) { IdleTimeout = Timeout.InfiniteTimeSpan, QueueCapacity = 0 };
        var fast = new byte[] { 0, 0, 0, 255 };
        var slow = new byte[] { 1, 0, 0, 255 };
        await using (var worker = new ScreenAiWorker(options))
        {
            await worker.RecognizeBgraAsync(fast, 1, 1);
            int id = worker.ProcessId!.Value;
            var active = worker.RecognizeBgraAsync(slow, 1, 1);
            using var queuedCancellation = new CancellationTokenSource(50);
            await Expect<OperationCanceledException>(() => worker.RecognizeBgraAsync(fast, 1, 1, cancellationToken: queuedCancellation.Token));
            await active;
            Check(worker.ProcessId == id, "Queued cancellation killed unrelated active OCR.");
            using var activeCancellation = new CancellationTokenSource(100);
            await Expect<OperationCanceledException>(() => worker.RecognizeBgraAsync(slow, 1, 1, cancellationToken: activeCancellation.Token));
            Check(!Alive(id) && worker.ProcessId == null, "Active cancellation left child alive.");
            await worker.RecognizeBgraAsync(fast, 1, 1);
            id = worker.ProcessId!.Value;
            active = worker.RecognizeBgraAsync(slow, 1, 1);
            var queued = worker.RecognizeBgraAsync(fast, 1, 1);
            await Task.WhenAll(worker.DisposeAsync().AsTask(), worker.DisposeAsync().AsTask());
            await Expect<OperationCanceledException>(() => active);
            await Expect<OperationCanceledException>(() => queued);
            Check(!Alive(id), "Dispose with active/queued work leaked child.");
            await Expect<ObjectDisposedException>(() => worker.RecognizeBgraAsync(fast, 1, 1));
        }
        options.RequestTimeout = TimeSpan.FromMilliseconds(100);
        await using (var worker = new ScreenAiWorker(options))
        {
            await Expect<TimeoutException>(() => worker.RecognizeBgraAsync(slow, 1, 1));
            Check(worker.ProcessId == null, "Timeout did not detach child.");
            await worker.RecognizeBgraAsync(fast, 1, 1);
        }
        options.StartupTimeout = TimeSpan.FromMilliseconds(1);
        await using (var worker = new ScreenAiWorker(options))
        {
            await Expect<TimeoutException>(() => worker.RecognizeBgraAsync(fast, 1, 1));
            Check(worker.ProcessId == null, "Startup timeout left child alive.");
        }
        Console.WriteLine("PASS: queue backpressure/cancellation, active cancellation, disposal during queued/active work, repeated disposal, request/startup timeouts, recovery.");
    }

    internal static async Task<int> RunFixture(string[] args)
    {
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await WorkerProtocol.ReadAsync(pipe, CancellationToken.None);
        await WorkerProtocol.WriteAsync(pipe, WorkerProtocol.Build(w =>
        {
            w.Write((byte)0); w.Write(WorkerProtocol.Version); w.Write("153.3"); w.Write(2048); w.Write(true);
        }), CancellationToken.None);
        while (true)
        {
            using var request = WorkerProtocol.Reader(await WorkerProtocol.ReadAsync(pipe, CancellationToken.None));
            request.ReadByte(); request.ReadInt32(); request.ReadInt32(); request.ReadBoolean(); request.ReadInt32();
            if (request.ReadByte() == 1) await Task.Delay(1000);
            await WorkerProtocol.WriteAsync(pipe, WorkerProtocol.Build(w => { w.Write((byte)0); w.Write(0); }), CancellationToken.None);
        }
    }
    private static bool Alive(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Smoke condition."); await Task.Delay(25); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
