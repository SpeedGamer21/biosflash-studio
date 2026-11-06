using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BiosFlashStudio.Analysis;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio.Controls;

internal static class Res
{
    public static FormattedText T(string s, double size, IBrush b, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, b);

    public static readonly Color[] RegionColors =
    {
        Color.FromRgb(0x7C, 0x8C, 0xFF), Color.FromRgb(0x3D, 0xDC, 0x97), Color.FromRgb(0x6C, 0xB4, 0xFF), Color.FromRgb(0xF7, 0xB9, 0x55),
        Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0xFF, 0x6B, 0x7A),
        Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0x81, 0x8C, 0xF8), Color.FromRgb(0xFA, 0xCC, 0x15),
        Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x94, 0xA3, 0xB8), Color.FromRgb(0x94, 0xA3, 0xB8), Color.FromRgb(0xFB, 0x92, 0x3C),
    };
}

/// <summary>Proportional bar of flash descriptor regions.</summary>
public sealed class RegionBar : Control
{
    List<FlashRegion> _regions = new();
    long _size;

    public RegionBar() { ActualThemeVariantChanged += (_, _) => InvalidateVisual(); }

    public void SetRegions(List<FlashRegion> regions, long size) { _regions = regions ?? new(); _size = size; InvalidateVisual(); }

    protected override Size MeasureOverride(Size a) => new(double.IsInfinity(a.Width) ? 400 : a.Width, 50);

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var dim = Palette.Br("TxtDim");
        dc.DrawRectangle(Palette.Br("InputBg"), new Pen(Palette.Br("Line"), 1), new Rect(0.5, 0.5, w - 1, 30), 8, 8);
        if (_size <= 0 || _regions.Count == 0)
        {
            dc.DrawText(Res.T(_size > 0 ? "No Intel flash descriptor — region map unavailable" : "No image loaded", 12, dim), new Point(12, 7));
            return;
        }
        using (dc.PushClip(new RoundedRect(new Rect(1, 1, w - 2, 28), 7)))
        {
            foreach (var r in _regions)
            {
                double x = r.Start / (double)_size * w, rw = Math.Max(2, r.Size / (double)_size * w);
                var c = Res.RegionColors[r.Index % 16];
                dc.FillRectangle(new SolidColorBrush(Color.FromArgb(r.State == "empty" ? (byte)0x50 : (byte)0xD8, c.R, c.G, c.B)), new Rect(x + 1, 1, rw - 1, 28));
                var label = Res.T($"{r.Name} · {r.SizeText}", 11, Brushes.Black, true);
                if (label.Width + 10 < rw) dc.DrawText(label, new Point(x + (rw - label.Width) / 2, 7));
            }
        }
        dc.DrawText(Res.T("0x00000000", 10.5, dim), new Point(0, 34));
        var end = Res.T($"0x{_size - 1:X8}", 10.5, dim);
        dc.DrawText(end, new Point(w - end.Width, 34));
    }
}

/// <summary>Grid of per-block usage cells (empty / zero / data / region-coloured).</summary>
public sealed class BlockMapView : Control
{
    byte[] _map = Array.Empty<byte>();
    int _blockSize = 65536;
    List<FlashRegion> _regions = new();
    const double Cell = 9, Gap = 2;

    public BlockMapView()
    {
        ToolTip.SetShowDelay(this, 100);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public void SetMap(byte[] map, int blockSize, List<FlashRegion> regions)
    {
        _map = map ?? Array.Empty<byte>(); _blockSize = blockSize; _regions = regions ?? new();
        InvalidateMeasure(); InvalidateVisual();
    }

    static int Cols(double w) => Math.Max(1, (int)((w + Gap) / (Cell + Gap)));

    protected override Size MeasureOverride(Size a)
    {
        double w = double.IsInfinity(a.Width) ? 600 : a.Width;
        int rows = _map.Length == 0 ? 2 : (_map.Length + Cols(w) - 1) / Cols(w);
        return new Size(w, rows * (Cell + Gap));
    }

    public override void Render(DrawingContext dc)
    {
        if (_map.Length == 0) { dc.DrawText(Res.T("No image loaded", 12, Palette.Br("TxtDim")), new Point(0, 0)); return; }
        int cols = Cols(Bounds.Width);
        var empty = Palette.Br("Line");
        var zero = Palette.Br("TxtFaint");
        for (int i = 0; i < _map.Length; i++)
        {
            double x = i % cols * (Cell + Gap), y = i / cols * (Cell + Gap);
            IBrush b;
            if (_map[i] == 0) b = empty;
            else if (_map[i] == 1) b = zero;
            else
            {
                long addr = (long)i * _blockSize;
                var reg = _regions.FirstOrDefault(r => addr >= r.Start && addr <= r.End);
                b = new SolidColorBrush(reg != null ? Res.RegionColors[reg.Index % 16] : Palette.Col("Info"));
            }
            dc.DrawRectangle(b, null, new Rect(x, y, Cell, Cell), 2, 2);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_map.Length == 0) return;
        var p = e.GetPosition(this);
        int cols = Cols(Bounds.Width);
        int i = (int)(p.Y / (Cell + Gap)) * cols + (int)(p.X / (Cell + Gap));
        if (i < 0 || i >= _map.Length) { ToolTip.SetTip(this, null); return; }
        long a = (long)i * _blockSize;
        var reg = _regions.FirstOrDefault(r => a >= r.Start && a <= r.End);
        ToolTip.SetTip(this, $"0x{a:X8} – 0x{a + _blockSize - 1:X8}\n{(_map[i] == 0 ? "Empty (0xFF)" : _map[i] == 1 ? "Zero-filled" : "Data")}{(reg != null ? "\nRegion: " + reg.Name : "")}");
        ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
    }
}

/// <summary>8-pin package pinout drawing.</summary>
public sealed class PinoutDiagram : Control
{
    public string Title { get; set; } = "";
    public string[] Pins { get; set; } = new string[8];

    public PinoutDiagram() { ActualThemeVariantChanged += (_, _) => InvalidateVisual(); }

    protected override Size MeasureOverride(Size a) => new(320, 200);

    public override void Render(DrawingContext dc)
    {
        var txt = Palette.Br("Txt");
        var dim = Palette.Br("TxtDim");
        dc.DrawText(Res.T(Title, 12.5, Palette.Br("Accent"), true), new Point(0, 0));
        double bx = 115, by = 34, bw = 90, bh = 150;
        dc.DrawRectangle(Palette.Br("ChipBody"), new Pen(Palette.Br("LineStrong"), 1.2), new Rect(bx, by, bw, bh), 8, 8);
        dc.DrawEllipse(Brushes.WhiteSmoke, null, new Point(bx + 14, by + 14), 4.5, 4.5);
        var pinB = new SolidColorBrush(Color.FromRgb(0xC4, 0xCA, 0xD3));
        var pinNum = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB0));
        for (int i = 0; i < 4; i++)
        {
            double y = by + 22 + i * 34;
            dc.FillRectangle(pinB, new Rect(bx - 16, y, 16, 9));
            dc.FillRectangle(pinB, new Rect(bx + bw, y, 16, 9));
            dc.DrawText(Res.T($"{i + 1}", 11, pinNum), new Point(bx + 6, y - 4));
            var rn = Res.T($"{8 - i}", 11, pinNum);
            dc.DrawText(rn, new Point(bx + bw - rn.Width - 6, y - 4));
            var l = Res.T(Pins[i] ?? "", 11.5, txt, true);
            dc.DrawText(l, new Point(bx - 22 - l.Width, y - 4));
            dc.DrawText(Res.T(Pins[7 - i] ?? "", 11.5, txt, true), new Point(bx + bw + 22, y - 4));
        }
        _ = dim;
    }
}
