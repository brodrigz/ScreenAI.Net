using System;
using System.IO;
using ScreenAI.Interop;
using ScreenAI.Parsing;
using ScreenAI.Imaging;

namespace ScreenAI
{
    /// <summary>
    /// Local Windows x64 OCR using a caller-supplied Screen AI component.
    /// Instances share one process-lifetime native runtime; all recognition calls are serialized.
    /// </summary>
    public sealed class ScreenAiEngine
    {
        private static readonly object Gate = new object();
        private static NativeRuntimeBase? _shared;
        private static Exception? _initializationFailure;
        private readonly NativeRuntimeBase _runtime;
        private readonly bool _defaultLightMode;

        public ScreenAiEngine(ScreenAiOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _defaultLightMode = options.UseLightMode;
            var directory = Path.GetFullPath(options.ComponentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            lock (Gate)
            {
                if (_initializationFailure != null)
                    throw new ScreenAiException("Native OCR initialization previously failed. Restart the process before retrying.", _initializationFailure);
                if (_shared == null)
                {
                    // Root the runtime BEFORE callbacks are registered. There is no documented native shutdown API.
                    _shared = NativeRuntimeFactory.Create(directory, options.UseLightMode, options.AllowUnverifiedNativeVersion);
                    try { _shared.Initialize(); }
                    catch (Exception error) { _initializationFailure = error; throw; }
                }
                else if (!string.Equals(_shared.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Screen AI is already initialized with a different component directory. Use ScreenAiWorker for independently owned runtimes.");
                }
                if (!_shared.IsVerifiedVersion && !options.AllowUnverifiedNativeVersion)
                    throw new NotSupportedException("The process was initialized with an unverified native version; explicit opt-in is required.");
                _runtime = _shared;
            }
        }

        public Version NativeVersion => _runtime.Version;
        public bool IsVerifiedNativeVersion => _runtime.IsVerifiedVersion;
        public int MaximumImageDimension => _runtime.MaximumDimension;

        /// <summary>
        /// Recognizes a top-down BGRA32 image with premultiplied alpha. Opaque pixels use alpha 255.
        /// Stride defaults to width * 4; padded positive strides are supported. Resizing requires explicit opt-in.
        /// Results use input-image pixel coordinates. The call is synchronous and cannot cancel native inference.
        /// </summary>
        public OcrResult RecognizeBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride = 0, RecognitionOptions? options = null)
        {
            var image = PreparedImage.Create(pixels, width, height, stride, MaximumImageDimension,
                options?.OversizedImageBehavior ?? OversizedImageBehavior.Reject);
            var annotation = RecognizeRaw(image.Pixels, image.Width, image.Height, options?.UseLightMode ?? _defaultLightMode);
            return AnnotationParser.Parse(annotation, image.Width, image.Height).MapToOriginal(width, height);
        }

        internal byte[] RecognizeRaw(ReadOnlySpan<byte> packedPixels, int width, int height, bool lightMode)
        {
            PreparedImage.Validate(packedPixels.Length, width, height, checked(width * 4));
            lock (Gate)
            {
                if (_initializationFailure != null)
                    throw new ScreenAiException("Native OCR initialization previously failed. Restart the process before retrying.", _initializationFailure);
                try { _runtime.SetLightMode(lightMode); }
                catch (Exception error) { _initializationFailure = error; throw; }
                using var bitmap = new NativeBitmap(packedPixels, width, height, checked(width * 4));
                return _runtime.Recognize(bitmap.Address);
            }
        }
    }
}
