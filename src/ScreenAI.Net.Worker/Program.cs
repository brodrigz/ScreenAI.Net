using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using ScreenAI;
using ScreenAI.Workers;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !args[0].StartsWith("ScreenAI.Net.", StringComparison.Ordinal) ||
            !int.TryParse(args[1], out int parentId))
        {
            Console.Error.WriteLine("This host is started and owned by ScreenAiWorker, not invoked directly.");
            return 2;
        }
        // Keep the original parent handle open: PID reuse cannot keep an orphan worker alive.
        using var parent = Process.GetProcessById(parentId);
        _ = parent.Handle;
        using var parentWatch = new Timer(_ =>
        {
            try { if (parent.HasExited) Environment.Exit(0); }
            catch (InvalidOperationException) { Environment.Exit(0); }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(30000).ConfigureAwait(false);
            using var init = WorkerProtocol.Reader(await WorkerProtocol.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false));
            if (init.ReadByte() != WorkerProtocol.Initialize || init.ReadInt32() != WorkerProtocol.Version)
                throw new InvalidDataException("Unsupported worker protocol.");
            var options = new ScreenAiOptions(init.ReadString())
            {
                UseLightMode = init.ReadBoolean(), AllowUnverifiedNativeVersion = init.ReadBoolean()
            };
            RequireEnd(init);
            var engine = new ScreenAiEngine(options);
            await WorkerProtocol.WriteAsync(pipe, WorkerProtocol.Build(w =>
            {
                w.Write((byte)0); w.Write(WorkerProtocol.Version); w.Write(engine.NativeVersion.ToString());
                w.Write(engine.MaximumImageDimension); w.Write(engine.IsVerifiedNativeVersion);
            }), CancellationToken.None).ConfigureAwait(false);
            while (true)
            {
                using var request = WorkerProtocol.Reader(await WorkerProtocol.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false));
                if (request.ReadByte() != WorkerProtocol.Recognize) throw new InvalidDataException("Unexpected worker command.");
                int width = request.ReadInt32(), height = request.ReadInt32();
                bool lightMode = request.ReadBoolean();
                int size = request.ReadInt32();
                if (width <= 0 || height <= 0 || width > engine.MaximumImageDimension || height > engine.MaximumImageDimension ||
                    size < 0 || size > 128 * 1024 * 1024 || (long)width * height * 4 != size)
                    throw new InvalidDataException("Invalid packed image dimensions.");
                var pixels = request.ReadBytes(size);
                if (pixels.Length != size) throw new EndOfStreamException("Incomplete image.");
                RequireEnd(request);
                // Native calls stay serialized. Parallelism is provided by separate processes.
                byte[] annotation = engine.RecognizeRaw(pixels, width, height, lightMode);
                await WorkerProtocol.WriteAsync(pipe, WorkerProtocol.Build(w =>
                {
                    w.Write((byte)0); w.Write(annotation.Length); w.Write(annotation);
                }), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (EndOfStreamException) { return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await WorkerProtocol.WriteAsync(pipe, WorkerProtocol.Build(w =>
                {
                    w.Write((byte)1); w.Write(error.Message.Length > 2048 ? error.Message.Substring(0, 2048) : error.Message);
                }), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* Parent may have cancelled/closed its pipe. */ }
            return 1;
        }
    }
    private static void RequireEnd(BinaryReader reader)
    {
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing worker message data.");
    }
}
