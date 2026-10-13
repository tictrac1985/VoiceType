using System.IO;
using System.Text;
using VoiceType.Core.Speech;
using VoiceType.Settings;

namespace VoiceType.App.Services;

/// <summary>
/// سجل تشخيص بسيط للإملاء في data\logs\dictation.log: أي نموذج استُخدم، وكم استغرق، ولماذا فشل ما قبله.
/// لا يُكتب فيه نص الكلام ولا المفاتيح.
/// </summary>
public static class DictationLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.Logs, "dictation.log");

    public static void Write(TimeSpan audio, TimeSpan elapsed, string? engineId, IReadOnlyList<EngineFailure> failures, string? error)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append($" | audio {audio.TotalSeconds:0.0}s")
                .Append(engineId is null ? " | FAILED" : $" | {engineId} in {elapsed.TotalSeconds:0.0}s");
            foreach (var failure in failures)
            {
                line.Append($" | before: {failure.EngineId} {failure.Kind} {failure.Details}");
            }

            if (error is not null)
            {
                line.Append($" | error: {error}");
            }

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, line.ToString().ReplaceLineEndings(" ") + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
