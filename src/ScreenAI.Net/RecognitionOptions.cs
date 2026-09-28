namespace ScreenAI
{
    public enum OversizedImageBehavior { Reject = 0, ResizeToFit = 1 }

    /// <summary>Per-request settings; copied at submission time.</summary>
    public sealed class RecognitionOptions
    {
        /// <summary>Override the engine/worker default for this request.</summary>
        public bool? UseLightMode { get; set; }
        /// <summary>Explicitly resize oversized input or reject it. Returned coordinates always refer to the original image.</summary>
        public OversizedImageBehavior OversizedImageBehavior { get; set; } = OversizedImageBehavior.Reject;
    }
}
