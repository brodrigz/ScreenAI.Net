using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenAI.Workers
{
    internal static class WorkerProtocol
    {
        internal const int Version = 1;
        internal const byte Initialize = 1, Recognize = 2;
        internal const int MaximumFrameLength = 129 * 1024 * 1024;
        internal static byte[] Build(Action<BinaryWriter> write)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            write(writer);
            return stream.ToArray();
        }
        internal static BinaryReader Reader(byte[] frame) => new BinaryReader(new MemoryStream(frame, false), Encoding.UTF8);
        internal static async Task WriteAsync(Stream stream, byte[] frame, CancellationToken token)
        {
            if (frame.Length > MaximumFrameLength) throw new InvalidDataException("IPC frame exceeds limit.");
            var header = BitConverter.GetBytes(frame.Length);
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[4];
            await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
            int length = BitConverter.ToInt32(header, 0);
            if (length < 1 || length > MaximumFrameLength) throw new InvalidDataException("Invalid IPC frame length.");
            var frame = new byte[length];
            await ReadExactlyAsync(stream, frame, token).ConfigureAwait(false);
            return frame;
        }
        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("OCR worker disconnected.");
                offset += read;
            }
        }
        internal static void CheckSuccess(BinaryReader reader)
        {
            if (reader.ReadByte() != 0) throw new ScreenAiException("OCR worker: " + reader.ReadString());
        }
    }
}
