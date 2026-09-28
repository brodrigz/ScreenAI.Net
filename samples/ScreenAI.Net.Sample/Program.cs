using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using ScreenAI;

// This executable keeps experimental native calls out of a long-running application's process.
// No PDF support: the library operates on caller-decoded pixels, and this sample decodes common images.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0].StartsWith("ScreenAI.Net.", StringComparison.Ordinal))
            return WorkerSmoke.RunFixture(args).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--parent-probe")
            return WorkerSmoke.RunParentProbe(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length < 2 || args[0] == "--help")
        {
            Console.Error.WriteLine("Usage: ScreenAI.Net.Sample <component-directory> <image-path|--smoke> [--allow-unverified-version] [--light]");
            Console.Error.WriteLine("       ScreenAI.Net.Sample <component-directory> --worker-smoke <worker-host-path>");
            return args.Length == 1 && args[0] == "--help" ? 0 : 2;
        }
        try
        {
            if (args.Length == 3 && args[1] == "--worker-smoke")
                return WorkerSmoke.Run(args[0], args[2]).GetAwaiter().GetResult();
            var allowed = new[] { "--allow-unverified-version", "--light" };
            if (args.Skip(2).Any(argument => !allowed.Contains(argument))) throw new ArgumentException("Unknown option.");
            var options = new ScreenAiOptions(args[0])
            {
                AllowUnverifiedNativeVersion = args.Contains("--allow-unverified-version"),
                UseLightMode = args.Contains("--light")
            };
            var engine = new ScreenAiEngine(options);
            Console.Error.WriteLine($"Screen AI {engine.NativeVersion}; verified version: {engine.IsVerifiedNativeVersion}; maximum dimension: {engine.MaximumImageDimension}");
            if (args[1] == "--smoke") return Smoke(engine, options);

            using var source = new Bitmap(args[1]);
            using var image = Flatten(source);
            var pixels = ReadPixels(image);
            var result = engine.RecognizeBgra(pixels, image.Width, image.Height);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int Smoke(ScreenAiEngine engine, ScreenAiOptions options)
    {
        using var bitmap = new Bitmap(1200, 360, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 36, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("SCREEN AI 12345", font, Brushes.Black, 30, 30);
            graphics.DrawString("PN ABC-123 QTY 25", font, Brushes.Black, 30, 120);
            graphics.DrawString("NET 6.30 KG GROSS 7.30 KG", font, Brushes.Black, 30, 210);
        }
        var pixels = ReadPixels(bitmap);
        var first = engine.RecognizeBgra(pixels, bitmap.Width, bitmap.Height);
        Console.WriteLine(first.Text);
        foreach (var expected in new[] { "12345", "ABC-123", "25", "6.30", "7.30" })
            Require(first.Text.Contains(expected, StringComparison.Ordinal), "OCR missed " + expected);
        Require(first.Lines.SelectMany(line => line.Words).Any(word => word.Bounds?.Width > 0), "No word coordinates.");
        Require(first.Lines.SelectMany(line => line.Words).Any(word => word.Confidence.HasValue), "No word confidences.");
        Require(first.Lines.SelectMany(line => line.Words).SelectMany(word => word.Symbols).Any(), "No symbol annotations.");

        // Repeated calls across forced GC exercise callback/buffer lifetime, not merely a successful first call.
        var secondEngine = new ScreenAiEngine(options);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Parallel.For(0, 3, _ => Require(secondEngine.RecognizeBgra(pixels, bitmap.Width, bitmap.Height).Text == first.Text,
            "Repeated/serialized OCR result changed."));

        var paddedStride = bitmap.Width * 4 + 16;
        var padded = new byte[paddedStride * bitmap.Height];
        for (var row = 0; row < bitmap.Height; row++)
            Buffer.BlockCopy(pixels, row * bitmap.Width * 4, padded, row * paddedStride, bitmap.Width * 4);
        var paddedResult = engine.RecognizeBgra(padded, bitmap.Width, bitmap.Height, paddedStride);
        foreach (var expected in new[] { "12345", "ABC-123", "25", "6.30", "7.30" })
            Require(paddedResult.Text.Contains(expected, StringComparison.Ordinal), "Padded stride OCR missed " + expected);

        Expect<ArgumentException>(() => engine.RecognizeBgra(new byte[1], 100, 100));
        Expect<ArgumentOutOfRangeException>(() => engine.RecognizeBgra(pixels, 0, bitmap.Height));
        Expect<ArgumentOutOfRangeException>(() => engine.RecognizeBgra(pixels, engine.MaximumImageDimension + 1, 1));
        Expect<ArgumentOutOfRangeException>(() => engine.RecognizeBgra(pixels, bitmap.Width, bitmap.Height, -1));
        var alternate = new ScreenAiEngine(new ScreenAiOptions(options.ComponentDirectory)
        {
            UseLightMode = !options.UseLightMode,
            AllowUnverifiedNativeVersion = options.AllowUnverifiedNativeVersion
        });
        Require(alternate.RecognizeBgra(pixels, bitmap.Width, bitmap.Height).Text.Contains("12345"), "Mode switch failed.");
        Require(engine.RecognizeBgra(pixels, bitmap.Width, bitmap.Height).Text == first.Text, "Restoring default mode failed.");

        using var blank = new Bitmap(400, 200, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.White);
        var empty = engine.RecognizeBgra(ReadPixels(blank), blank.Width, blank.Height);
        Require(string.IsNullOrWhiteSpace(empty.Text), "Blank image unexpectedly returned text.");
        Console.WriteLine("PASS: exact sample tokens, word boxes/confidence, symbols, repeated calls/GC, concurrent callers, padded stride, invalid inputs, mode switching, and blank image.");
        return 0;
    }

    private static Bitmap Flatten(Bitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        return bitmap;
    }

    internal static byte[] ReadPixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = checked(bitmap.Width * 4);
            var pixels = new byte[checked(stride * bitmap.Height)];
            for (var row = 0; row < bitmap.Height; row++)
                Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * stride, stride);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
