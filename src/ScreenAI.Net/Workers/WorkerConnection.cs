using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenAI.Workers
{
    internal sealed class WorkerConnection : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Process _process;
        private readonly StringBuilder _diagnostics = new StringBuilder();
        private bool _started, _aborted, _disposed;
        internal NamedPipeServerStream Pipe { get; }
        internal Version NativeVersion { get; private set; } = new Version(0, 0);
        internal int MaximumDimension { get; private set; }
        internal bool Verified { get; private set; }
        internal int Id { get; private set; }
        internal bool IsAlive { get { lock (_gate) return _started && !_aborted && !_disposed && !_process.HasExited; } }
        internal string Diagnostics { get { lock (_diagnostics) return _diagnostics.ToString(); } }

        internal WorkerConnection(ScreenAiWorkerOptions options)
        {
            if (!File.Exists(options.WorkerPath)) throw new FileNotFoundException("Publish the ScreenAI.Net.Worker host and configure its path.", options.WorkerPath);
            string pipeName = "ScreenAI.Net." + Guid.NewGuid().ToString("N");
            Pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo
            {
                FileName = options.WorkerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? options.DotNetHostPath : options.WorkerPath,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (options.WorkerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(options.WorkerPath);
            start.ArgumentList.Add(pipeName);
            using (var parent = Process.GetCurrentProcess()) start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _process = new Process { StartInfo = start };
        }

        internal async Task StartAsync(ScreenAiWorkerOptions options, CancellationToken token)
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_aborted || _disposed) throw new OperationCanceledException(token);
                if (!_process.Start()) throw new ScreenAiException("Could not start OCR worker.");
                _started = true; Id = _process.Id;
                _ = DrainAsync(_process.StandardError);
                _ = DrainAsync(_process.StandardOutput);
            }
            await Pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            await WorkerProtocol.WriteAsync(Pipe, WorkerProtocol.Build(w =>
            {
                w.Write(WorkerProtocol.Initialize); w.Write(WorkerProtocol.Version);
                w.Write(options.ComponentDirectory); w.Write(options.UseLightMode); w.Write(options.AllowUnverifiedNativeVersion);
            }), token).ConfigureAwait(false);
            using var reply = WorkerProtocol.Reader(await WorkerProtocol.ReadAsync(Pipe, token).ConfigureAwait(false));
            WorkerProtocol.CheckSuccess(reply);
            if (reply.ReadInt32() != WorkerProtocol.Version) throw new ScreenAiException("OCR worker protocol version mismatch.");
            NativeVersion = Version.Parse(reply.ReadString());
            MaximumDimension = reply.ReadInt32(); Verified = reply.ReadBoolean();
            if (MaximumDimension <= 0 || MaximumDimension > 32768) throw new InvalidDataException("Invalid worker image threshold.");
        }

        internal void Abort()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _aborted = true;
                Pipe.Dispose();
                if (_started)
                {
                    try { if (!_process.HasExited) _process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception error)
                    {
                        // Cancellation callbacks must not throw on a timer thread. Dispose retries and verifies exit.
                        lock (_diagnostics)
                        {
                            _diagnostics.Append("Worker termination failed: ").Append(error.Message);
                            if (_diagnostics.Length > 4096) _diagnostics.Remove(0, _diagnostics.Length - 4096);
                        }
                    }
                }
            }
        }
        public void Dispose()
        {
            Abort();
            lock (_gate)
            {
                if (_disposed) return;
                if (_started && !_process.WaitForExit(5000)) throw new ScreenAiException("OCR worker did not exit after termination.");
                _disposed = true;
                _process.Dispose();
                Pipe.Dispose();
            }
        }
        private async Task DrainAsync(StreamReader reader)
        {
            try
            {
                var buffer = new char[1024];
                int read;
                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    lock (_diagnostics)
                    {
                        _diagnostics.Append(buffer, 0, read);
                        if (_diagnostics.Length > 4096) _diagnostics.Remove(0, _diagnostics.Length - 4096);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
