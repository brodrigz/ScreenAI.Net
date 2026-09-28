using System;
using System.IO;
using System.Threading;

namespace ScreenAI
{
    public sealed class ScreenAiWorkerOptions
    {
        public ScreenAiWorkerOptions(string componentDirectory, string workerPath)
        {
            ComponentDirectory = componentDirectory;
            WorkerPath = workerPath;
        }
        public string ComponentDirectory { get; }
        /// <summary>Absolute path to the published ScreenAI.Net.Worker executable or DLL.</summary>
        public string WorkerPath { get; }
        public string DotNetHostPath { get; set; } = "dotnet";
        public bool UseLightMode { get; set; }
        public bool AllowUnverifiedNativeVersion { get; set; }
        /// <summary>Admitted waiting requests, in addition to the one executing. Further callers asynchronously await capacity.</summary>
        public int QueueCapacity { get; set; } = 4;
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Execution deadline after startup, excluding queue wait.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan RestartBackoff { get; set; } = TimeSpan.FromMilliseconds(250);
        public TimeSpan MaximumRestartBackoff { get; set; } = TimeSpan.FromSeconds(10);

        internal ScreenAiWorkerOptions Snapshot()
        {
            if (string.IsNullOrWhiteSpace(ComponentDirectory)) throw new ArgumentException("Component directory is required.");
            if (string.IsNullOrWhiteSpace(WorkerPath)) throw new ArgumentException("Worker path is required.");
            if (string.IsNullOrWhiteSpace(DotNetHostPath)) throw new ArgumentException(".NET host path is required.");
            if (QueueCapacity < 0 || QueueCapacity > 1024) throw new ArgumentOutOfRangeException(nameof(QueueCapacity));
            CheckDuration(StartupTimeout, nameof(StartupTimeout));
            CheckDuration(RequestTimeout, nameof(RequestTimeout));
            if (IdleTimeout != Timeout.InfiniteTimeSpan) CheckDuration(IdleTimeout, nameof(IdleTimeout));
            CheckDuration(RestartBackoff, nameof(RestartBackoff));
            CheckDuration(MaximumRestartBackoff, nameof(MaximumRestartBackoff));
            if (MaximumRestartBackoff < RestartBackoff) throw new ArgumentException("Maximum restart backoff must be at least the initial backoff.");
            return new ScreenAiWorkerOptions(Path.GetFullPath(ComponentDirectory), Path.GetFullPath(WorkerPath))
            {
                DotNetHostPath = DotNetHostPath, UseLightMode = UseLightMode,
                AllowUnverifiedNativeVersion = AllowUnverifiedNativeVersion, QueueCapacity = QueueCapacity,
                StartupTimeout = StartupTimeout, RequestTimeout = RequestTimeout, IdleTimeout = IdleTimeout,
                RestartBackoff = RestartBackoff, MaximumRestartBackoff = MaximumRestartBackoff
            };
        }
        private static void CheckDuration(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(name);
        }
    }
}
