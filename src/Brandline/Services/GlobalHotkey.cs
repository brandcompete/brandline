using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Brandline.Services;

/// <summary>
/// Systemweites Tastenkürzel: Text in einer beliebigen App markieren (Outlook, Browser, PDF …),
/// Kürzel drücken → die markierte Nummer wird gewählt.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB0C1;

    private readonly HwndSource _source;
    private readonly Action _pressed;
    private bool _registered;

    public GlobalHotkey(Window window, Action pressed)
    {
        _pressed = pressed;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(WndProc);
    }

    /// <summary>Registriert z. B. "Ctrl+Alt+D". Liefert false, wenn das Kürzel ungültig oder schon belegt ist.</summary>
    public bool Register(string gesture)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(gesture)) return true;
        if (!TryParse(gesture, out var modifiers, out var key)) return false;

        var vk = KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_source.Handle, HotkeyId, (uint)modifiers | ModNoRepeat, (uint)vk);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    public static bool TryParse(string gesture, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(gesture) is not KeyGesture g) return false;
            modifiers = g.Modifiers;
            key = g.Key;
            return modifiers != ModifierKeys.None;
        }
        catch
        {
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Kopiert die aktuelle Markierung der Vordergrund-App (simuliertes Strg+C) und liefert den Text.
    /// Ein vorher kopierter Text wird danach wiederhergestellt.
    /// </summary>
    public static async Task<string?> GetSelectedTextAsync()
    {
        string? previous = null;
        try { if (Clipboard.ContainsText()) previous = Clipboard.GetText(); } catch { /* Zwischenablage belegt */ }

        // Noch gedrückte Kürzel-Tasten lösen, sonst käme bei der Ziel-App z. B. Strg+Alt+C an.
        ReleaseModifiers();
        try { Clipboard.Clear(); } catch { /* ignorieren */ }
        SendCtrlC();

        string? selected = null;
        for (var i = 0; i < 10 && selected == null; i++)
        {
            await Task.Delay(50);
            try { if (Clipboard.ContainsText()) selected = Clipboard.GetText(); } catch { /* weiter versuchen */ }
        }

        try
        {
            if (previous != null) Clipboard.SetText(previous);
        }
        catch { /* ignorieren */ }
        return selected;
    }

    private static void ReleaseModifiers()
    {
        var inputs = new[] { VkControl, VkMenu, VkShift, VkLWin }
            .Select(vk => KeyInput(vk, up: true)).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static void SendCtrlC()
    {
        var inputs = new[]
        {
            KeyInput(VkControl, up: false),
            KeyInput(VkC, up: false),
            KeyInput(VkC, up: true),
            KeyInput(VkControl, up: true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input KeyInput(ushort vk, bool up) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Flags = up ? 2u : 0u } }
    };

    private const uint ModNoRepeat = 0x4000;
    private const ushort VkControl = 0x11, VkMenu = 0x12, VkShift = 0x10, VkLWin = 0x5B, VkC = 0x43;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // Nur für die korrekte Größe der Union (MOUSEINPUT ist das größte Mitglied).
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }
}
