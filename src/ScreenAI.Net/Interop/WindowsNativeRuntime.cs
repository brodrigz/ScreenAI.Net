using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenAI.Interop
{
    internal sealed class WindowsNativeRuntime : NativeRuntimeBase
    {
        private const int MaxModelFileBytes = 256 * 1024 * 1024;
        private const uint MaxResultBytes = 64 * 1024 * 1024;
        private readonly IntPtr _module;
        private readonly string _directoryPrefix;
        private readonly ConcurrentDictionary<string, Lazy<FileStream>> _models = new ConcurrentDictionary<string, Lazy<FileStream>>(StringComparer.OrdinalIgnoreCase);
        // These delegates must remain rooted for as long as native code can invoke them, including worker threads.
        private readonly NativeMethods.FileSize _fileSize;
        private readonly NativeMethods.FileContent _fileContent;
        private readonly NativeMethods.SetFileCallbacks _setCallbacks;
        private readonly NativeMethods.InitOcr _initialize;
        private readonly NativeMethods.SetLightMode _setLightMode;
        private readonly NativeMethods.GetMaxDimension _getMaxDimension;
        private readonly NativeMethods.PerformOcr _performOcr;
        private readonly NativeMethods.FreeResult _freeResult;
        private Exception? _callbackFailure;
        private int _maximumDimension;

        internal override string DirectoryPath { get; }
        private bool _lightMode;
        internal override bool LightMode => _lightMode;
        internal override Version Version { get; }
        internal override bool IsVerifiedVersion { get; }
        internal override int MaximumDimension => _maximumDimension;

        internal WindowsNativeRuntime(string directory, bool lightMode, bool allowUnverifiedVersion)
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            DirectoryPath = directory;
            _directoryPrefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            _lightMode = lightMode;
            _fileSize = GetFileSize;
            _fileContent = GetFileContent;

            // Validate the complete OCR component before native initialization (missing native assets can abort).
            var fileList = Path.Combine(directory, "files_list_ocr.txt");
            if (!File.Exists(fileList))
                throw new FileNotFoundException("The full Screen AI component is required, including files_list_ocr.txt.", fileList);
            var count = 0;
            foreach (var entry in File.ReadAllLines(fileList))
            {
                var relativePath = entry.Trim();
                if (relativePath.Length == 0 || relativePath.StartsWith("#", StringComparison.Ordinal)) continue;
                var modelPath = ResolveModelPath(relativePath);
                var info = new FileInfo(modelPath);
                if (!info.Exists || info.Length == 0 || info.Length > MaxModelFileBytes)
                    throw new ScreenAiException("Missing, empty, or oversized OCR model/configuration file: " + modelPath);
                count++;
            }
            if (count == 0) throw new ScreenAiException("files_list_ocr.txt does not list any OCR assets.");

            _module = NativeMethods.Load(Path.Combine(directory, "chrome_screen_ai.dll"));
            try
            {
                NativeMethods.GetExport<NativeMethods.GetVersion>(_module, "GetLibraryVersion")(out var major, out var minor);
                Version = new Version(checked((int)major), checked((int)minor));
                // Only versions exercised by our Windows x64 smoke sample belong here.
                IsVerifiedVersion = Version == new Version(153, 2) || Version == new Version(153, 3);
                if (!IsVerifiedVersion && !allowUnverifiedVersion)
                    throw new NotSupportedException("Screen AI " + Version + " has an unverified native bitmap ABI. " +
                        "Use a verified component version. AllowUnverifiedNativeVersion is only for isolated experiments.");
                _setCallbacks = NativeMethods.GetExport<NativeMethods.SetFileCallbacks>(_module, "SetFileContentFunctions");
                _initialize = NativeMethods.GetExport<NativeMethods.InitOcr>(_module, "InitOCRUsingCallback");
                _setLightMode = NativeMethods.GetExport<NativeMethods.SetLightMode>(_module, "SetOCRLightMode");
                _getMaxDimension = NativeMethods.GetExport<NativeMethods.GetMaxDimension>(_module, "GetMaxImageDimension");
                _performOcr = NativeMethods.GetExport<NativeMethods.PerformOcr>(_module, "PerformOCR");
                _freeResult = NativeMethods.GetExport<NativeMethods.FreeResult>(_module, "FreeLibraryAllocatedCharArray");
            }
            catch
            {
                // Safe only before registering callbacks or starting the native runtime.
                NativeMethods.FreeLibrary(_module);
                throw;
            }
        }

        internal override void Initialize()
        {
            _setCallbacks(_fileSize, _fileContent);
            _setLightMode(LightMode);
            var initialized = _initialize();
            ThrowIfCallbackFailed();
            if (!initialized) throw new ScreenAiException("Screen AI OCR initialization failed. Check that models and DLL are from the same component.");
            _maximumDimension = checked((int)_getMaxDimension());
            if (MaximumDimension <= 0 || MaximumDimension > 32768)
                throw new ScreenAiException("Screen AI returned an invalid maximum image dimension.");
        }

        internal override byte[] Recognize(IntPtr bitmap)
        {
            ThrowIfCallbackFailed();
            var result = _performOcr(bitmap, out var size);
            try
            {
                ThrowIfCallbackFailed();
                if (result == IntPtr.Zero)
                    throw new ScreenAiException("PerformOCR returned a null result. This is an OCR failure, not an empty recognition.");
                if (size > MaxResultBytes) throw new ScreenAiException("Screen AI returned an oversized annotation.");
                var bytes = new byte[checked((int)size)];
                if (bytes.Length > 0) Marshal.Copy(result, bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                if (result != IntPtr.Zero) _freeResult(result);
            }
        }

        internal override void SetLightMode(bool enabled)
        {
            if (LightMode == enabled) return;
            ThrowIfCallbackFailed();
            _lightMode = enabled;
            // Changing the native mode invalidates its pipeline. Explicitly initialize it again.
            Initialize();
        }

        private string ResolveModelPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.IndexOf(':') >= 0)
                throw new ScreenAiException("The component requested an invalid relative model path.");
            var fullPath = Path.GetFullPath(Path.Combine(DirectoryPath, relativePath));
            if (!fullPath.StartsWith(_directoryPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ScreenAiException("The component requested a model outside its directory.");
            return fullPath;
        }

        private FileStream? OpenModel(IntPtr path)
        {
            var relativePath = Marshal.PtrToStringUTF8(path) ?? throw new ScreenAiException("The component requested a null model path.");
            var fullPath = ResolveModelPath(relativePath);
            if (!File.Exists(fullPath)) return null; // Native size probes may refer to optional files.
            return _models.GetOrAdd(fullPath, value => new Lazy<FileStream>(() =>
            {
                var stream = new FileStream(value, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxModelFileBytes)
                {
                    stream.Dispose();
                    throw new ScreenAiException("Requested model exceeds the size limit.");
                }
                return stream;
            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        private uint GetFileSize(IntPtr path)
        {
            try { return checked((uint)(OpenModel(path)?.Length ?? 0)); }
            catch (Exception error) { RecordCallbackFailure(error); return 0; }
        }

        private void GetFileContent(IntPtr path, uint size, IntPtr destination)
        {
            try
            {
                var stream = OpenModel(path) ?? throw new FileNotFoundException("A requested model is missing.");
                if (size < stream.Length || (destination == IntPtr.Zero && stream.Length != 0))
                    throw new ScreenAiException("The native model callback supplied an unexpected buffer.");
                var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    lock (stream)
                    {
                        stream.Position = 0;
                        int offset = 0;
                        while (offset < stream.Length)
                        {
                            var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, stream.Length - offset));
                            if (read == 0) throw new EndOfStreamException("Model file changed while reading.");
                            Marshal.Copy(chunk, 0, IntPtr.Add(destination, offset), read);
                            offset += read;
                        }
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(chunk); }
            }
            catch (Exception error) { RecordCallbackFailure(error); }
        }

        private void RecordCallbackFailure(Exception error) => Interlocked.CompareExchange(ref _callbackFailure, error, null);

        private void ThrowIfCallbackFailed()
        {
            var error = Volatile.Read(ref _callbackFailure);
            if (error != null) throw new ScreenAiException("A native model-file callback failed. Restart the OCR worker after correcting the component files.", error);
        }
    }
}
