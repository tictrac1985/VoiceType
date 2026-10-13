using System.Windows.Interop;
using static VoiceType.App.Services.NativeMethods;

namespace VoiceType.App.Services;

/// <summary>اختصارات لوحة المفاتيح على مستوى Windows (RegisterHotKey).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 0x5600;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("VoiceTypeHotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    /// <summary>يسجل اختصارًا. يعيد المعرّف، أو null إذا كان الاختصار مستخدمًا من برنامج آخر.</summary>
    public int? Register(uint modifiers, uint key, Action action)
    {
        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, key))
        {
            return null;
        }

        _actions[id] = action;
        return id;
    }

    public void Unregister(int? id)
    {
        if (id is { } value && _actions.Remove(value))
        {
            UnregisterHotKey(_source.Handle, value);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue((int)wParam, out var action))
        {
            handled = true;
            action();
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys.ToList())
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _actions.Clear();
        _source.Dispose();
    }
}
