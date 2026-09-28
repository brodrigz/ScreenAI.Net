using System;
using System.Runtime.InteropServices;

namespace ScreenAI.Interop
{
    internal static class NativeRuntimeFactory
    {
        internal static NativeRuntimeBase Create(
            string componentDirectory,
            bool lightMode,
            bool allowUnverifiedVersion)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new PlatformNotSupportedException("The Windows Screen AI runtime currently requires an x64 process.");
                return new WindowsNativeRuntime(componentDirectory, lightMode, allowUnverifiedVersion);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return new LinuxNativeRuntime(componentDirectory, lightMode, allowUnverifiedVersion);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new MacOsNativeRuntime(componentDirectory, lightMode, allowUnverifiedVersion);

            throw new PlatformNotSupportedException("ScreenAI.Net does not support this operating system.");
        }
    }
}
