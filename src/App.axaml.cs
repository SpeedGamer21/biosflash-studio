using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BiosFlashStudio.Core;
using BiosFlashStudio.Ui;

namespace BiosFlashStudio;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Resources["MonoFont"] = Palette.Mono;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            CrashLog(e.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
                _ = Dialogs.Info(w, "Unexpected error", e.Exception.Message, DialogKind.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog(e.ExceptionObject as Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }

    static void CrashLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
