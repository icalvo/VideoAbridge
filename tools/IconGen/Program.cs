using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

const int Size = 512;
var outPng = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "VideoAbridge", "Assets", "VideoAbridge.png"));
var outIco = Path.ChangeExtension(outPng, ".ico");

Directory.CreateDirectory(Path.GetDirectoryName(outPng)!);

using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(bmp))
{
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.FromArgb(255, 30, 30, 30));

    // Scissors first (large); film borders drawn last so sprockets stay readable.
    DrawScissors(g, Size);
    DrawFilmBorders(g, Size);
}

bmp.Save(outPng, ImageFormat.Png);
WriteIco(bmp, outIco, [256, 128, 64, 48, 32, 16]);
Console.WriteLine($"Wrote {outPng}");
Console.WriteLine($"Wrote {outIco}");

static void DrawFilmBorders(Graphics g, int size)
{
    var stripH = Math.Max(18, size / 22);
    using var white = new SolidBrush(Color.FromArgb(255, 236, 236, 236));
    using var hole = new SolidBrush(Color.FromArgb(255, 30, 30, 30));

    g.FillRectangle(white, 0, 0, size, stripH);
    g.FillRectangle(white, 0, size - stripH, size, stripH);

    var holeW = Math.Max(8, stripH / 2);
    var holeH = Math.Max(10, stripH - 10);
    var gap = holeW + Math.Max(10, size / 28);
    var yTop = (stripH - holeH) / 2;
    var yBot = size - stripH + (stripH - holeH) / 2;
    for (var x = gap / 2; x + holeW < size; x += gap)
    {
        g.FillRectangle(hole, x, yTop, holeW, holeH);
        g.FillRectangle(hole, x, yBot, holeW, holeH);
    }
}

static void DrawScissors(Graphics g, int size)
{
    var state = g.Save();

    float cx = size / 2f;
    float cy = size / 2f;
    g.TranslateTransform(cx, cy);
    g.RotateTransform(-42f);
    // Fill nearly the full square; borders will mask the very top/bottom.
    var scale = (size - 4f) / 280f;
    g.ScaleTransform(scale, scale);

    var blue = Color.FromArgb(255, 59, 130, 246);
    var blueDark = Color.FromArgb(255, 37, 99, 210);
    using var brush = new SolidBrush(blue);
    using var brushDark = new SolidBrush(blueDark);
    using var pivot = new SolidBrush(Color.FromArgb(255, 45, 45, 48));
    using var bg = new SolidBrush(Color.FromArgb(255, 30, 30, 30));

    // Handle loops — large and close together for more blue mass
    g.FillEllipse(brush, -102, 62, 100, 100);
    g.FillEllipse(brush, 2, 62, 100, 100);
    g.FillEllipse(bg, -78, 86, 52, 52);
    g.FillEllipse(bg, 26, 86, 52, 52);

    // Thick shanks
    using (var path = new GraphicsPath())
    {
        path.AddPolygon(new[]
        {
            new Point(-46, 80), new Point(-4, 80), new Point(0, 4), new Point(-40, 4)
        });
        g.FillPath(brush, path);
    }

    using (var path = new GraphicsPath())
    {
        path.AddPolygon(new[]
        {
            new Point(4, 80), new Point(46, 80), new Point(40, 4), new Point(0, 4)
        });
        g.FillPath(brush, path);
    }

    // Slightly less open blades = denser silhouette covering more of the square
    using (var path = new GraphicsPath())
    {
        path.AddPolygon(new[]
        {
            new Point(-42, 2), new Point(2, 2), new Point(-48, -168), new Point(-92, -150)
        });
        g.FillPath(brush, path);
    }

    using (var path = new GraphicsPath())
    {
        path.AddPolygon(new[]
        {
            new Point(-2, 2), new Point(42, 2), new Point(92, -150), new Point(48, -168)
        });
        g.FillPath(brushDark, path);
    }

    // Pivot
    g.FillEllipse(pivot, -20, -12, 40, 40);
    g.FillEllipse(brush, -11, -3, 22, 22);

    g.Restore(state);
}

static void WriteIco(Bitmap source, string path, int[] sizes)
{
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);

    var images = new List<byte[]>();
    foreach (var s in sizes)
    {
        using var resized = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, s, s);
        }

        using var ms = new MemoryStream();
        resized.Save(ms, ImageFormat.Png);
        images.Add(ms.ToArray());
    }

    // ICO header
    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)images.Count);

    var offset = 6 + 16 * images.Count;
    for (var i = 0; i < images.Count; i++)
    {
        var s = sizes[i];
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(images[i].Length);
        bw.Write(offset);
        offset += images[i].Length;
    }

    foreach (var png in images)
        bw.Write(png);
}
