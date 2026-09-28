using System.Collections.Generic;
using System.IO;
using Google.Protobuf;

namespace ScreenAI.Parsing
{
    /// <summary>
    /// Public-result projection of Chromium's VisualAnnotation wire schema.
    /// Exact wire tags are matched (not only field numbers); unknown fields remain forward-compatible.
    /// Uses Google's bounded protobuf reader instead of hand-implementing varints and wire validation.
    /// Source: services/screen_ai/proto/chrome_screen_ai.proto; see THIRD-PARTY-NOTICES.md.
    /// </summary>
    internal static class AnnotationParser
    {
        internal static OcrResult Parse(byte[] bytes, int width, int height)
        {
            try
            {
                using var input = new CodedInputStream(bytes);
                var lines = new List<OcrLine>();
                uint tag;
                while ((tag = input.ReadTag()) != 0)
                {
                    if (tag == 18) lines.Add(ReadLine(input.ReadBytes()));
                    else input.SkipLastField();
                }
                return new OcrResult(width, height, lines.AsReadOnly());
            }
            catch (IOException error)
            {
                throw new ScreenAiException("Screen AI returned an invalid VisualAnnotation protobuf.", error);
            }
        }

        private static OcrLine ReadLine(ByteString bytes)
        {
            using var input = bytes.CreateCodedInput();
            var line = new OcrLine();
            var words = new List<OcrWord>();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 10: words.Add(ReadWord(input.ReadBytes())); break;
                    case 18: line.Bounds = ReadBounds(input.ReadBytes()); break;
                    case 26: line.Text = input.ReadString(); break;
                    case 34: line.Language = input.ReadString(); break;
                    case 40: line.BlockId = input.ReadInt32(); break;
                    case 56: line.Direction = (OcrDirection)input.ReadEnum(); break;
                    case 64: line.ContentType = (OcrContentType)input.ReadEnum(); break;
                    case 85: line.Confidence = input.ReadFloat(); break;
                    case 88: line.ParagraphId = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            line.Words = words.AsReadOnly();
            return line;
        }

        private static OcrWord ReadWord(ByteString bytes)
        {
            using var input = bytes.CreateCodedInput();
            var word = new OcrWord();
            var symbols = new List<OcrSymbol>();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 10: symbols.Add(ReadSymbol(input.ReadBytes())); break;
                    case 18: word.Bounds = ReadBounds(input.ReadBytes()); break;
                    case 26: word.Text = input.ReadString(); break;
                    case 42: word.Language = input.ReadString(); break;
                    case 96: word.Direction = (OcrDirection)input.ReadEnum(); break;
                    case 104: word.ContentType = (OcrContentType)input.ReadEnum(); break;
                    case 125: word.Confidence = input.ReadFloat(); break;
                    case 138: word.WhitespaceBounds = ReadBounds(input.ReadBytes()); break;
                    default: input.SkipLastField(); break;
                }
            }
            word.Symbols = symbols.AsReadOnly();
            return word;
        }

        private static OcrSymbol ReadSymbol(ByteString bytes)
        {
            using var input = bytes.CreateCodedInput();
            var symbol = new OcrSymbol();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 10: symbol.Bounds = ReadBounds(input.ReadBytes()); break;
                    case 18: symbol.Text = input.ReadString(); break;
                    case 29: symbol.Confidence = input.ReadFloat(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return symbol;
        }

        private static OcrBounds ReadBounds(ByteString bytes)
        {
            using var input = bytes.CreateCodedInput();
            var bounds = new OcrBounds();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 8: bounds.X = input.ReadInt32(); break;
                    case 16: bounds.Y = input.ReadInt32(); break;
                    case 24: bounds.Width = input.ReadInt32(); break;
                    case 32: bounds.Height = input.ReadInt32(); break;
                    case 45: bounds.Angle = input.ReadFloat(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return bounds;
        }
    }
}
