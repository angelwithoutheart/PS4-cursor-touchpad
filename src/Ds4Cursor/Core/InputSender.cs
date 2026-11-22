using System.Runtime.InteropServices;

namespace Ds4Cursor.Core;

internal enum MouseButton
{
    Left,
    Right,
    Middle
}

/// <summary>
/// Тонкая обёртка над SendInput. Буфер ввода переиспользуется, горячий путь не создаёт мусора.
/// </summary>
internal sealed class InputSender
{
    private static readonly int InputSize = Marshal.SizeOf<NativeMethods.INPUT>();
    private readonly NativeMethods.INPUT[] _buffer = new NativeMethods.INPUT[8];
    private int _count;

    public void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        AddMouse(NativeMethods.MouseEventMove, dx, dy, 0);
        Flush();
    }

    public void SetMouseButton(MouseButton button, bool down)
    {
        var flags = button switch
        {
            MouseButton.Left => down ? NativeMethods.MouseEventLeftDown : NativeMethods.MouseEventLeftUp,
            MouseButton.Right => down ? NativeMethods.MouseEventRightDown : NativeMethods.MouseEventRightUp,
            MouseButton.Middle => down ? NativeMethods.MouseEventMiddleDown : NativeMethods.MouseEventMiddleUp,
            _ => 0u
        };

        if (flags == 0)
        {
            return;
        }

        AddMouse(flags, 0, 0, 0);
        Flush();
    }

    public void LeftClick()
    {
        AddMouse(NativeMethods.MouseEventLeftDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftUp, 0, 0, 0);
        Flush();
    }

    public void RightClick()
    {
        AddMouse(NativeMethods.MouseEventRightDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventRightUp, 0, 0, 0);
        Flush();
    }

    public void DoubleClick()
    {
        AddMouse(NativeMethods.MouseEventLeftDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftUp, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftDown, 0, 0, 0);
        AddMouse(NativeMethods.MouseEventLeftUp, 0, 0, 0);
        Flush();
    }

    public void Wheel(int notches, bool horizontal)
    {
        if (notches == 0)
        {
            return;
        }

        WheelDelta(unchecked(notches * 120), horizontal);
    }

    public void WheelDelta(int delta, bool horizontal)
    {
        if (delta == 0)
        {
            return;
        }

        AddMouse(horizontal ? NativeMethods.MouseEventHWheel : NativeMethods.MouseEventWheel,
            0, 0, unchecked((uint)delta));
        Flush();
    }

    public void KeyTap(ushort virtualKey, bool extended = false)
    {
        AddKey(virtualKey, false, extended);
        AddKey(virtualKey, true, extended);
        Flush();
    }

    public void AltTab()
    {
        AddKey(NativeMethods.VkMenu, false, false);
        AddKey(NativeMethods.VkTab, false, false);
        AddKey(NativeMethods.VkTab, true, false);
        AddKey(NativeMethods.VkMenu, true, false);
        Flush();
    }

    public void ShowDesktop()
    {
        AddKey(NativeMethods.VkLWin, false, true);
        AddKey(NativeMethods.VkD, false, false);
        AddKey(NativeMethods.VkD, true, false);
        AddKey(NativeMethods.VkLWin, true, true);
        Flush();
    }

    public void CloseTab()
    {
        AddKey(NativeMethods.VkControl, false, false);
        AddKey(NativeMethods.VkW, false, false);
        AddKey(NativeMethods.VkW, true, false);
        AddKey(NativeMethods.VkControl, true, false);
        Flush();
    }

    public void VolumeUp() => KeyTap(NativeMethods.VkVolumeUp, true);

    public void VolumeDown() => KeyTap(NativeMethods.VkVolumeDown, true);

    public void MediaNext() => KeyTap(NativeMethods.VkMediaNextTrack, true);

    public void MediaPrevious() => KeyTap(NativeMethods.VkMediaPrevTrack, true);

    public void MediaPlayPause() => KeyTap(NativeMethods.VkMediaPlayPause, true);

    public void Enter() => KeyTap(NativeMethods.VkReturn);

    public void Backspace() => KeyTap(NativeMethods.VkBack);

    private void AddMouse(uint flags, int dx, int dy, uint data)
    {
        if (_count >= _buffer.Length)
        {
            Flush();
        }

        _buffer[_count++] = new NativeMethods.INPUT
        {
            type = NativeMethods.InputMouse,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = data,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private void AddKey(ushort virtualKey, bool up, bool extended)
    {
        if (_count >= _buffer.Length)
        {
            Flush();
        }

        uint flags = 0;
        if (up)
        {
            flags |= NativeMethods.KeyEventKeyUp;
        }

        if (extended)
        {
            flags |= NativeMethods.KeyEventExtendedKey;
        }

        _buffer[_count++] = new NativeMethods.INPUT
        {
            type = NativeMethods.InputKeyboard,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private void Flush()
    {
        if (_count == 0)
        {
            return;
        }

        NativeMethods.SendInput((uint)_count, _buffer, InputSize);
        _count = 0;
    }
}

