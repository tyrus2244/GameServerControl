# Renders the GameServerControl logo via GDI+ (System.Drawing).
# Output: logo.ico (multi-size) + logo.png (512px preview), in the path passed as -OutDir.
#
# Design — black + red, original geometric mark. Composition:
#   - Rounded-square background, near-black with a subtle vertical red->black gradient
#   - Thin red bevel ring around the outer edge
#   - Central red angular "play" triangle (gaming) sitting on three stacked red bars
#     (server rack) — together = "Game Server Control"
#   - Sized to read clearly at 16x16 (the taskbar/shortcut overlay size)

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
    // Brand colors
    static readonly Color BgTop    = Color.FromArgb(255,  28,  6,  8);   // deep red-tinted black at top
    static readonly Color BgBot    = Color.FromArgb(255,   6,  3,  4);   // near-pure black at bottom
    static readonly Color Crimson  = Color.FromArgb(255, 220, 30, 45);   // primary red — vivid but not neon
    static readonly Color CrimsonDim = Color.FromArgb(255, 140, 18, 26); // shadow red for depth
    static readonly Color RimRed   = Color.FromArgb(180, 220, 30, 45);   // partially transparent for the ring
    static readonly Color Hilite   = Color.FromArgb(220, 255, 220, 220); // bright pin-prick

    public static byte[] RenderPng(int size) {
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode      = SmoothingMode.AntiAlias;
            g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            // ---- Rounded-square background with vertical gradient ----
            float corner = size * 0.18f;
            using (var path = MakeRoundRect(0, 0, size - 1, size - 1, corner)) {
                using (var lgb = new LinearGradientBrush(new RectangleF(0, 0, size, size), BgTop, BgBot, 90f)) {
                    g.FillPath(lgb, path);
                }
                // Outer red bevel — keeps the silhouette visible on dark backgrounds
                if (size >= 32) {
                    using (var pen = new Pen(RimRed, Math.Max(1f, size / 96f))) {
                        g.DrawPath(pen, path);
                    }
                }
            }

            // ---- Center "play" triangle ----
            // Triangle is slightly above center; its baseline sits over the rack stack.
            float cx = size / 2f;
            float cy = size / 2f - size * 0.05f;
            float triH = size * 0.42f;          // total triangle height
            float triW = triH * 0.95f;          // visual width (pointed isoceles)
            float yTop = cy - triH * 0.55f;
            float yBot = cy + triH * 0.45f;
            float xLeft = cx - triW * 0.45f;
            float xRight = cx + triW * 0.55f;

            using (var tri = new GraphicsPath()) {
                tri.AddPolygon(new PointF[] {
                    new PointF(xLeft, yTop),
                    new PointF(xRight, cy),
                    new PointF(xLeft, yBot)
                });
                // Shadow (drop down-right, only at larger sizes)
                if (size >= 48) {
                    using (var shadow = new GraphicsPath()) {
                        float dx = size * 0.012f;
                        shadow.AddPolygon(new PointF[] {
                            new PointF(xLeft + dx, yTop + dx),
                            new PointF(xRight + dx, cy + dx),
                            new PointF(xLeft + dx, yBot + dx)
                        });
                        using (var br = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillPath(br, shadow);
                    }
                }
                // Triangle gradient: crimson at top, darker red at bottom for depth
                using (var br = new LinearGradientBrush(
                    new RectangleF(xLeft, yTop, xRight - xLeft, yBot - yTop),
                    Crimson, CrimsonDim, 90f)) {
                    g.FillPath(br, tri);
                }
                // Thin black inner border so it reads as solid even on dark bg
                if (size >= 24) {
                    using (var pen = new Pen(Color.FromArgb(180, 0, 0, 0), Math.Max(1f, size / 128f))) {
                        g.DrawPath(pen, tri);
                    }
                }
            }

            // Highlight pin on triangle top-left edge — gives it 3D
            if (size >= 64) {
                using (var br = new SolidBrush(Hilite)) {
                    float r = size * 0.012f;
                    float hx = xLeft + (xRight - xLeft) * 0.18f;
                    float hy = yTop + (yBot - yTop) * 0.22f;
                    g.FillEllipse(br, hx - r, hy - r, r * 2, r * 2);
                }
            }

            // ---- Server-rack: three horizontal red bars under the triangle ----
            // Bars taper inward, evoking a stack of servers.
            if (size >= 20) {
                float startY = yBot + size * 0.05f;
                float lineH  = Math.Max(2f, size / 26f);
                float gap    = lineH * 1.55f;
                float baseW  = size * 0.50f;
                for (int i = 0; i < 3; i++) {
                    float w     = baseW * (1f - i * 0.18f);
                    float left  = cx - w / 2f;
                    float top   = startY + i * gap;
                    using (var p = MakePill(left, top, w, lineH)) {
                        using (var br = new SolidBrush(
                                    i == 0 ? Crimson :
                                    i == 1 ? Color.FromArgb(220, 200, 25, 38) :
                                             Color.FromArgb(200, 170, 20, 30))) {
                            g.FillPath(br, p);
                        }
                    }
                }
            }

            // ---- Tiny LED dot to the right of the top bar — implies "online" ----
            if (size >= 48) {
                float dotR = Math.Max(1f, size / 60f);
                float dotX = cx + (size * 0.50f) / 2f + size * 0.05f;
                float dotY = yBot + size * 0.05f + Math.Max(2f, size / 26f) / 2f;
                using (var br = new SolidBrush(Color.FromArgb(230, 255, 90, 100))) {
                    g.FillEllipse(br, dotX - dotR, dotY - dotR, dotR * 2, dotR * 2);
                }
            }

            using (var ms = new MemoryStream()) {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static GraphicsPath MakeRoundRect(float x, float y, float w, float h, float r) {
        var p = new GraphicsPath();
        float d = r * 2f;
        p.AddArc(x,         y,         d, d, 180, 90);
        p.AddArc(x + w - d, y,         d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0,   90);
        p.AddArc(x,         y + h - d, d, d, 90,  90);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath MakePill(float x, float y, float w, float h) {
        var p = new GraphicsPath();
        p.AddArc(x,         y, h, h, 90,  180);
        p.AddArc(x + w - h, y, h, h, 270, 180);
        p.CloseFigure();
        return p;
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
