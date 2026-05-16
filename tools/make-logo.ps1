# Renders an original logo for GameServerControl directly via GDI+ (System.Drawing).
# Output: logo.ico (multi-size) + logo.png (256px preview), in the path passed as -OutDir.

param(
    [string]$OutDir = 'C:\GameServerControl\Client'
)

Add-Type -AssemblyName System.Drawing

$code = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class LogoRender {
    public static byte[] RenderPng(int size) {
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            // Rounded square background with vertical gradient (dark teal -> near black)
            float corner = size * 0.18f;
            using (var path = new GraphicsPath()) {
                float d = corner * 2f;
                RectangleF r = new RectangleF(0, 0, size - 1, size - 1);
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                using (var lgb = new LinearGradientBrush(r, Color.FromArgb(255, 14, 28, 36), Color.FromArgb(255, 5, 8, 11), 90f)) {
                    g.FillPath(lgb, path);
                }
                // Subtle 1px neon edge
                if (size >= 32) {
                    using (var penEdge = new Pen(Color.FromArgb(95, 63, 255, 142), Math.Max(1f, size / 96f))) {
                        g.DrawPath(penEdge, path);
                    }
                }
            }

            // Geometry
            float cx = size / 2f;
            float cy = size / 2f + size * 0.04f;
            float radius = size * 0.27f;
            float strokeW = Math.Max(2f, size * 0.10f);

            // Outer glow for the power symbol (only at larger sizes)
            if (size >= 64) {
                for (int i = 4; i >= 1; i--) {
                    int alpha = 18 + i * 6;
                    using (var glow = new Pen(Color.FromArgb(alpha, 63, 255, 142), strokeW + i * (size / 80f))) {
                        glow.StartCap = LineCap.Round;
                        glow.EndCap = LineCap.Round;
                        RectangleF rect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
                        g.DrawArc(glow, rect, -60f, 300f);
                        float topY = cy - radius * 1.05f;
                        float botY = cy - radius * 0.28f;
                        g.DrawLine(glow, cx, topY, cx, botY);
                    }
                }
            }

            // Main power-symbol stroke
            using (var pen = new Pen(Color.FromArgb(255, 63, 255, 142), strokeW)) {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                RectangleF rect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
                // Arc opens at top (gap), drawn clockwise
                g.DrawArc(pen, rect, -60f, 300f);
                // Vertical bar through the gap
                float topY = cy - radius * 1.05f;
                float botY = cy - radius * 0.28f;
                g.DrawLine(pen, cx, topY, cx, botY);
            }

            // Bright highlight pinprick on the bar (gives it neon depth)
            if (size >= 48) {
                using (var brush = new SolidBrush(Color.FromArgb(220, 220, 255, 230))) {
                    float dotR = Math.Max(1f, size / 110f);
                    float yDot = cy - radius * 0.85f;
                    g.FillEllipse(brush, cx - dotR, yDot - dotR, dotR * 2, dotR * 2);
                }
            }

            // Three "server rack" horizontal bars below the power symbol (cyan)
            if (size >= 28) {
                float startY = cy + radius * 1.18f;
                float lineH = Math.Max(2f, size / 30f);
                float gap = lineH * 1.7f;
                float baseW = size * 0.45f;
                Color cyan = Color.FromArgb(210, 102, 217, 255);
                using (var brush = new SolidBrush(cyan)) {
                    for (int i = 0; i < 3; i++) {
                        float w = baseW * (1f - i * 0.14f);
                        using (var p = new GraphicsPath()) {
                            float r = lineH / 2f;
                            float left = cx - w / 2f;
                            float top = startY + i * gap;
                            float right = left + w;
                            float bot = top + lineH;
                            p.AddArc(left, top, lineH, lineH, 90, 180);
                            p.AddArc(right - lineH, top, lineH, lineH, 270, 180);
                            p.CloseFigure();
                            g.FillPath(brush, p);
                        }
                    }
                }
            }

            using (var ms = new MemoryStream()) {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    public static void WritePng(string path, int size) {
        File.WriteAllBytes(path, RenderPng(size));
    }

    public static void WriteIco(string path, int[] sizes) {
        byte[][] pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) pngs[i] = RenderPng(sizes[i]);

        using (var fs = new FileStream(path, FileMode.Create))
        using (var bw = new BinaryWriter(fs)) {
            bw.Write((ushort)0);                 // Reserved
            bw.Write((ushort)1);                 // Type: icon
            bw.Write((ushort)sizes.Length);      // Count

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++) {
                int s = sizes[i];
                bw.Write(s >= 256 ? (byte)0 : (byte)s); // width (0 = 256)
                bw.Write(s >= 256 ? (byte)0 : (byte)s); // height
                bw.Write((byte)0);   // ColorCount
                bw.Write((byte)0);   // Reserved
                bw.Write((ushort)1); // Planes
                bw.Write((ushort)32);// BitsPerPixel
                bw.Write((uint)pngs[i].Length);
                bw.Write((uint)offset);
                offset += pngs[i].Length;
            }
            for (int i = 0; i < sizes.Length; i++) bw.Write(pngs[i]);
        }
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp -ReferencedAssemblies @('System.Drawing')

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$icoPath = Join-Path $OutDir 'logo.ico'
$pngPath = Join-Path $OutDir 'logo.png'

[LogoRender]::WriteIco($icoPath, @(16, 24, 32, 48, 64, 128, 256))
[LogoRender]::WritePng($pngPath, 512)

"Wrote $icoPath ($([Math]::Round((Get-Item $icoPath).Length/1024,1)) KB)"
"Wrote $pngPath (512x512 preview)"
