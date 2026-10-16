using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace WidgetWindowDemo.Services
{
    /// <summary>What a GIF file declares about its animation, read from its block structure (no pixel decoding).</summary>
    public sealed class GifInspection
    {
        /// <summary>Null when the file can be displayed; otherwise a user-facing reason.</summary>
        public string? Error { get; init; }
        public bool IsValid => Error == null;

        public int FrameCount { get; init; }

        /// <summary>
        /// NETSCAPE2.0 loop value: null = the file has no loop block (plays once, as browsers do),
        /// 0 = infinite, n = finite (WpfAnimatedGif plays it n times in total).
        /// </summary>
        public int? LoopCount { get; init; }

        /// <summary>One loop, using the same delay rules as WpfAnimatedGif (0 → 100 ms).</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>The data ends early (no trailer, or a block cut short).</summary>
        public bool Truncated { get; init; }

        /// <summary>
        /// Frames with a 10 ms delay. Browsers slow these down to 100 ms; WpfAnimatedGif plays them as written,
        /// so the animation runs much faster than in a browser.
        /// </summary>
        public int TinyDelayFrames { get; init; }

        public int FramesOutsideCanvas { get; init; }
    }

    /// <summary>
    /// Reads the structure of a GIF to explain how it will play (loop count, frames, delays) and to detect
    /// damaged files before WpfAnimatedGif tries to animate them. Never modifies the file.
    /// </summary>
    public static class GifInspector
    {
        private static readonly ConcurrentDictionary<string, GifInspection> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static GifInspection Inspect(string path) => Cache.GetOrAdd(path, Read);

        public static void Evict(string path) => Cache.TryRemove(path, out _);

        private static GifInspection Read(string path)
        {
            byte[] d;
            try
            {
                d = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                return new GifInspection { Error = "No se puede leer el archivo: " + ex.Message };
            }

            if (d.Length < 13 || Encoding.ASCII.GetString(d, 0, 6) is not ("GIF89a" or "GIF87a"))
                return new GifInspection { Error = "No es un archivo GIF válido (la cabecera no es GIF87a/GIF89a)." };

            int canvasW = U16(d, 6), canvasH = U16(d, 8);
            if (canvasW == 0 || canvasH == 0)
                return new GifInspection { Error = "El lienzo del GIF tiene tamaño 0." };

            int p = 13;
            if ((d[10] & 0x80) != 0) p += 3 << ((d[10] & 7) + 1); // global color table

            bool truncated = p > d.Length;
            bool trailer = false;
            int frames = 0, tinyDelays = 0, outside = 0;
            int? loop = null;
            int pendingDelay = -1;
            double durationMs = 0;

            while (!truncated && p < d.Length)
            {
                byte block = d[p++];

                if (block == 0x21) // extension
                {
                    if (p >= d.Length) { truncated = true; break; }
                    byte label = d[p++];

                    if (label == 0xF9 && p + 4 < d.Length && d[p] >= 4) // graphic control: delay
                    {
                        pendingDelay = U16(d, p + 2);
                    }
                    else if (label == 0xFF && p < d.Length) // application: loop count
                    {
                        int size = d[p];
                        if (p + 1 + size > d.Length) { truncated = true; break; }
                        string app = Encoding.ASCII.GetString(d, p + 1, size);
                        p += 1 + size;

                        if (app is "NETSCAPE2.0" or "ANIMEXTS1.0" && p + 3 < d.Length && d[p] >= 3 && d[p + 1] == 1)
                            loop = U16(d, p + 2);
                    }

                    p = SkipSubBlocks(d, p, ref truncated);
                }
                else if (block == 0x2C) // image descriptor = one frame
                {
                    if (p + 9 > d.Length) { truncated = true; break; }
                    int x = U16(d, p), y = U16(d, p + 2), w = U16(d, p + 4), h = U16(d, p + 6);
                    byte flags = d[p + 8];
                    p += 9;
                    if ((flags & 0x80) != 0) p += 3 << ((flags & 7) + 1); // local color table
                    p++; // LZW minimum code size
                    if (p > d.Length) { truncated = true; break; }
                    p = SkipSubBlocks(d, p, ref truncated);

                    frames++;
                    if (x + w > canvasW || y + h > canvasH) outside++;
                    if (pendingDelay == 1) tinyDelays++;
                    durationMs += pendingDelay > 0 ? pendingDelay * 10 : 100;
                    pendingDelay = -1;
                }
                else if (block == 0x3B) // trailer
                {
                    trailer = true;
                    break;
                }
                else
                {
                    if (frames == 0)
                        return new GifInspection { Error = $"Estructura GIF dañada: bloque desconocido 0x{block:X2} en el byte {p - 1}." };
                    truncated = true; // unreadable data after valid frames: treat like a cut-off file
                    break;
                }
            }

            if (frames == 0)
                return new GifInspection { Error = "El archivo no contiene ningún fotograma (está vacío o dañado)." };

            return new GifInspection
            {
                FrameCount = frames,
                LoopCount = loop,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                Truncated = truncated || !trailer,
                TinyDelayFrames = tinyDelays,
                FramesOutsideCanvas = outside
            };
        }

        private static int SkipSubBlocks(byte[] d, int p, ref bool truncated)
        {
            while (p < d.Length)
            {
                int n = d[p++];
                if (n == 0) return p;
                p += n;
            }

            truncated = true;
            return d.Length;
        }

        private static int U16(byte[] d, int i) => d[i] | (d[i + 1] << 8);
    }
}
