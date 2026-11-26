using System;
using System.Globalization;

namespace TabLink {
    // Device capabilities cross the USB connection; no model or serial allowlist.
    sealed class StreamProfile {
        public int Width, Height, Rate, Bits;
        public static StreamProfile Parse(string hello, string token) {
            if (hello == null || token == null || token.Length != 64) return null;
            string[] fields = hello.Split(' ');
            if (fields.Length != 6 || fields[0] != "TABLINK/5" || fields[1] != token) return null;
            int w, h, rate, bits;
            if (!int.TryParse(fields[2], out w) || !int.TryParse(fields[3], out h) ||
                !int.TryParse(fields[4], out rate) || !int.TryParse(fields[5], out bits)) return null;
            if (w < 640 || w > 4096 || h < 360 || h > 2160 || w < h || (w & 1) != 0 || (h & 1) != 0 ||
                (rate != 30 && rate != 60 && rate != 75 && rate != 90 && rate != 120) || (bits != 8 && bits != 10)) return null;
            return new StreamProfile { Width=w, Height=h, Rate=rate, Bits=bits };
        }
        public double Score(int width, int height, int rate) {
            if (width > Width || height > Height || rate > Rate || width < 640 || height < 360) return double.PositiveInfinity;
            double aspectError = Math.Abs((double)width/height - (double)Width/Height);
            // Prefer aspect ratio, then usable pixels, then refresh rate.
            return aspectError*1000 + (1-(double)width*height/(Width*Height))*100 + (Rate-rate)*0.1;
        }
    }
}
