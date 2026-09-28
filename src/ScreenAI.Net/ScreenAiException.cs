using System;

namespace ScreenAI
{
    /// <summary>A managed initialization, callback, or OCR-result failure. Native crashes cannot be caught reliably.</summary>
    public sealed class ScreenAiException : Exception
    {
        public ScreenAiException(string message) : base(message) { }
        public ScreenAiException(string message, Exception innerException) : base(message, innerException) { }
    }
}
