using System;

namespace ScreenAI.Interop
{
    /// <summary>Internal platform boundary. The public API remains platform-neutral.</summary>
    internal abstract class NativeRuntimeBase
    {
        internal abstract string DirectoryPath { get; }
        internal abstract bool LightMode { get; }
        internal abstract Version Version { get; }
        internal abstract bool IsVerifiedVersion { get; }
        internal abstract int MaximumDimension { get; }
        internal abstract void Initialize();
        internal abstract void SetLightMode(bool enabled);
        internal abstract byte[] Recognize(IntPtr bitmap);
    }
}
