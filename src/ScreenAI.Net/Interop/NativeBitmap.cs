using System;
using System.Runtime.InteropServices;

namespace ScreenAI.Interop
{
    /// <summary>
    /// Explicit x64 SkBitmap ABI based on clv-locro's reference, not a managed SkiaSharp object.
    /// The synthetic pixel reference is ONLY valid for the verified synchronous OCR code path.
    /// See THIRD-PARTY-NOTICES.md and docs/native-interop.md.
    /// </summary>
    internal sealed class NativeBitmap : IDisposable
    {
        private IntPtr _pixels;
        private IntPtr _pixelReference;
        private IntPtr _vtable;
        public IntPtr Address { get; private set; }

        public NativeBitmap(ReadOnlySpan<byte> pixels, int width, int height, int stride)
        {
            try
            {
                // Screen AI's current conversion path assumes tightly packed pixels even when
                // SkPixmap advertises a wider row stride. Normalize here instead of leaking that
                // undocumented limitation into the public API.
                var rowBytes = checked(width * 4);
                var data = new byte[checked(rowBytes * height)];
                for (var row = 0; row < height; row++)
                    pixels.Slice(row * stride, rowBytes).CopyTo(data.AsSpan(row * rowBytes, rowBytes));
                _pixels = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, _pixels, data.Length);
                _vtable = AllocateZeroed(16 * IntPtr.Size);
                _pixelReference = AllocateZeroed(104);
                Marshal.WriteIntPtr(_pixelReference, 0, _vtable);
                Marshal.WriteInt32(_pixelReference, 8, 1); // reference count
                Marshal.WriteInt32(_pixelReference, 16, width);
                Marshal.WriteInt32(_pixelReference, 20, height);
                Marshal.WriteIntPtr(_pixelReference, 24, _pixels);
                Marshal.WriteInt64(_pixelReference, 32, rowBytes);

                Address = AllocateZeroed(56);
                Marshal.WriteIntPtr(Address, 0, _pixelReference);
                Marshal.WriteIntPtr(Address, 8, _pixels);
                Marshal.WriteInt64(Address, 16, rowBytes);
                // Offset 24: null color space, interpreted as sRGB.
                Marshal.WriteInt32(Address, 32, 6); // kBGRA_8888
                Marshal.WriteInt32(Address, 36, 2); // kPremul
                Marshal.WriteInt32(Address, 40, width);
                Marshal.WriteInt32(Address, 44, height);
                // Offset 48: flags, zero.
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static IntPtr AllocateZeroed(int size)
        {
            var pointer = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, pointer, size);
            return pointer;
        }

        public void Dispose()
        {
            if (Address != IntPtr.Zero) { Marshal.FreeHGlobal(Address); Address = IntPtr.Zero; }
            Free(ref _pixelReference);
            Free(ref _vtable);
            Free(ref _pixels);
        }

        private static void Free(ref IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(pointer);
            pointer = IntPtr.Zero;
        }
    }
}
