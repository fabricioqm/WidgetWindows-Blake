using System.IO;

namespace WidgetWindowDemo.Services
{
    /// <summary>
    /// Structural checks for PNG, JPEG and BMP. Windows' decoders are tolerant: a file cut in half still "decodes"
    /// (the missing part is filled in), so truncation and corruption are detected here from the file layout.
    /// </summary>
    internal static class ImageStructure
    {
        /// <summary>Null when the structure is sound (or the format isn't checked here); otherwise the reason.</summary>
        public static string? Check(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => CheckPng(d),
                ".jpg" or ".jpeg" => CheckJpeg(d),
                ".bmp" => CheckBmp(d),
                _ => null
            };
        }

        private const string Truncated = "La imagen está incompleta o truncada (le faltan datos al final).";

        private static string? CheckPng(byte[] d)
        {
            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (d.Length < 8 || !d.AsSpan(0, 8).SequenceEqual(signature))
                return "No es un archivo PNG válido (firma incorrecta).";

            long p = 8;
            while (p + 12 <= d.Length)
            {
                long length = BigEndian32(d, p);
                long end = p + 12 + length;
                if (length > int.MaxValue || end > d.Length) return Truncated;

                uint stored = (uint)BigEndian32(d, end - 4);
                uint actual = Crc32(d, (int)p + 4, (int)length + 4); // type + data
                if (stored != actual)
                    return "La imagen está dañada (un bloque PNG no supera la comprobación CRC).";

                if (d[p + 4] == 'I' && d[p + 5] == 'E' && d[p + 6] == 'N' && d[p + 7] == 'D')
                    return null;

                p = end;
            }

            return Truncated;
        }

        private static string? CheckJpeg(byte[] d)
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
                return "No es un archivo JPEG válido (falta el marcador de inicio).";

            int p = 2;
            while (p < d.Length)
            {
                if (d[p] != 0xFF) return "La estructura del JPEG está dañada.";
                while (p < d.Length && d[p] == 0xFF) p++; // fill bytes
                if (p >= d.Length) return Truncated;

                byte marker = d[p++];
                if (marker == 0xD9) return null;                          // EOI: complete image
                if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;      // markers without length

                if (p + 2 > d.Length) return Truncated;
                int segmentLength = (d[p] << 8) | d[p + 1];
                if (segmentLength < 2) return "La estructura del JPEG está dañada.";
                p += segmentLength;
                if (p > d.Length) return Truncated;

                if (marker == 0xDA) // start of scan: entropy-coded data runs until the next real marker
                {
                    while (true)
                    {
                        if (p + 1 >= d.Length) return Truncated;
                        if (d[p] != 0xFF) { p++; continue; }

                        byte next = d[p + 1];
                        if (next == 0x00 || next is >= 0xD0 and <= 0xD7) p += 2; // stuffed byte / restart marker
                        else if (next == 0xFF) p++;                              // fill byte
                        else break;                                               // a marker: back to segments
                    }
                }
            }

            return Truncated;
        }

        private static string? CheckBmp(byte[] d)
        {
            if (d.Length < 14 || d[0] != 'B' || d[1] != 'M')
                return "No es un archivo BMP válido (firma incorrecta).";

            long declared = BitConverter.ToUInt32(d, 2); // bfSize, little-endian
            return declared > 0 && d.Length < declared ? Truncated : null;
        }

        private static long BigEndian32(byte[] d, long i) =>
            ((long)d[i] << 24) | ((long)d[i + 1] << 16) | ((long)d[i + 2] << 8) | d[i + 3];

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] d, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                c = CrcTable[(c ^ d[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
