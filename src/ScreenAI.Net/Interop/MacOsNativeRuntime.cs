using System;

namespace ScreenAI.Interop
{
    /// <summary>Reserved implementation boundary for Chrome's macOS libchromescreenai.so component.</summary>
    internal sealed class MacOsNativeRuntime : NativeRuntimeBase
    {
        internal MacOsNativeRuntime(string componentDirectory, bool lightMode, bool allowUnverifiedVersion)
        {
            throw new NotImplementedException(
                "macOS libchromescreenai.so support is scaffolded but not implemented or ABI-verified yet.");
        }

        internal override string DirectoryPath => throw new NotImplementedException();
        internal override bool LightMode => throw new NotImplementedException();
        internal override Version Version => throw new NotImplementedException();
        internal override bool IsVerifiedVersion => throw new NotImplementedException();
        internal override int MaximumDimension => throw new NotImplementedException();
        internal override void Initialize() => throw new NotImplementedException();
        internal override void SetLightMode(bool enabled) => throw new NotImplementedException();
        internal override byte[] Recognize(IntPtr bitmap) => throw new NotImplementedException();
    }
}
