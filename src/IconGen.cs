using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WshHelper
{
    // 程序图标：深色盾牌底 + 金色"W"符文，纯代码绘制，多尺寸打包为.ico
    public static class IconGen
    {
        static readonly Color BgTop = Color.FromArgb(255, 38, 52, 74);
        static readonly Color BgBottom = Color.FromArgb(255, 12, 18, 28);
        static readonly Color Edge = Color.FromArgb(255, 206, 166, 84);
        static readonly Color GoldHi = Color.FromArgb(255, 255, 226, 150);
        static readonly Color GoldLo = Color.FromArgb(255, 184, 130, 44);

        // W字形轮廓(0~1归一化坐标)
        static readonly float[,] WShape = new float[,]
        {
            {0.09f,0.24f},{0.235f,0.24f},{0.335f,0.585f},{0.435f,0.30f},
            {0.565f,0.30f},{0.665f,0.585f},{0.765f,0.24f},{0.91f,0.24f},
            {0.735f,0.79f},{0.595f,0.79f},{0.50f,0.525f},{0.405f,0.79f},
            {0.265f,0.79f}
        };

        public static Bitmap Render(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float pad = size * 0.045f;
                RectangleF box = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
                float radius = size * 0.24f;

                using (GraphicsPath bg = RoundedRect(box, radius))
                {
                    using (LinearGradientBrush lb = new LinearGradientBrush(
                        new RectangleF(box.X, box.Y - 1, box.Width, box.Height + 2),
                        BgTop, BgBottom, LinearGradientMode.Vertical))
                    {
                        g.FillPath(lb, bg);
                    }
                    // 顶部高光
                    using (GraphicsPath clip = RoundedRect(box, radius))
                    {
                        GraphicsState st = g.Save();
                        g.SetClip(clip);
                        using (LinearGradientBrush hl = new LinearGradientBrush(
                            new RectangleF(box.X, box.Y, box.Width, box.Height * 0.5f),
                            Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255),
                            LinearGradientMode.Vertical))
                        {
                            g.FillRectangle(hl, box.X, box.Y, box.Width, box.Height * 0.5f);
                        }
                        g.Restore(st);
                    }
                    float pen = Math.Max(1f, size * 0.045f);
                    using (Pen p = new Pen(Edge, pen))
                    {
                        p.Alignment = PenAlignment.Inset;
                        g.DrawPath(p, bg);
                    }
                }

                // 金色 W
                using (GraphicsPath w = new GraphicsPath())
                {
                    PointF[] pts = new PointF[WShape.GetLength(0)];
                    for (int i = 0; i < pts.Length; i++)
                        pts[i] = new PointF(WShape[i, 0] * size, WShape[i, 1] * size);
                    w.AddPolygon(pts);

                    using (LinearGradientBrush gb = new LinearGradientBrush(
                        new RectangleF(0, size * 0.22f, size, size * 0.58f),
                        GoldHi, GoldLo, LinearGradientMode.Vertical))
                    {
                        g.FillPath(gb, w);
                    }
                    if (size >= 32)
                    {
                        using (Pen p = new Pen(Color.FromArgb(150, 90, 58, 12), Math.Max(1f, size * 0.018f)))
                            g.DrawPath(p, w);
                    }
                }
            }
            return bmp;
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        // 组装标准 .ico：小尺寸用DIB(兼容性最好)，大尺寸用PNG(体积小)
        public static byte[] BuildIco()
        {
            List<byte[]> payloads = new List<byte[]>();
            List<bool> isPng = new List<bool>();
            foreach (int s in Sizes)
            {
                using (Bitmap b = Render(s))
                {
                    // 仅256尺寸用PNG(约定俗成)，其余用DIB —— Icon.ToBitmap 不支持PNG条目
                    if (s >= 256) { payloads.Add(EncodePng(b)); isPng.Add(true); }
                    else { payloads.Add(EncodeDib(b)); isPng.Add(false); }
                }
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((short)0);              // reserved
                w.Write((short)1);              // type = icon
                w.Write((short)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    int s = Sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0);           // palette
                    w.Write((byte)0);           // reserved
                    w.Write((short)1);          // planes
                    w.Write((short)32);         // bpp
                    w.Write(payloads[i].Length);
                    w.Write(offset);
                    offset += payloads[i].Length;
                }
                foreach (byte[] p in payloads) w.Write(p);
                return ms.ToArray();
            }
        }

        static byte[] EncodePng(Bitmap b)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // ICO内嵌DIB: BITMAPINFOHEADER(高度为2倍) + BGRA像素(自下而上) + AND掩码
        static byte[] EncodeDib(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            int maskStride = ((w + 31) / 32) * 4;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(40);            // biSize
                bw.Write(w);
                bw.Write(h * 2);         // XOR + AND
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(0);             // BI_RGB
                bw.Write(w * h * 4 + maskStride * h);
                bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

                BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[w * 4];
                    for (int y = h - 1; y >= 0; y--)
                    {
                        IntPtr src = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                        System.Runtime.InteropServices.Marshal.Copy(src, row, 0, row.Length);
                        bw.Write(row);
                    }
                }
                finally { b.UnlockBits(bd); }

                byte[] mask = new byte[maskStride * h];   // 全0 = 全部不透明，由alpha通道控制
                bw.Write(mask);
                return ms.ToArray();
            }
        }

        static Icon _cached;

        public static Icon AppIcon()
        {
            if (_cached == null)
            {
                using (MemoryStream ms = new MemoryStream(BuildIco()))
                    _cached = new Icon(ms);
            }
            return _cached;
        }

        public static Icon TrayIcon()
        {
            return new Icon(AppIcon(), 16, 16);
        }
    }
}
