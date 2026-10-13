using System.IO;
using Microsoft.Win32;

namespace VoiceType.App.Services;

/// <summary>التشغيل مع Windows عبر مفتاح Run الخاص بالمستخدم (لا يحتاج صلاحيات مسؤول).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VoiceType";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>إذا نُقل البرنامج لمكان آخر، يحدّث المسار المسجل في التشغيل مع ويندوز.</summary>
    public static void Refresh(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            var expected = $"\"{Environment.ProcessPath}\" --background";
            if (enabled ? current != expected : current is not null)
            {
                Set(enabled);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    /// <summary>ينشئ اختصارًا على سطح المكتب يشير لمكان البرنامج الحالي.</summary>
    public static string CreateDesktopShortcut()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(desktop, "صدى.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("تعذّر إنشاء الاختصار.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = Environment.ProcessPath;
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.IconLocation = Environment.ProcessPath + ",0";
            shortcut.Description = "صدى — الإملاء الصوتي";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        return path;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
