using System;

namespace ScreenAI
{
    /// <summary>Settings for the process-wide native OCR runtime.</summary>
    public sealed class ScreenAiOptions
    {
        public ScreenAiOptions(string componentDirectory)
        {
            if (string.IsNullOrWhiteSpace(componentDirectory))
                throw new ArgumentException("A trusted Screen AI component directory is required.", nameof(componentDirectory));
            ComponentDirectory = componentDirectory;
        }

        /// <summary>Directory containing chrome_screen_ai.dll and its matching model/configuration files.</summary>
        public string ComponentDirectory { get; }

        /// <summary>Default OCR mode for this engine instance; individual requests may override it.</summary>
        public bool UseLightMode { get; set; }

        /// <summary>
        /// Permit an unverified native version to use the known bitmap layout. This can crash the process.
        /// Use only in an isolated compatibility experiment, never as an automatic production fallback.
        /// </summary>
        public bool AllowUnverifiedNativeVersion { get; set; }
    }
}
