using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScreenAI.Workers;
using ScreenAI.Imaging;

namespace ScreenAI
{
    /// <summary>Bounded async admission and parallel recognition across independently owned worker processes.</summary>
    public sealed class ScreenAiWorkerPool : IDisposable, IAsyncDisposable
    {
        private readonly ScreenAiWorker[] _workers;
        private readonly ConcurrentQueue<ScreenAiWorker> _available = new ConcurrentQueue<ScreenAiWorker>();
        private readonly SemaphoreSlim _leases, _capacity;
        private readonly OperationLifetime _lifetime = new OperationLifetime();
        private readonly object _disposeGate = new object();
        private Task? _disposeTask;
        public ScreenAiWorkerPool(ScreenAiWorkerOptions options, int workerCount = 2, int queueCapacity = 4)
        {
            if (workerCount < 1 || workerCount > 32) throw new ArgumentOutOfRangeException(nameof(workerCount));
            if (queueCapacity < 0 || queueCapacity > 1024) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
            var snapshot = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
            snapshot.QueueCapacity = 0;
            _workers = Enumerable.Range(0, workerCount).Select(_ => new ScreenAiWorker(snapshot)).ToArray();
            foreach (var worker in _workers) _available.Enqueue(worker);
            _leases = new SemaphoreSlim(workerCount, workerCount);
            _capacity = new SemaphoreSlim(workerCount + queueCapacity, workerCount + queueCapacity);
        }
        public async Task<OcrResult> RecognizeBgraAsync(ReadOnlyMemory<byte> pixels, int width, int height,
            int stride = 0, RecognitionOptions? options = null, CancellationToken cancellationToken = default)
        {
            _lifetime.Enter();
            bool admitted = false, leased = false;
            ScreenAiWorker? worker = null;
            var request = options == null ? null : new RecognitionOptions { UseLightMode = options.UseLightMode, OversizedImageBehavior = options.OversizedImageBehavior };
            try
            {
                stride = PreparedImage.Validate(pixels.Length, width, height, stride);
                if (request != null && request.OversizedImageBehavior != OversizedImageBehavior.Reject &&
                    request.OversizedImageBehavior != OversizedImageBehavior.ResizeToFit) throw new ArgumentOutOfRangeException(nameof(options));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Cancellation.Token);
                await _capacity.WaitAsync(linked.Token).ConfigureAwait(false); admitted = true;
                await _leases.WaitAsync(linked.Token).ConfigureAwait(false); leased = true;
                if (!_available.TryDequeue(out worker)) throw new InvalidOperationException("Worker lease invariant failed.");
                return await worker.RecognizeBgraAsync(pixels, width, height, stride, request, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                if (worker != null) _available.Enqueue(worker);
                if (leased) _leases.Release();
                if (admitted) _capacity.Release();
                _lifetime.Exit();
            }
        }
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (_disposeTask == null) { _lifetime.Stop(); _disposeTask = FinishDisposeAsync(); }
                return new ValueTask(_disposeTask);
            }
        }
        private async Task FinishDisposeAsync()
        {
            await Task.WhenAll(_workers.Select(worker => worker.DisposeAsync().AsTask())).ConfigureAwait(false);
            await _lifetime.Drained.ConfigureAwait(false);
            _capacity.Dispose(); _leases.Dispose(); _lifetime.Cancellation.Dispose();
        }
    }
}
