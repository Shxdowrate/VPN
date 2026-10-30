using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NeonVpn.Core;

namespace NeonVpn.Services
{
    /// <summary>Ctrl + Alt + V — включить/выключить VPN откуда угодно.</summary>
    public static class Hotkey
    {
        private const int Id = 0x4E56;
        private const int WmHotkey = 0x0312;

        public static void Register(Window owner, Action onPress)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(owner).EnsureHandle();

                // MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, 'V'
                if (!RegisterHotKey(handle, Id, 0x0001 | 0x0002 | 0x4000, 0x56))
                {
                    Log.Write("Ctrl+Alt+V занят другой программой");
                    return;
                }

                HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WmHotkey && wParam.ToInt32() == Id)
                    {
                        onPress();
                        handled = true;
                    }

                    return IntPtr.Zero;
                });
            }
            catch (Exception e)
            {
                Log.Write("Горячая клавиша: " + e.Message);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    }
}
