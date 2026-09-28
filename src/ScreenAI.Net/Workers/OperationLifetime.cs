using System;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenAI.Workers
{
    // Counts callers including those waiting for admission, so disposal waits until they have unwound.
    internal sealed class OperationLifetime
    {
        private readonly object _gate = new object();
        private readonly TaskCompletionSource<bool> _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _operations;
        private bool _stopping;
        internal CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
        internal Task Drained => _drained.Task;
        internal bool TryEnter()
        {
            lock (_gate) { if (_stopping) return false; _operations++; return true; }
        }
        internal void Enter()
        {
            if (!TryEnter()) throw new ObjectDisposedException("Screen AI worker");
        }
        internal void Exit()
        {
            lock (_gate) { if (--_operations == 0 && _stopping) _drained.TrySetResult(true); }
        }
        internal void Stop()
        {
            lock (_gate) { _stopping = true; if (_operations == 0) _drained.TrySetResult(true); }
            Cancellation.Cancel();
        }
    }
}
