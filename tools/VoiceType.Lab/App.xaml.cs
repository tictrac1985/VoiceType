using System.IO;
using System.Windows;
using System.Windows.Threading;
using VoiceType.Settings;

namespace VoiceType.Lab;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logFile = Path.Combine(AppPaths.Ensure(AppPaths.Logs), "lab-errors.log");
        try
        {
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // لا نريد أن يفشل تسجيل الخطأ نفسه
        }

        MessageBox.Show(
            $"حدث خطأ غير متوقع، لكن البرنامج سيستمر في العمل.\n\n{e.Exception.Message}\n\nتفاصيل الخطأ محفوظة في:\n{logFile}",
            "VoiceType Lab",
            MessageBoxButton.OK,
            MessageBoxImage.Warning,
            MessageBoxResult.OK,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        e.Handled = true;
    }
}
