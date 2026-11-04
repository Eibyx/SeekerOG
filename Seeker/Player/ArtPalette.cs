using Android.Graphics;
using System;

namespace Seeker.Player
{
    /// <summary>
    /// Artwork-driven colours for the player (concept inspired by Namida's dynamic colours,
    /// implemented independently). Everything works on a tiny down-scaled copy of the artwork,
    /// so it costs well under a millisecond and needs no extra library (works on Android 6).
    /// </summary>
    public sealed class ArtPalette
    {
        public static readonly Color DefaultAccent = Color.ParseColor("#1B7FB5");

        /// <summary>Vivid colour for the play button, progress and active toggles.</summary>
        public Color Accent { get; private set; } = DefaultAccent;
        /// <summary>Dark, desaturated version used to tint the background.</summary>
        public Color Deep { get; private set; } = Color.ParseColor("#0A101C");
        /// <summary>Readable colour for content drawn on top of Accent.</summary>
        public Color OnAccent { get; private set; } = Color.White;

        public static ArtPalette FromBitmap(Bitmap? art)
        {
            var p = new ArtPalette();
            if (art == null || art.IsRecycled)
            {
                return p;
            }
            Bitmap small;
            try
            {
                small = Bitmap.CreateScaledBitmap(art, 24, 24, true);
            }
            catch (Exception)
            {
                return p;
            }
            // Hue histogram weighted by saturation * value: picks the most "colourful" dominant hue,
            // falling back to the average colour for greyscale covers.
            var hueWeight = new double[36];
            var hueSat = new double[36];
            var hueVal = new double[36];
            double r = 0, g = 0, b = 0;
            int n = 0;
            float[] hsv = new float[3];
            for (int y = 0; y < small.Height; y++)
            {
                for (int x = 0; x < small.Width; x++)
                {
                    int c = small.GetPixel(x, y);
                    int cr = (c >> 16) & 0xff, cg = (c >> 8) & 0xff, cb = c & 0xff;
                    r += cr; g += cg; b += cb; n++;
                    Color.RGBToHSV(cr, cg, cb, hsv);
                    if (hsv[1] < 0.18f || hsv[2] < 0.15f)
                    {
                        continue;
                    }
                    int bucket = Math.Min(35, (int)(hsv[0] / 10f));
                    double w = hsv[1] * hsv[2];
                    hueWeight[bucket] += w;
                    hueSat[bucket] += hsv[1] * w;
                    hueVal[bucket] += hsv[2] * w;
                }
            }
            if (!ReferenceEquals(small, art))
            {
                small.Recycle();
            }
            int best = -1;
            double bestW = 0;
            for (int i = 0; i < 36; i++)
            {
                if (hueWeight[i] > bestW)
                {
                    bestW = hueWeight[i];
                    best = i;
                }
            }
            if (best >= 0 && bestW > 2.0)
            {
                float hue = best * 10f + 5f;
                float sat = (float)Math.Min(0.85, Math.Max(0.45, hueSat[best] / bestW));
                float val = (float)Math.Min(0.92, Math.Max(0.62, hueVal[best] / bestW));
                p.Accent = Color.HSVToColor(new[] { hue, sat, val });
                p.Deep = Color.HSVToColor(new[] { hue, Math.Min(0.7f, sat), 0.16f });
            }
            else if (n > 0)
            {
                var avg = Color.Rgb((int)(r / n), (int)(g / n), (int)(b / n));
                Color.RGBToHSV(avg.R, avg.G, avg.B, hsv);
                p.Accent = Color.HSVToColor(new[] { hsv[0], Math.Min(0.25f, hsv[1]), 0.75f });
                p.Deep = Color.HSVToColor(new[] { hsv[0], Math.Min(0.25f, hsv[1]), 0.14f });
            }
            // luminance check for the icon on the play button
            double lum = (0.299 * p.Accent.R + 0.587 * p.Accent.G + 0.114 * p.Accent.B) / 255.0;
            p.OnAccent = lum > 0.7 ? Color.ParseColor("#101418") : Color.White;
            return p;
        }

        /// <summary>
        /// Cheap "blur": the artwork shrunk to a few pixels and drawn back with bilinear filtering.
        /// On Android 12+ the view additionally gets a real RenderEffect blur.
        /// </summary>
        public static Bitmap? BackdropFrom(Bitmap? art)
        {
            if (art == null || art.IsRecycled)
            {
                return null;
            }
            try
            {
                var tiny = Bitmap.CreateScaledBitmap(art, 12, 12, true);
                var soft = Bitmap.CreateScaledBitmap(tiny, 96, 96, true);
                if (!ReferenceEquals(tiny, art) && !ReferenceEquals(tiny, soft))
                {
                    tiny.Recycle();
                }
                return soft;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Color WithAlpha(Color c, int alpha) => Color.Argb(alpha, c.R, c.G, c.B);
    }
}
