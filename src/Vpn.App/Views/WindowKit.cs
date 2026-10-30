using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NeonVpn.Controls;

namespace NeonVpn.Views
{
    /// <summary>Общее для неоновых окон: перетаскивание за корпус, место на экране, цвета рамки.</summary>
    public static class WindowKit
    {
        /// <summary>Тянуть окно за любое «пустое» место корпуса (не за кнопки и поля).</summary>
        public static void EnableDrag(Window w, UIElement surface)
        {
            surface.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed || IsInteractive(e.OriginalSource as DependencyObject))
                {
                    return;
                }

                try
                {
                    w.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // кнопку уже отпустили
                }
            };
        }

        private static bool IsInteractive(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ButtonBase || d is TextBox || d is ListBoxItem || d is ScrollBar || d is Slider || d is PowerButton || d is ComboBox)
                {
                    return true;
                }

                if (d is NeonFrame || d is Window)
                {
                    return false;
                }

                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }

            return false;
        }

        /// <summary>Ставит окно на сохранённое место, если оно ещё на каком-то экране, иначе — по центру.</summary>
        public static void Place(Window w, double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                return;
            }

            Rect screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            Rect r = new Rect(left, top, Math.Max(100, w.Width), Math.Max(60, w.Height));

            if (screen.IntersectsWith(r) && r.Left > screen.Left - r.Width + 80 && r.Top > screen.Top - 20 && r.Top < screen.Bottom - 60)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = left;
                w.Top = top;
            }
            else
            {
                w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        public static void ShowAndActivate(Window w)
        {
            if (!w.IsVisible)
            {
                w.Show();
            }

            if (w.WindowState == WindowState.Minimized)
            {
                w.WindowState = WindowState.Normal;
            }

            w.Activate();
            w.Topmost = true;
            w.Topmost = false;
            w.Focus();
        }

        public static void Style(NeonFrame frame, Core.Settings s, Color stateColor, bool on)
        {
            frame.Shape = s.FrameShape;
            frame.Intensity = s.Glow * (on ? 1.15 : 0.75);
            frame.GlowColor = on ? Palette.Accent : stateColor == Palette.Off ? Palette.Accent : stateColor;
            frame.GlowColor2 = Palette.Accent2;
        }
    }
}
