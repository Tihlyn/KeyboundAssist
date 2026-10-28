using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeyboundAssist.Typing;

// One physical key, plus whether Shift must be held (resolved through the game's keyboard layout).
public readonly record struct KeyStroke(ushort Vk, ushort Scan, bool Shift);

// Real key presses through SendInput (scan codes, Shift included): the duty reads key presses, and a
// bare WM_CHAR was routed to the chat box instead (session 4). SendInput goes to the foreground window,
// so nothing is sent unless the game window is in front. Esc is deliberately absent: it also means "Quit".
public static class KeySender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFScanCode = 0x0008;
    private const uint MapVkToVsc = 0;
    private const ushort VkShift = 0x10;
    private const ushort VkBack = 0x08;
    private const ushort VkReturn = 0x0D;

    private static nint window;

    public static KeyStroke Backspace { get; } = Make(VkBack, false);

    public static KeyStroke Enter { get; } = Make(VkReturn, false);

    public static nint Window
    {
        get
        {
            if (window == 0)
                window = Process.GetCurrentProcess().MainWindowHandle;
            return window;
        }
    }

    public static bool GameFocused => Window != 0 && GetForegroundWindow() == Window;

    // Null when the character needs Ctrl/Alt (AltGr) or does not exist on the active layout.
    // Call from the framework thread: VkKeyScan uses the calling thread's (the game's) layout.
    public static KeyStroke? Resolve(char c)
    {
        var r = VkKeyScanW(c);
        if (r == -1)
            return null;
        var vk = (ushort)(r & 0xFF);
        var mods = (r >> 8) & 0xFF;
        if ((mods & ~1) != 0)
            return null;
        return Make(vk, (mods & 1) != 0);
    }

    public static bool CanType(string text)
    {
        foreach (var c in text)
        {
            if (Resolve(c) is null)
                return false;
        }

        return true;
    }

    public static bool Down(KeyStroke k)
    {
        Span<Input> inputs = stackalloc Input[2];
        var n = 0;
        if (k.Shift)
            inputs[n++] = Key(Make(VkShift, false), up: false);
        inputs[n++] = Key(k, up: false);
        return Send(inputs[..n]);
    }

    public static bool Up(KeyStroke k)
    {
        Span<Input> inputs = stackalloc Input[2];
        var n = 0;
        inputs[n++] = Key(k, up: true);
        if (k.Shift)
            inputs[n++] = Key(Make(VkShift, false), up: true);
        return Send(inputs[..n]);
    }

    private static KeyStroke Make(ushort vk, bool shift) => new(vk, (ushort)MapVirtualKeyW(vk, MapVkToVsc), shift);

    private static Input Key(KeyStroke k, bool up) => new()
    {
        Type = InputKeyboard,
        Ki = new KeybdInput
        {
            Vk = k.Vk,
            Scan = k.Scan,
            Flags = KeyEventFScanCode | (up ? KeyEventFKeyUp : 0),
        },
    };

    private static unsafe bool Send(Span<Input> inputs)
    {
        fixed (Input* p = inputs)
            return SendInput((uint)inputs.Length, p, sizeof(Input)) == inputs.Length;
    }

    // INPUT is 40 bytes on x64: type + padding + the 32-byte union (MOUSEINPUT is its largest member).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)]
        public uint Type;

        [FieldOffset(8)]
        public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint SendInput(uint count, Input* inputs, int size);

    [DllImport("user32.dll")]
    private static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
