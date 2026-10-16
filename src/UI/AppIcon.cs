using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SignalWatchdogs.UI
{
    /// <summary>Icône dessinée par code (écran + tracé de surveillance), exportable en .ico multi-tailles.</summary>
    public static class AppIcon
    {
        public static Bitmap Render(int s, bool alert = false)
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var rect = new RectangleF(0.5f, 0.5f, s - 1f, s - 1f);
            using (var path = Theme.RoundRect(rect, s * 0.22f))
            using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s),
                       alert ? Color.FromArgb(0xFF, 0x4D, 0x4D) : Color.FromArgb(0x2B, 0x6B, 0xFF),
                       alert ? Color.FromArgb(0xC0, 0x1F, 0x5A) : Color.FromArgb(0x7B, 0x3F, 0xF2)))
                g.FillPath(brush, path);

            float w = Math.Max(1.2f, s * 0.065f);
            using var pen = new Pen(Color.White, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            var screen = new RectangleF(s * 0.17f, s * 0.22f, s * 0.66f, s * 0.44f);
            using (var sp = Theme.RoundRect(screen, s * 0.05f)) g.DrawPath(pen, sp);
            if (s >= 24)
            {
                g.DrawLine(pen, s * 0.5f, s * 0.66f, s * 0.5f, s * 0.76f);
                g.DrawLine(pen, s * 0.36f, s * 0.78f, s * 0.64f, s * 0.78f);
            }
            using var pulse = new Pen(Color.FromArgb(0x7F, 0xF5, 0xFF), Math.Max(1.2f, s * 0.055f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pulse, new[]
            {
                new PointF(s * 0.24f, s * 0.46f), new PointF(s * 0.37f, s * 0.46f), new PointF(s * 0.43f, s * 0.33f),
                new PointF(s * 0.51f, s * 0.58f), new PointF(s * 0.57f, s * 0.41f), new PointF(s * 0.62f, s * 0.46f),
                new PointF(s * 0.76f, s * 0.46f)
            });
            return bmp;
        }

        public static byte[] BuildIco(int[] sizes, bool alert = false)
        {
            var images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using var bmp = Render(sizes[i], alert);
                images[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);
            }
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
            return ms.ToArray();
        }

        static byte[] Png(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        static byte[] Dib(Bitmap bmp)
        {
            int s = bmp.Width;
            int maskStride = ((s + 31) / 32) * 4;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[s * 4];
                for (int y = s - 1; y >= 0; y--)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    w.Write(row);
                }
            }
            finally { bmp.UnlockBits(data); }
            w.Write(new byte[maskStride * s]);
            return ms.ToArray();
        }

        public static Icon Create(bool alert = false) => new Icon(new MemoryStream(BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, alert)));

        public static Icon CreateSmall(bool alert = false) => new Icon(new MemoryStream(BuildIco(new[] { 16, 20, 24, 32, 40, 48 }, alert)), SystemInformation.SmallIconSize);

        public static void WriteIco(string path) => File.WriteAllBytes(path, BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }));
    }
}
