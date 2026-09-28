using System;
using System.Collections.Generic;
using System.Linq;

namespace ScreenAI
{
    public enum OcrDirection { Unspecified = 0, LeftToRight = 1, RightToLeft = 2, TopToBottom = 3 }
    public enum OcrContentType
    {
        PrintedText = 0, HandwrittenText = 1, Image = 2, LineDrawing = 3,
        Separator = 4, UnreadableText = 5, Formula = 6, HandwrittenFormula = 7, Signature = 8
    }

    /// <summary>Coordinates in pixels of the supplied image. Angle is clockwise about the top-left corner.</summary>
    public sealed class OcrBounds
    {
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public float Angle { get; internal set; }
    }

    public sealed class OcrSymbol
    {
        public string Text { get; internal set; } = string.Empty;
        public OcrBounds? Bounds { get; internal set; }
        /// <summary>Native confidence when present; null means the field was absent from the response.</summary>
        public float? Confidence { get; internal set; }
    }

    public sealed class OcrWord
    {
        public string Text { get; internal set; } = string.Empty;
        public string Language { get; internal set; } = string.Empty;
        public OcrBounds? Bounds { get; internal set; }
        public OcrBounds? WhitespaceBounds { get; internal set; }
        public float? Confidence { get; internal set; }
        public OcrDirection Direction { get; internal set; }
        public OcrContentType ContentType { get; internal set; }
        public IReadOnlyList<OcrSymbol> Symbols { get; internal set; } = new OcrSymbol[0];
    }

    public sealed class OcrLine
    {
        public string Text { get; internal set; } = string.Empty;
        public string Language { get; internal set; } = string.Empty;
        public OcrBounds? Bounds { get; internal set; }
        public float? Confidence { get; internal set; }
        public int BlockId { get; internal set; }
        public int ParagraphId { get; internal set; }
        public OcrDirection Direction { get; internal set; }
        public OcrContentType ContentType { get; internal set; }
        public IReadOnlyList<OcrWord> Words { get; internal set; } = new OcrWord[0];
    }

    public sealed class OcrResult
    {
        internal OcrResult(int width, int height, IReadOnlyList<OcrLine> lines)
        {
            Width = width;
            Height = height;
            ProcessedWidth = width;
            ProcessedHeight = height;
            Lines = lines;
            Text = string.Join("\n", lines.Select(line => line.Text));
        }

        public int Width { get; }
        public int Height { get; }
        public int ProcessedWidth { get; internal set; }
        public int ProcessedHeight { get; internal set; }
        /// <summary>Native line order joined with LF; not a reconstructed table or guaranteed reading order.</summary>
        public string Text { get; }
        public IReadOnlyList<OcrLine> Lines { get; }

        internal OcrResult MapToOriginal(int width, int height)
        {
            if (width == Width && height == Height) return this;
            double sx = (double)width / Width, sy = (double)height / Height;
            foreach (var line in Lines)
            {
                Scale(line.Bounds, sx, sy);
                foreach (var word in line.Words)
                {
                    Scale(word.Bounds, sx, sy);
                    Scale(word.WhitespaceBounds, sx, sy);
                    foreach (var symbol in word.Symbols) Scale(symbol.Bounds, sx, sy);
                }
            }
            return new OcrResult(width, height, Lines) { ProcessedWidth = Width, ProcessedHeight = Height };
        }

        private static void Scale(OcrBounds? b, double sx, double sy)
        {
            if (b == null) return;
            b.X = (int)Math.Round(b.X * sx); b.Y = (int)Math.Round(b.Y * sy);
            var angle = b.Angle * Math.PI / 180;
            var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
            b.Width = (int)Math.Round(b.Width * Math.Sqrt(sx * sx * cosine * cosine + sy * sy * sine * sine));
            b.Height = (int)Math.Round(b.Height * Math.Sqrt(sx * sx * sine * sine + sy * sy * cosine * cosine));
            b.Angle = (float)(Math.Atan2(sy * sine, sx * cosine) * 180 / Math.PI);
        }
    }
}
