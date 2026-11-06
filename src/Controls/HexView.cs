using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio.Controls;

/// <summary>Fast virtualized hex editor that can display/edit very large buffers (64 MB+).</summary>
public sealed class HexView : Control
{
    const int BPR = 16;
    const double Pad = 12;
    byte[] _data = Array.Empty<byte>();
    byte[] _base;
    long _top, _cursor, _anchor;
    bool _ascii, _lowNib, _dragging, _syncing;
    ScrollBar _scroll;
    Typeface _tf;
    const double Fs = 13;
    double _cw, _rh;

    public event EventHandler SelectionChanged;
    public event EventHandler DataEdited;
    public bool ReadOnly { get; set; }
    public byte[] Data => _data;
    public long CursorOffset => _cursor;
    public long SelectionStart => Math.Min(_anchor, _cursor);
    public long SelectionLength => _data.Length == 0 ? 0 : Math.Abs(_cursor - _anchor) + 1;

    public HexView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _tf = new Typeface(Palette.Mono);
        var ft = Ft("0000000000", Brushes.Black);
        _cw = ft.WidthIncludingTrailingWhitespace / 10.0;
        _rh = Math.Ceiling(Fs * 1.55);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public ScrollBar ScrollBar
    {
        get => _scroll;
        set
        {
            _scroll = value;
            _scroll.ValueChanged += (_, e) =>
            {
                if (_syncing) return;
                _top = (long)Math.Round(e.NewValue);
                InvalidateVisual();
            };
        }
    }

    // ----------------------------------------------------------- data api
    public void SetData(byte[] data, bool resetBaseline = true)
    {
        _data = data ?? Array.Empty<byte>();
        if (resetBaseline) _base = _data.Length > 0 ? (byte[])_data.Clone() : null;
        _top = 0; _cursor = 0; _anchor = 0; _lowNib = false;
        SyncScroll();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AcceptEdits() { _base = _data.Length > 0 ? (byte[])_data.Clone() : null; InvalidateVisual(); }

    public void RevertEdits()
    {
        if (_base != null && _base.Length == _data.Length)
        {
            Buffer.BlockCopy(_base, 0, _data, 0, _data.Length);
            InvalidateVisual();
            DataEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    public long ModifiedCount()
    {
        if (_base == null || _base.Length != _data.Length) return 0;
        long n = 0;
        for (int i = 0; i < _data.Length; i++) if (_data[i] != _base[i]) n++;
        return n;
    }

    public void Select(long start, long length)
    {
        if (_data.Length == 0) return;
        start = Math.Clamp(start, 0, _data.Length - 1);
        long end = Math.Clamp(start + Math.Max(1, length) - 1, 0, _data.Length - 1);
        _anchor = start; _cursor = end; _lowNib = false;
        long row = start / BPR;
        if (row < _top || row >= _top + VisibleRows) _top = Math.Max(0, row - 2);
        SyncScroll();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoTo(long addr) => Select(addr, 1);

    public long Find(byte[] pattern, long from)
    {
        if (pattern.Length == 0 || _data.Length == 0 || from >= _data.Length) return -1;
        int i = new ReadOnlySpan<byte>(_data, (int)from, (int)(_data.Length - from)).IndexOf(pattern);
        return i < 0 ? -1 : from + i;
    }

    public string SelectionAsHex(int max = 1 << 20)
    {
        long n = Math.Min(SelectionLength, max);
        var sb = new StringBuilder((int)n * 3);
        for (long i = 0; i < n; i++) sb.Append(_data[SelectionStart + i].ToString("X2")).Append(i % 16 == 15 ? "\n" : " ");
        return sb.ToString().TrimEnd();
    }

    public void RefreshView() => InvalidateVisual();

    // ----------------------------------------------------------- layout helpers
    long TotalRows => (_data.Length + BPR - 1) / BPR;
    double HeaderH => _rh + 8;
    int VisibleRows => Math.Max(1, (int)((Bounds.Height - HeaderH) / _rh));
    double HexX => Pad + 11 * _cw;
    double ByteX(int i) => HexX + (i * 3 + (i >= 8 ? 1 : 0)) * _cw;
    double AsciiX => HexX + (BPR * 3 + 3) * _cw;

    FormattedText Ft(string s, IBrush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _tf, Fs, b);

    void SyncScroll()
    {
        if (_scroll == null) return;
        _syncing = true;
        long vis = VisibleRows;
        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, TotalRows - vis);
        _scroll.ViewportSize = vis;
        _scroll.LargeChange = vis;
        _scroll.SmallChange = 1;
        _top = Math.Clamp(_top, 0, (long)_scroll.Maximum);
        _scroll.Value = _top;
        _syncing = false;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SyncScroll();
    }

    // ----------------------------------------------------------- rendering
    public override void Render(DrawingContext dc)
    {
        var bg = Palette.Br("InputBg");
        var txt = Palette.Br("Txt");
        var dim = Palette.Br("TxtFaint");
        var offB = Palette.Br("Info");
        var mod = Palette.Br("Warn");
        var selB = Palette.Br("SelBg");
        var linePen = new Pen(Palette.Br("Line"), 1);
        var curPen = new Pen(Palette.Br("Accent"), 1.4);
        double w = Bounds.Width, h = Bounds.Height;
        double ty = (_rh - Fs * 1.25) / 2;

        dc.FillRectangle(bg, new Rect(0, 0, w, h));
        dc.FillRectangle(Palette.Br("CardBg2"), new Rect(0, 0, w, HeaderH));
        dc.DrawText(Ft("OFFSET", dim), new Point(Pad, 4 + ty));
        for (int i = 0; i < BPR; i++) dc.DrawText(Ft(i.ToString("X2"), _data.Length > 0 && i == _cursor % BPR ? offB : dim), new Point(ByteX(i), 4 + ty));
        dc.DrawText(Ft("ASCII", dim), new Point(AsciiX, 4 + ty));
        dc.DrawLine(linePen, new Point(0, HeaderH - 0.5), new Point(w, HeaderH - 0.5));
        dc.DrawLine(linePen, new Point(HexX - _cw, HeaderH), new Point(HexX - _cw, h));
        dc.DrawLine(linePen, new Point(AsciiX - _cw * 1.5, HeaderH), new Point(AsciiX - _cw * 1.5, h));

        if (_data.Length == 0)
        {
            dc.DrawText(Ft("Buffer empty — read a chip, open a file (Ctrl+O) or drop a .bin / .cap here.", Palette.Br("TxtDim")), new Point(Pad, HeaderH + 14));
            return;
        }

        long selMin = SelectionStart, selMax = Math.Max(_anchor, _cursor);
        bool hasSel = selMax > selMin;
        bool baseOk = _base != null && _base.Length == _data.Length;
        int vis = VisibleRows + 1;
        var hexChars = new char[BPR * 3 + 1];
        var ascChars = new char[BPR];
        const string HX = "0123456789ABCDEF";
        for (int r = 0; r < vis; r++)
        {
            long row = _top + r;
            if (row >= TotalRows) break;
            double y = HeaderH + r * _rh + 1;
            long off = row * BPR;
            int n = (int)Math.Min(BPR, _data.Length - off);

            for (int i = 0; i < n; i++)
            {
                long idx = off + i;
                if (hasSel && idx >= selMin && idx <= selMax)
                {
                    dc.FillRectangle(selB, new Rect(ByteX(i) - _cw * 0.5, y, _cw * 3, _rh));
                    dc.FillRectangle(selB, new Rect(AsciiX + i * _cw, y, _cw, _rh));
                }
                if (idx == _cursor)
                {
                    double cx = ByteX(i) - 2 + (_lowNib && !_ascii ? _cw : 0);
                    dc.DrawRectangle(null, curPen, new Rect(_ascii ? ByteX(i) - 2 : cx, y + 0.5, _ascii || !_lowNib ? _cw * 2 + 4 : _cw + 3, _rh - 1), 3, 3);
                    dc.DrawRectangle(null, curPen, new Rect(AsciiX + i * _cw - 0.5, y + 0.5, _cw + 1, _rh - 1), 2, 2);
                }
            }

            dc.DrawText(Ft(off.ToString("X8"), offB), new Point(Pad, y + ty));

            int p = 0;
            for (int i = 0; i < BPR; i++)
            {
                if (i < n)
                {
                    byte b = _data[off + i];
                    hexChars[p] = HX[b >> 4];
                    hexChars[p + 1] = HX[b & 15];
                    ascChars[i] = b >= 0x20 && b < 0x7F ? (char)b : '.';
                }
                else { hexChars[p] = ' '; hexChars[p + 1] = ' '; ascChars[i] = ' '; }
                hexChars[p + 2] = ' ';
                p += 3;
                if (i == 7) hexChars[p++] = ' ';
            }
            var ftHex = Ft(new string(hexChars, 0, p), txt);
            var ftAsc = Ft(new string(ascChars, 0, n), txt);
            for (int i = 0; i < n; i++)
            {
                byte b = _data[off + i];
                int ci = i * 3 + (i >= 8 ? 1 : 0);
                IBrush c = null;
                if (baseOk && _base[off + i] != b) c = mod;
                else if (b == 0xFF || b == 0x00) c = dim;
                if (c != null) { ftHex.SetForegroundBrush(c, ci, 2); ftAsc.SetForegroundBrush(c, i, 1); }
            }
            dc.DrawText(ftHex, new Point(HexX, y + ty));
            dc.DrawText(ftAsc, new Point(AsciiX, y + ty));
        }
    }

    // ----------------------------------------------------------- input
    (long idx, bool ascii) HitTest(Point pt, bool clamp)
    {
        if (_data.Length == 0) return (-1, false);
        double y = pt.Y - HeaderH;
        long row = _top + (long)Math.Floor(y / _rh);
        if (!clamp && (y < 0 || row >= TotalRows)) return (-1, false);
        row = Math.Clamp(row, 0, TotalRows - 1);
        bool ascii;
        int col;
        if (pt.X >= AsciiX - _cw * 0.5)
        {
            ascii = true;
            col = (int)Math.Floor((pt.X - AsciiX) / _cw);
        }
        else
        {
            ascii = false;
            double x = pt.X - HexX;
            if (x > 8 * 3 * _cw) x -= _cw;
            col = (int)Math.Floor((x + _cw * 0.5) / (3 * _cw));
        }
        col = Math.Clamp(col, 0, BPR - 1);
        long idx = Math.Min(row * BPR + col, _data.Length - 1);
        return (idx, ascii);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pp = e.GetCurrentPoint(this);
        if (!pp.Properties.IsLeftButtonPressed) return;
        Focus();
        var (idx, ascii) = HitTest(pp.Position, false);
        if (idx < 0) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _anchor = idx;
        _cursor = idx;
        _ascii = ascii;
        _lowNib = false;
        _dragging = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var pt = e.GetPosition(this);
        if (pt.Y < HeaderH && _top > 0) { _top--; SyncScroll(); }
        else if (pt.Y > Bounds.Height && _top < TotalRows - VisibleRows) { _top++; SyncScroll(); }
        var (idx, _) = HitTest(pt, true);
        if (idx >= 0 && idx != _cursor)
        {
            _cursor = idx;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        long max = Math.Max(0, TotalRows - VisibleRows);
        _top = Math.Clamp(_top - (long)Math.Round(e.Delta.Y * 3), 0, max);
        SyncScroll();
        InvalidateVisual();
        e.Handled = true;
    }

    void Move(long delta, bool extend)
    {
        if (_data.Length == 0) return;
        long n = Math.Clamp(_cursor + delta, 0, _data.Length - 1);
        _cursor = n;
        if (!extend) _anchor = n;
        _lowNib = false;
        EnsureVisible();
    }

    void EnsureVisible()
    {
        long row = _cursor / BPR;
        int vis = VisibleRows;
        if (row < _top) _top = row;
        else if (row >= _top + vis) _top = row - vis + 1;
        SyncScroll();
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        int vis = VisibleRows;
        switch (e.Key)
        {
            case Key.Left: Move(-1, shift); break;
            case Key.Right: Move(1, shift); break;
            case Key.Up: Move(-BPR, shift); break;
            case Key.Down: Move(BPR, shift); break;
            case Key.PageUp: Move(-BPR * vis, shift); break;
            case Key.PageDown: Move(BPR * vis, shift); break;
            case Key.Home: Move(ctrl ? -_cursor : -(_cursor % BPR), shift); break;
            case Key.End: Move(ctrl ? _data.Length - 1 - _cursor : BPR - 1 - _cursor % BPR, shift); break;
            case Key.Tab: _ascii = !_ascii; _lowNib = false; InvalidateVisual(); break;
            case Key.A when ctrl:
                if (_data.Length > 0) { _anchor = 0; _cursor = _data.Length - 1; InvalidateVisual(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
                break;
            case Key.C when ctrl:
                if (_data.Length > 0) _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(SelectionAsHex());
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (ReadOnly || _data.Length == 0 || string.IsNullOrEmpty(e.Text)) return;
        char c = e.Text[0];
        if (!_ascii)
        {
            int v = Uri.IsHexDigit(c) ? Convert.ToInt32(c.ToString(), 16) : -1;
            if (v < 0) return;
            byte b = _data[_cursor];
            if (!_lowNib) { _data[_cursor] = (byte)((v << 4) | (b & 0x0F)); _lowNib = true; InvalidateVisual(); }
            else
            {
                _data[_cursor] = (byte)((b & 0xF0) | v);
                _lowNib = false;
                if (_cursor < _data.Length - 1) _cursor++;
                _anchor = _cursor;
                EnsureVisible();
            }
        }
        else
        {
            if (c < 0x20 || c > 0x7E) return;
            _data[_cursor] = (byte)c;
            if (_cursor < _data.Length - 1) _cursor++;
            _anchor = _cursor;
            EnsureVisible();
        }
        DataEdited?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
