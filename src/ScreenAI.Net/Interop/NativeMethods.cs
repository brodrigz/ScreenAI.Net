using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScreenAI.Interop
{
    internal static class NativeMethods
    {
        // NativeLibrary is not part of the .NET Standard 2.1 contract. Use the Windows loader directly.
        // Limit dependency lookup to the trusted component directory and System32, never the current directory.
        internal static IntPtr Load(string path)
        {
            var handle = LoadLibraryExW(path, IntPtr.Zero, 0x00000100 | 0x00000800);
            if (handle == IntPtr.Zero)
                throw new ScreenAiException("Unable to load the Windows x64 Screen AI component: " + path,
                    new Win32Exception(Marshal.GetLastWin32Error()));
            return handle;
        }

        internal static T GetExport<T>(IntPtr handle, string name) where T : Delegate
        {
            var address = GetProcAddress(handle, name);
            if (address == IntPtr.Zero)
                throw new ScreenAiException("The component does not export " + name + ". Its native interface is incompatible.");
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void GetVersion(out uint major, out uint minor);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint FileSize(IntPtr relativePath);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void FileContent(IntPtr relativePath, uint bufferSize, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SetFileCallbacks(FileSize size, FileContent content);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool InitOcr();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void SetLightMode([MarshalAs(UnmanagedType.I1)] bool enabled);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate uint GetMaxDimension();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr PerformOcr(IntPtr bitmap, out uint resultLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void FreeResult(IntPtr result);
    }
}
