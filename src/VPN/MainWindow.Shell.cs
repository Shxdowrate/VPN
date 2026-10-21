using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VPN.Core;

namespace VPN
{
    /// <summary>
    /// Всё, что касается самого окна: фигурная форма, выдвижная панель слева, положение на экране,
    /// трей, системные уведомления и горячая клавиша.
    /// </summary>
    public partial class MainWindow
    {
        private enum DrawerPage
        {
            Servers,
            Tunnels,
            Sub,
            Settings
        }

        /// <summary>Где начинается «смартфон» внутри окна: 16 тень + 420 панель + 12 зазор.</summary>
        private const double PhoneOffset = 448;

        /// <summary>Отступ под тень справа, сверху и снизу.</summary>
        private const double ShadowPad = 16;

        private const double PhoneMinWidth = 360;
        private const double PhoneDefaultWidth = 372;

        private readonly DispatcherTimer placementSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private bool drawerOpen;
        private DrawerPage drawerPage;
        private double drawerShiftApplied;
        private bool animatingLeft;
        private bool reallyExit;
        private bool trayHintShown;

        private System.Windows.Forms.NotifyIcon tray;
        private System.Windows.Forms.ToolStripMenuItem trayToggle;
        private System.Windows.Forms.ToolStripMenuItem trayStatus;
        private System.Windows.Forms.ToolStripMenuItem trayServers;
        private readonly Dictionary<string, System.Drawing.Icon> trayIcons = new Dictionary<string, System.Drawing.Icon>();

        // ================================================================== запуск окна

        private void InitShell()
        {
            RestorePlacement();
            Topmost = settings.Topmost;
            UpdatePinLook();

            placementSave.Tick += (_, _) =>
            {
                placementSave.Stop();
                SavePlacement();
            };

            LocationChanged += (_, _) =>
            {
                if (!animatingLeft)
                {
                    // Окно передвинули руками, пока панель открыта, — назад его не возвращаем.
                    drawerShiftApplied = 0;
                }

                placementSave.Stop();
                placementSave.Start();
            };

            SizeChanged += (_, _) =>
            {
                placementSave.Stop();
                placementSave.Start();
            };

            SourceInitialized += (_, _) =>
            {
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
                ApplyHotkey();
            };

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape)
                {
                    return;
                }

                if (PickerOverlay.Visibility == Visibility.Visible)
                {
                    PickerOverlay.Visibility = Visibility.Collapsed;
                }
                else if (LogOverlay.Visibility == Visibility.Visible)
                {
                    OnLogClose(null, null);
                }
                else
                {
                    CloseDrawer();
                }

                e.Handled = true;
            };
        }

        /// <summary>Возвращаем «смартфон» туда, где он был; если того экрана уже нет — по центру.</summary>
        private void RestorePlacement()
        {
            Rect work = SystemParameters.WorkArea;
            MinHeight = Math.Min(MinHeight, work.Height + 2 * ShadowPad);

            double phoneWidth = settings.WindowWidth >= PhoneMinWidth ? settings.WindowWidth : PhoneDefaultWidth;
            Width = PhoneOffset + phoneWidth + ShadowPad;
            Height = settings.WindowHeight >= MinHeight ? settings.WindowHeight : Math.Max(MinHeight, Math.Min(780, work.Height + 2 * ShadowPad - 20));

            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            bool saved = !double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop) &&
                         screen.IntersectsWith(new Rect(settings.WindowLeft, settings.WindowTop, phoneWidth, 120));

            double phoneLeft = saved ? settings.WindowLeft : work.Left + (work.Width - phoneWidth) / 2;
            double top = saved ? settings.WindowTop - ShadowPad : work.Top + (work.Height - Height) / 2;
            Left = phoneLeft - PhoneOffset;
            Top = top;
        }

        private void SavePlacement()
        {
            if (WindowState != WindowState.Normal || !IsLoaded)
            {
                return;
            }

            settings.WindowLeft = Left + PhoneOffset - drawerShiftApplied;
            settings.WindowTop = Top + ShadowPad;
            settings.WindowWidth = Math.Max(PhoneMinWidth, Width - PhoneOffset - ShadowPad);
            settings.WindowHeight = Height;
            settings.Save();
        }

        // ================================================================== форма корпуса

        private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
        {
            Geometry shape = BuildBodyShape(BodyHost.ActualWidth, BodyHost.ActualHeight);
            BodyShadow.Data = shape;
            BodyFill.Data = shape;
            BodyStroke.Data = shape;
            BodyContent.Clip = shape;
        }

        /// <summary>
        /// Корпус: прямоугольник с большими скруглениями, а посередине сверху — плавный выступ-«капсула»,
        /// в котором живёт статус. Края выступа — S-образные кривые, без углов.
        /// </summary>
        private static Geometry BuildBodyShape(double w, double h)
        {
            const double rise = 14;   // насколько выступ выше основного края
            const double radius = 30; // скругление углов корпуса
            const double curve = 24;  // ширина плавного перехода
            double tab = Math.Min(176, Math.Max(80, w - 2 * radius - 2 * curve - 60));

            if (w < 2 * radius + 1 || h < rise + 2 * radius + 1)
            {
                return Geometry.Empty;
            }

            double cx = w / 2;
            double left = cx - tab / 2;
            double right = cx + tab / 2;

            var g = new StreamGeometry();

            using (StreamGeometryContext c = g.Open())
            {
                c.BeginFigure(new Point(radius, rise), true, true);
                c.LineTo(new Point(left - curve, rise), true, true);
                c.BezierTo(new Point(left - curve * 0.45, rise), new Point(left - curve * 0.55, 0), new Point(left, 0), true, true);
                c.LineTo(new Point(right, 0), true, true);
                c.BezierTo(new Point(right + curve * 0.55, 0), new Point(right + curve * 0.45, rise), new Point(right + curve, rise), true, true);
                c.LineTo(new Point(w - radius, rise), true, true);
                c.ArcTo(new Point(w, rise + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(w, h - radius), true, true);
                c.ArcTo(new Point(w - radius, h), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(radius, h), true, true);
                c.ArcTo(new Point(0, h - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(0, rise + radius), true, true);
                c.ArcTo(new Point(radius, rise), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            }

            g.Freeze();
            return g;
        }

        // ================================================================== перетаскивание

        /// <summary>Окно тянется за любое «пустое» место корпуса, капсулу и шапку панели.</summary>
        private void OnDragArea(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            for (DependencyObject d = e.OriginalSource as DependencyObject; d != null; d = Parent(d))
            {
                if (d == PowerButton || d is ButtonBase || d is TextBoxBase || d is ScrollBar || d is Thumb || d is ListBoxItem)
                {
                    return;
                }

                if (d == sender)
                {
                    break;
                }
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // кнопку уже отпустили
            }
        }

        private static DependencyObject Parent(DependencyObject d) =>
            d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        // ================================================================== выдвижная панель

        private void OnDockClick(object sender, RoutedEventArgs e)
        {
            DrawerPage page = sender == DockTunnels ? DrawerPage.Tunnels
                : sender == DockSub ? DrawerPage.Sub
                : sender == DockSettings ? DrawerPage.Settings
                : DrawerPage.Servers;

            if (drawerOpen && drawerPage == page)
            {
                CloseDrawer();
            }
            else
            {
                OpenDrawer(page);
            }
        }

        private void OnDrawerClose(object sender, RoutedEventArgs e) => CloseDrawer();

        /// <summary>Открыть раздел слева. Если слева не хватает экрана — окно плавно отъезжает вправо.</summary>
        private void OpenDrawer(DrawerPage page)
        {
            drawerPage = page;
            DockServers.IsChecked = page == DrawerPage.Servers;
            DockTunnels.IsChecked = page == DrawerPage.Tunnels;
            DockSub.IsChecked = page == DrawerPage.Sub;
            DockSettings.IsChecked = page == DrawerPage.Settings;

            FrameworkElement show = page switch
            {
                DrawerPage.Tunnels => PageTunnels,
                DrawerPage.Sub => PageSub,
                DrawerPage.Settings => PageSettings,
                _ => PageServers
            };

            DrawerTitle.Text = page switch
            {
                DrawerPage.Tunnels => "Туннели",
                DrawerPage.Sub => "Подписка",
                DrawerPage.Settings => "Настройки",
                _ => "Серверы"
            };

            foreach (FrameworkElement p in new FrameworkElement[] { PageServers, PageTunnels, PageSub, PageSettings })
            {
                p.Visibility = p == show ? Visibility.Visible : Visibility.Collapsed;
            }

            PickerOverlay.Visibility = Visibility.Collapsed;
            OnLogClose(null, null);

            if (drawerOpen)
            {
                Fade(show, 1, 160, from: 0);
                return;
            }

            drawerOpen = true;
            EnsureDrawerOnScreen();
            DrawerHost.Visibility = Visibility.Visible;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            DrawerHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

            if (page == DrawerPage.Sub)
            {
                // ширина полосы трафика известна только после раскладки
                Dispatcher.BeginInvoke(new Action(UpdateSubscriptionCard), DispatcherPriority.Loaded);
            }
        }

        private void CloseDrawer()
        {
            if (!drawerOpen)
            {
                return;
            }

            drawerOpen = false;
            DockServers.IsChecked = false;
            DockTunnels.IsChecked = false;
            DockSub.IsChecked = false;
            DockSettings.IsChecked = false;
            logVisible = false;

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var slide = new DoubleAnimation(0, 60, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            slide.Completed += (_, _) =>
            {
                if (!drawerOpen)
                {
                    DrawerHost.Visibility = Visibility.Collapsed;
                }
            };
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, slide);
            DrawerHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)));

            if (drawerShiftApplied > 0)
            {
                double back = drawerShiftApplied;
                drawerShiftApplied = 0;
                AnimateLeft(Left - back);
            }
        }

        /// <summary>Панель не должна уехать за левый край экрана.</summary>
        private void EnsureDrawerOnScreen()
        {
            Rect work = CurrentWorkArea();
            double drawerLeft = Left + ShadowPad;
            double need = work.Left + 8 - drawerLeft;

            if (need > 0)
            {
                drawerShiftApplied = need;
                AnimateLeft(Left + need);
            }
        }

        /// <summary>Рабочая область монитора, на котором окно, — в единицах WPF.</summary>
        private Rect CurrentWorkArea()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                System.Drawing.Rectangle r = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        private void AnimateLeft(double target)
        {
            animatingLeft = true;
            var a = new DoubleAnimation(Left, target, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            a.Completed += (_, _) =>
            {
                // Сначала обычное значение, потом снимаем анимацию — так окно не дёргается.
                Left = target;
                BeginAnimation(LeftProperty, null);
                animatingLeft = false;
            };
            BeginAnimation(LeftProperty, a);
        }

        // ================================================================== кнопки шапки

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e) => HideToTray();

        private void OnPinToggle(object sender, RoutedEventArgs e) => SetTopmost(!settings.Topmost);

        private void SetTopmost(bool on)
        {
            settings.Topmost = on;
            Topmost = on;
            TopmostCheck.IsChecked = on;
            UpdatePinLook();
            settings.Save();
            ShowToast(on ? "Окно поверх остальных" : "Окно как обычно", ToastKind.Info);
        }

        private void UpdatePinLook()
        {
            if (settings.Topmost)
            {
                PinButton.Foreground = (Brush)FindResource("Accent");
                PinButton.ToolTip = "Открепить";
            }
            else
            {
                PinButton.ClearValue(ForegroundProperty);
                PinButton.ToolTip = "Поверх остальных окон";
            }
        }

        // ================================================================== трей

        private void HideToTray()
        {
            CloseDrawer();
            SavePlacement();
            Hide();

            if (!trayHintShown)
            {
                trayHintShown = true;
                tray.ShowBalloonTip(3000, "VPN работает в трее",
                    "Клик по значку — открыть окно. Ctrl + Alt + V — включить/выключить. Выход — правой кнопкой.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            Show();

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
        }

        private void CreateTray()
        {
            BuildTrayIcons();

            tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "VPN — отключено",
                Visible = true,
                Icon = trayIcons.TryGetValue("off", out System.Drawing.Icon off) ? off : System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            trayStatus = new System.Windows.Forms.ToolStripMenuItem("Отключено") { Enabled = false };
            menu.Items.Add(trayStatus);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Открыть", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
            trayToggle = new System.Windows.Forms.ToolStripMenuItem("Подключить", null,
                (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ToggleAsync())));
            menu.Items.Add(trayToggle);

            // Серверы прямо из трея: список собираем при каждом открытии — он мог поменяться.
            trayServers = new System.Windows.Forms.ToolStripMenuItem("Сервер");
            trayServers.DropDownItems.Add("…");
            trayServers.DropDownOpening += (_, _) => FillTrayServers();
            menu.Items.Add(trayServers);

            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ExitAsync())));
            tray.ContextMenuStrip = menu;

            // Клик левой кнопкой — показать окно, ещё клик — спрятать.
            tray.MouseClick += (_, args) =>
            {
                if (args.Button != System.Windows.Forms.MouseButtons.Left)
                {
                    return;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (IsVisible && IsActive && WindowState != WindowState.Minimized)
                    {
                        HideToTray();
                    }
                    else
                    {
                        ShowFromTray();
                    }
                }));
            };

            UpdateTray();
        }

        private void FillTrayServers()
        {
            trayServers.DropDownItems.Clear();

            if (servers.Count <= 1)
            {
                trayServers.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("Нет серверов — добавь подписку") { Enabled = false });
                return;
            }

            var order = new ServerOrder(false);
            List<ServerItem> list = servers.ToList();
            list.Sort(order.Compare);
            int shown = 0;

            foreach (ServerItem item in list)
            {
                if (shown++ >= 60)
                {
                    break;
                }

                string label = item.Tag == "auto"
                    ? "Самый быстрый (авто)"
                    : (item.IsFavorite ? "★ " : "") + item.Title + (item.PingText.Length > 0 && item.PingText != "…" ? "   " + item.PingText : "");
                ServerItem target = item;
                var menuItem = new System.Windows.Forms.ToolStripMenuItem(label) { Checked = item.IsSelected };
                menuItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ChooseServer(target)));
                trayServers.DropDownItems.Add(menuItem);

                if (item.Tag == "auto")
                {
                    trayServers.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
                }
            }
        }

        /// <summary>Значок в трее с цветной точкой статуса — видно, включён ли VPN, не открывая окно.</summary>
        private void BuildTrayIcons()
        {
            try
            {
                using System.Drawing.Icon baseIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");

                if (baseIcon == null)
                {
                    return;
                }

                using System.Drawing.Bitmap baseBitmap = baseIcon.ToBitmap();
                var colors = new Dictionary<string, System.Drawing.Color>
                {
                    ["off"] = System.Drawing.Color.FromArgb(0x7A, 0x84, 0x99),
                    ["on"] = System.Drawing.Color.FromArgb(0x3D, 0xDC, 0x97),
                    ["busy"] = System.Drawing.Color.FromArgb(0xFF, 0xB3, 0x47),
                    ["error"] = System.Drawing.Color.FromArgb(0xFF, 0x5C, 0x6C)
                };

                foreach (var pair in colors)
                {
                    using var bitmap = new System.Drawing.Bitmap(32, 32);

                    using (var g = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(baseBitmap, 0, 0, 32, 32);

                        using var outline = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x0D, 0x10, 0x15));
                        using var fill = new System.Drawing.SolidBrush(pair.Value);
                        g.FillEllipse(outline, 17, 17, 15, 15);
                        g.FillEllipse(fill, 19.5f, 19.5f, 10, 10);
                    }

                    // Хэндл значка живёт всё время работы программы — освобождать не нужно, их всего четыре.
                    trayIcons[pair.Key] = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
                }
            }
            catch (Exception e)
            {
                Log.Write("Значки трея: " + e.Message);
            }
        }

        private void UpdateTray()
        {
            if (tray == null)
            {
                return;
            }

            string key = state switch
            {
                State.On when noResponseWarned => "busy",
                State.On => "on",
                State.Off when lastError != null => "error",
                State.Off => "off",
                _ => "busy"
            };

            if (trayIcons.TryGetValue(key, out System.Drawing.Icon icon) && tray.Icon != icon)
            {
                tray.Icon = icon;
            }

            string status = state switch
            {
                State.On when noResponseWarned => "Нет ответа от сервера",
                State.On => "Подключено · " + ActualServerTitle() + (lastDelay.HasValue ? " · " + lastDelay + " мс" : ""),
                State.Connecting => "Подключение…",
                State.Reconnecting => "Переподключение…",
                State.Switching => "Смена сервера…",
                _ => lastError != null ? "Ошибка подключения" : "Отключено"
            };

            tray.Text = Shorten("VPN — " + status, 63);
            trayStatus.Text = status;
            trayToggle.Text = state == State.Off ? "Подключить" : "Отключить";
        }

        /// <summary>
        /// Системное уведомление Windows — только когда окно не перед глазами.
        /// Если окно открыто и активно, хватает всплывашки внутри него.
        /// </summary>
        private void SystemNotify(string title, string text, System.Windows.Forms.ToolTipIcon icon)
        {
            if (!settings.Notifications || tray == null || reallyExit)
            {
                return;
            }

            if (IsVisible && IsActive && WindowState != WindowState.Minimized)
            {
                return;
            }

            try
            {
                tray.ShowBalloonTip(4000, title, string.IsNullOrWhiteSpace(text) ? " " : text, icon);
            }
            catch (Exception e)
            {
                Log.Write("Уведомление: " + e.Message);
            }
        }

        private async Task ExitAsync()
        {
            reallyExit = true;
            tick.Stop();
            SavePlacement();
            UnregisterHotkey();

            try
            {
                await DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(6));
            }
            catch
            {
                // не дождались — всё равно выходим
            }

            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!reallyExit)
            {
                // Alt+F4 прячет в трей.
                e.Cancel = true;
                HideToTray();
            }

            base.OnClosing(e);
        }

        // ================================================================== горячая клавиша

        private const int HotkeyId = 0xB001;
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private const uint VkV = 0x56;
        private bool hotkeyRegistered;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private void ApplyHotkey()
        {
            UnregisterHotkey();
            HotkeyNote.Visibility = Visibility.Collapsed;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            if (!settings.Hotkey || hwnd == IntPtr.Zero)
            {
                return;
            }

            hotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkV);

            if (!hotkeyRegistered)
            {
                HotkeyNote.Text = "Ctrl + Alt + V уже занято другой программой — сочетание не сработает.";
                HotkeyNote.Visibility = Visibility.Visible;
                Log.Write("Горячая клавиша Ctrl+Alt+V занята (" + Marshal.GetLastWin32Error() + ")");
            }
        }

        private void UnregisterHotkey()
        {
            if (!hotkeyRegistered)
            {
                return;
            }

            UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            hotkeyRegistered = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Dispatcher.BeginInvoke(new Action(async () => await ToggleAsync()));
            }

            return IntPtr.Zero;
        }
    }
}
