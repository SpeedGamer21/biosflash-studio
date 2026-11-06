using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace BiosFlashStudio.Ui;

/// <summary>Theme helpers: brush lookup, light/dark switching and monospace font resolution.</summary>
public static class Palette
{
    public static IBrush Br(string key)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var v))
        {
            if (v is IBrush b) return b;
            if (v is Color c) return new SolidColorBrush(c);
        }
        return Brushes.Magenta;
    }

    public static Color Col(string key) => Br(key) is ISolidColorBrush s ? s.Color : Colors.Magenta;

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static void Apply(bool dark)
    {
        if (Application.Current != null) Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    static FontFamily _mono;

    /// <summary>First installed monospace font out of a cross-platform candidate list.</summary>
    public static FontFamily Mono
    {
        get
        {
            if (_mono != null) return _mono;
            string[] candidates =
            {
                "Cascadia Mono", "Consolas", "JetBrains Mono", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono",
                "Adwaita Mono", "Ubuntu Mono", "Source Code Pro", "Menlo", "Courier New",
            };
            try
            {
                var installed = new HashSet<string>(FontManager.Current.SystemFonts.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                var pick = candidates.FirstOrDefault(installed.Contains);
                _mono = new FontFamily(pick ?? "monospace");
            }
            catch { _mono = new FontFamily("monospace"); }
            return _mono;
        }
    }
}

public static class AppIcon
{
    /// <summary>Renders the app logo (mint tile + chip) into a window/taskbar icon.</summary>
    public static WindowIcon Create()
    {
        try
        {
            const int S = 64;
            var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(S, S));
            using (var ctx = rtb.CreateDrawingContext())
            {
                ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#3DDC97")), null, new Rect(2, 2, S - 4, S - 4), 14, 14);
                using (ctx.PushTransform(Matrix.CreateScale(S / 24.0 * 0.72, S / 24.0 * 0.72) * Matrix.CreateTranslation(S * 0.14, S * 0.14)))
                    ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#04241A")), 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Icons.Get("chip"));
            }
            return new WindowIcon(rtb);
        }
        catch { return null; }
    }
}

public static class ResExt
{
    /// <summary>Binds a property to a theme resource so it follows light/dark switches (replaces any earlier binding).</summary>
    public static void Res(this Control c, AvaloniaProperty p, string key) => c.Bind(p, c.GetResourceObservable(key));
}

/// <summary>Stroke icon drawn from a 24×24 path; colour follows the inherited Foreground.</summary>
public sealed class Ico : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<Ico, string>(nameof(Kind));
    public static readonly StyledProperty<IBrush> BrushProperty = AvaloniaProperty.Register<Ico, IBrush>(nameof(Brush));
    public static readonly StyledProperty<double> StrokeProperty = AvaloniaProperty.Register<Ico, double>(nameof(Stroke), 1.9);

    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    static Ico() => AffectsRender<Ico>(KindProperty, BrushProperty, StrokeProperty, TextElement.ForegroundProperty);

    public Ico() { IsHitTestVisible = false; }

    public override void Render(DrawingContext ctx)
    {
        var g = Icons.Get(Kind);
        if (g == null) return;
        var brush = Brush ?? GetValue(TextElement.ForegroundProperty) ?? Brushes.Gray;
        double s = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        if (s <= 0) return;
        double ox = (Bounds.Width - 24 * s) / 2, oy = (Bounds.Height - 24 * s) / 2;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
            ctx.DrawGeometry(null, new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
    }
}

public static class Icons
{
    static readonly Dictionary<string, string> Src = new()
    {
        ["chip"] = "M7,5 H17 V19 H7 Z M4,8 H7 M4,12 H7 M4,16 H7 M17,8 H20 M17,12 H20 M17,16 H20 M10,8 A1,1 0 1 1 9.99,8",
        ["usb"] = "M9,3 V8 M15,3 V8 M6,8 H18 V12 A6,6 0 0 1 6,12 Z M12,18 V22",
        ["search"] = "M11,4 A7,7 0 1 1 10.99,4 Z M16.2,16.2 L21,21",
        ["read"] = "M12,3 V15 M7,10 L12,15 L17,10 M5,20 H19",
        ["write"] = "M12,16 V4 M7,9 L12,4 L17,9 M5,20 H19",
        ["erase"] = "M4,7 H20 M9,7 V4 H15 V7 M6,7 L7,20 H17 L18,7 M10,11 V16 M14,11 V16",
        ["blank"] = "M4,4 H8 M11,4 H13 M16,4 H20 V8 M20,11 V13 M20,16 V20 H16 M13,20 H11 M8,20 H4 V16 M4,13 V11 M4,8 V4",
        ["verify"] = "M12,3 L20,6 V12 C20,16.5 16.5,20 12,21 C7.5,20 4,16.5 4,12 V6 Z M8.5,12 L11,14.5 L15.5,9.5",
        ["auto"] = "M13,2 L4,14 H12 L11,22 L20,10 H12 Z",
        ["cancel"] = "M12,3 A9,9 0 1 1 11.99,3 Z M9,9 L15,15 M15,9 L9,15",
        ["open"] = "M3,6 H9 L11,8 H21 V19 H3 Z",
        ["save"] = "M5,4 H16 L20,8 V20 H5 Z M8,4 V9 H15 V4 M8,20 V14 H16 V20",
        ["grid"] = "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z",
        ["hex"] = "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M14,4 L10,20",
        ["pulse"] = "M3,12 H7 L10,5 L14,19 L17,12 H21",
        ["box"] = "M12,3 L20,7.5 V16.5 L12,21 L4,16.5 V7.5 Z M4,7.5 L12,12 L20,7.5 M12,12 V21",
        ["tune"] = "M4,6 H13 M17,6 H20 M15,4 V8 M4,12 H6 M10,12 H20 M8,10 V14 M4,18 H12 M16,18 H20 M14,16 V20",
        ["db"] = "M4,6 A8,3 0 1 0 20,6 A8,3 0 1 0 4,6 Z M4,6 V18 A8,3 0 0 0 20,18 V6 M4,12 A8,3 0 0 0 20,12",
        ["clock"] = "M12,3 A9,9 0 1 1 11.99,3 Z M12,7 V12 L15,14",
        ["gear"] = "M12,9 A3,3 0 1 1 11.99,9 Z M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M4.9,19.1 L7,17 M17,7 L19.1,4.9",
        ["term"] = "M4,5 H20 V19 H4 Z M7,9 L10,12 L7,15 M12,15 H17",
        ["sun"] = "M12,8 A4,4 0 1 1 11.99,8 Z M12,2 V4 M12,20 V22 M2,12 H4 M20,12 H22 M5,5 L6.5,6.5 M17.5,17.5 L19,19 M5,19 L6.5,17.5 M17.5,6.5 L19,5",
        ["moon"] = "M20,14.5 A8,8 0 1 1 9.5,4 A6.5,6.5 0 0 0 20,14.5 Z",
        ["copy"] = "M9,9 H20 V20 H9 Z M5,15 V4 H15 V6",
        ["file"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19",
        ["export"] = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19 M12,11 V17 M9,14 L12,17 L15,14",
        ["key"] = "M8,11 A4,4 0 1 1 7.99,11 Z M11,13 L20,4 M17,7 L20,10 M15,9 L17,11",
        ["refresh"] = "M20,12 A8,8 0 1 1 17.7,6.3 M20,4 V9 H15",
        ["unlock"] = "M6,11 H18 V21 H6 Z M8,11 V7 A4,4 0 0 1 15.5,5",
        ["compare"] = "M8,4 V20 M16,4 V20 M4,8 L8,4 L12,8 M12,16 L16,20 L20,16",
        ["merge"] = "M6,3 V9 A6,6 0 0 0 12,15 H19 M16,12 L19,15 L16,18 M6,21 V15",
        ["info"] = "M12,3 A9,9 0 1 1 11.99,3 Z M12,11 V16 M12,8 V8.01",
        ["warn"] = "M12,3 L22,20 H2 Z M12,10 V14 M12,17 V17.01",
        ["check"] = "M12,3 A9,9 0 1 1 11.99,3 Z M8,12 L11,15 L16,9",
        ["pin"] = "M5,9 H19 V15 H5 Z M7,9 V5 M12,9 V5 M17,9 V5 M7,15 V19 M12,15 V19 M17,15 V19",
        ["layers"] = "M12,3 L21,8 L12,13 L3,8 Z M3,12 L12,17 L21,12 M3,16 L12,21 L21,16",
        ["log"] = "M5,5 H19 M5,10 H19 M5,15 H14 M5,20 H11",
        ["linux"] = "M12,3 A4,5 0 0 1 16,8 V12 L19,17 H5 L8,12 V8 A4,5 0 0 1 12,3 Z M10,8 V8.01 M14,8 V8.01 M9,20 H15",
        ["bench"] = "M12,13 L16,9 M4,18 A9,9 0 1 1 20,18",
        ["unlink"] = "M9,15 L15,9 M10,6 L12,4 A4,4 0 0 1 20,12 L18,14 M6,10 L4,12 A4,4 0 0 0 12,20 L14,18",
    };

    static readonly Dictionary<string, Geometry> Cache = new();

    public static Geometry Get(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (Cache.TryGetValue(name, out var g)) return g;
        g = Src.TryGetValue(name, out var s) ? StreamGeometry.Parse(s) : null;
        Cache[name] = g;
        return g;
    }
}

public enum DialogKind { Info, Question, Warning, Error }

/// <summary>Themed message boxes (Avalonia has no built-in MessageBox).</summary>
public static class Dialogs
{
    public static async Task<bool> Ask(Window owner, string title, string text, string yes = "Yes", string no = "No", DialogKind kind = DialogKind.Question)
        => await Show(owner, title, text, kind, yes, no);

    public static async Task Info(Window owner, string title, string text, DialogKind kind = DialogKind.Info)
        => await Show(owner, title, text, kind, "OK", null);

    static async Task<bool> Show(Window owner, string title, string text, DialogKind kind, string yes, string no)
    {
        bool result = false;
        var (icon, key) = kind switch
        {
            DialogKind.Warning => ("warn", "Warn"),
            DialogKind.Error => ("cancel", "Bad"),
            DialogKind.Question => ("info", "Info"),
            _ => ("check", "Accent"),
        };
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 400,
            MaxWidth = 600,
        };
        var yesBtn = new Button { Content = yes, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        yesBtn.Classes.Add(kind == DialogKind.Warning && no != null ? "danger" : "accent");
        yesBtn.Click += (_, _) => { result = true; w.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 20, 0, 0) };
        if (no != null)
        {
            var noBtn = new Button { Content = no, IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            noBtn.Click += (_, _) => { result = false; w.Close(); };
            buttons.Children.Add(noBtn);
        }
        buttons.Children.Add(yesBtn);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Background = Palette.Br(key + (key == "Accent" ? "Soft" : "Bg")),
            Child = new Ico { Kind = icon, Brush = Palette.Br(key), Width = 22, Height = 22 },
        });
        head.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(52, 10, 0, 0), MaxWidth = 500, Foreground = Palette.Br("Txt"), LineHeight = 20 });
        body.Children.Add(buttons);
        w.Content = body;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { result = false; w.Close(); } };
        if (owner != null && owner.IsVisible) await w.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            w.Closed += (_, _) => tcs.TrySetResult();
            w.Show();
            await tcs.Task;
        }
        return result;
    }
}
