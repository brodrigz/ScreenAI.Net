using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScreenAI.Imaging;
using ScreenAI.Parsing;
using ScreenAI.Workers;

namespace ScreenAI
{
    /// <summary>An independently owned, restartable OCR process. Dispose releases its native memory and model handles.</summary>
    public sealed class ScreenAiWorker : IDisposable, IAsyncDisposable
    {
        private readonly ScreenAiWorkerOptions _options;
        private readonly OperationLifetime _lifetime = new OperationLifetime();
        private readonly SemaphoreSlim _capacity;
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private readonly object _connectionGate = new object(), _disposeGate = new object();
        private WorkerConnection? _connection;
        private Task? _disposeTask;
        private readonly Timer? _idleTimer;
        private long _lastActivity = Stopwatch.GetTimestamp(), _nextStart;
        private int _failureCount;

        public ScreenAiWorker(ScreenAiWorkerOptions options)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
            _capacity = new SemaphoreSlim(_options.QueueCapacity + 1, _options.QueueCapacity + 1);
            if (_options.IdleTimeout != Timeout.InfiniteTimeSpan)
            {
                var interval = TimeSpan.FromMilliseconds(Math.Max(50, Math.Min(1000, _options.IdleTimeout.TotalMilliseconds / 2)));
                _idleTimer = new Timer(CheckIdle, null, interval, interval);
            }
        }
        /// <summary>Current child PID, or null before first use or after idle shutdown/disposal.</summary>
        public int? ProcessId { get { lock (_connectionGate) return _connection?.IsAlive == true ? _connection.Id : (int?)null; } }

        /// <summary>
        /// Caller retains the buffer and must not modify it until completion. Admission is bounded; no image copy is made while
        /// waiting for capacity. Cancellation during execution terminates this worker process; subsequent requests start a new one.
        /// </summary>
        public async Task<OcrResult> RecognizeBgraAsync(ReadOnlyMemory<byte> pixels, int width, int height,
            int stride = 0, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            _lifetime.Enter();
            bool admitted = false, executing = false;
            var mode = options?.UseLightMode ?? _options.UseLightMode;
            var resize = options?.OversizedImageBehavior ?? OversizedImageBehavior.Reject;
            try
            {
                stride = PreparedImage.Validate(pixels.Length, width, height, stride);
                if (resize != OversizedImageBehavior.Reject && resize != OversizedImageBehavior.ResizeToFit) throw new ArgumentOutOfRangeException(nameof(options));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Cancellation.Token);
                await _capacity.WaitAsync(linked.Token).ConfigureAwait(false); admitted = true;
                await _serial.WaitAsync(linked.Token).ConfigureAwait(false); executing = true;
                linked.Token.ThrowIfCancellationRequested();
                var connection = await EnsureStartedAsync(linked.Token).ConfigureAwait(false);
                var image = PreparedImage.Create(pixels.Span, width, height, stride, connection.MaximumDimension, resize);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                deadline.CancelAfter(_options.RequestTimeout);
                using var abort = deadline.Token.Register(connection.Abort);
                try
                {
                    var request = WorkerProtocol.Build(w =>
                    {
                        w.Write(WorkerProtocol.Recognize); w.Write(image.Width); w.Write(image.Height); w.Write(mode);
                        w.Write(image.Pixels.Length); w.Write(image.Pixels);
                    });
                    await WorkerProtocol.WriteAsync(connection.Pipe, request, deadline.Token).ConfigureAwait(false);
                    using var reply = WorkerProtocol.Reader(await WorkerProtocol.ReadAsync(connection.Pipe, deadline.Token).ConfigureAwait(false));
                    WorkerProtocol.CheckSuccess(reply);
                    int length = reply.ReadInt32();
                    if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("Invalid OCR annotation size.");
                    byte[] bytes = reply.ReadBytes(length);
                    if (bytes.Length != length || reply.BaseStream.Position != reply.BaseStream.Length) throw new InvalidDataException("Incomplete OCR response.");
                    deadline.Token.ThrowIfCancellationRequested();
                    var result = AnnotationParser.Parse(bytes, image.Width, image.Height).MapToOriginal(width, height);
                    deadline.Token.ThrowIfCancellationRequested();
                    _failureCount = 0;
                    return result;
                }
                catch (Exception error)
                {
                    RemoveConnection(connection);
                    if (linked.IsCancellationRequested) throw new OperationCanceledException(linked.Token);
                    RecordFailure();
                    if (deadline.IsCancellationRequested) throw new TimeoutException("OCR execution exceeded " + _options.RequestTimeout + ". The worker was terminated.", error);
                    throw new ScreenAiException("OCR worker failed. The next request may restart it. " + connection.Diagnostics, error);
                }
            }
            finally
            {
                if (executing) { _lastActivity = Stopwatch.GetTimestamp(); _serial.Release(); }
                if (admitted) _capacity.Release();
                _lifetime.Exit();
            }
        }

        private async Task<WorkerConnection> EnsureStartedAsync(CancellationToken token)
        {
            WorkerConnection? current;
            lock (_connectionGate) current = _connection;
            if (current?.IsAlive == true) return current;
            if (current != null) { RemoveConnection(current); RecordFailure(); }
            var wait = TimeSpan.FromSeconds((double)(_nextStart - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(_options.StartupTimeout);
            WorkerConnection connection;
            lock (_connectionGate)
            {
                token.ThrowIfCancellationRequested();
                connection = new WorkerConnection(_options);
                _connection = connection;
            }
            using var abort = startup.Token.Register(connection.Abort);
            try
            {
                await connection.StartAsync(_options, startup.Token).ConfigureAwait(false);
                startup.Token.ThrowIfCancellationRequested();
                return connection;
            }
            catch (Exception error)
            {
                RemoveConnection(connection);
                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                RecordFailure();
                if (startup.IsCancellationRequested) throw new TimeoutException("OCR worker startup timed out.", error);
                throw new ScreenAiException("OCR worker startup failed. " + connection.Diagnostics, error);
            }
        }

        private void RecordFailure()
        {
            _failureCount = Math.Min(20, _failureCount + 1);
            var seconds = Math.Min(_options.MaximumRestartBackoff.TotalSeconds,
                _options.RestartBackoff.TotalSeconds * Math.Pow(2, _failureCount - 1));
            _nextStart = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        }
        private void RemoveConnection(WorkerConnection connection)
        {
            connection.Dispose();
            lock (_connectionGate) { if (ReferenceEquals(_connection, connection)) _connection = null; }
        }
        private void CheckIdle(object? ignored)
        {
            if (!_lifetime.TryEnter()) return;
            try
            {
                if (!_serial.Wait(0)) return;
                try
                {
                    if ((double)(Stopwatch.GetTimestamp() - _lastActivity) / Stopwatch.Frequency < _options.IdleTimeout.TotalSeconds) return;
                    WorkerConnection? connection;
                    lock (_connectionGate) connection = _connection;
                    if (connection != null) RemoveConnection(connection);
                }
                finally { _serial.Release(); }
            }
            catch (Exception) { /* Timer cannot surface errors; the next request observes/replaces an exited process. */ }
            finally { _lifetime.Exit(); }
        }
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (_disposeTask == null)
                {
                    _idleTimer?.Dispose();
                    _lifetime.Stop();
                    _disposeTask = FinishDisposeAsync();
                }
                return new ValueTask(_disposeTask);
            }
        }
        private async Task FinishDisposeAsync()
        {
            await _lifetime.Drained.ConfigureAwait(false);
            WorkerConnection? connection;
            lock (_connectionGate) { connection = _connection; _connection = null; }
            connection?.Dispose();
            _capacity.Dispose(); _serial.Dispose(); _lifetime.Cancellation.Dispose();
        }
    }
}
