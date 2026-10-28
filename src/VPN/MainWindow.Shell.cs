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
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VPN.Core;

namespace VPN
{
    /// <summary>
    /// Всё, что касается самого окна: фигурная форма и неоновое свечение, разделы, положение на экране,
    /// трей, системные уведомления, горячая клавиша, ссылки из других программ и буфер обмена.
    /// </summary>
    public partial class MainWindow
    {
        private enum Page
        {
            Servers,
            Tunnels,
            Subs,
            Settings
        }

        /// <summary>Отступ вокруг корпуса под свечение и тень (Margin корневой сетки).</summary>
        private const double GlowPad = 22;

        /// <summary>Ширина правой колонки (кнопка и статус) — как в разметке.</summary>
        private const double HeroWidth = 372;

        private readonly DispatcherTimer placementSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private Page page;
        private bool reallyExit;
        private bool trayHintShown;
        private string lastClipboardOffer = "";

        private System.Windows.Forms.NotifyIcon tray;
        private System.Windows.Forms.ToolStripMenuItem trayToggle;
        private System.Windows.Forms.ToolStripMenuItem trayStatus;
        private System.Windows.Forms.ToolStripMenuItem trayServers;
        private System.Windows.Forms.ToolStripMenuItem trayProfiles;
        private readonly Dictionary<string, System.Drawing.Icon> trayIcons = new Dictionary<string, System.Drawing.Icon>();

        // ================================================================== запуск окна

        private void InitShell()
        {
            // «Живой» цвет: своя, не замороженная кисть, чтобы её можно было плавно перекрашивать.
            liveBrush = new SolidColorBrush(Violet);
            Resources["Live"] = liveBrush;

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

            // Свечение крутится только когда окно видно — в трее оно ничего не тратит.
            IsVisibleChanged += (_, _) => UpdateGlowMotion();
            StateChanged += (_, _) => UpdateGlowMotion();

            Activated += (_, _) => OfferClipboard();

            PreviewKeyDown += OnWindowKey;
        }

        private void OnWindowKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
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
                    HideToast();
                }

                e.Handled = true;
                return;
            }

            // Ctrl+V вне поля ввода — добавить ссылку из буфера (подписку, сервер, профиль).
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBoxBase)
            {
                e.Handled = true;
                _ = ImportClipboardAsync(explicitRequest: true);
                return;
            }

            // Ctrl+1…4 — разделы.
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key >= Key.D1 && e.Key <= Key.D4)
            {
                OpenPage((Page)(e.Key - Key.D1));
                e.Handled = true;
            }
        }

        /// <summary>Возвращаем окно туда, где оно было; если того экрана уже нет — по центру.</summary>
        private void RestorePlacement()
        {
            Rect work = SystemParameters.WorkArea;
            Width = settings.WindowWidth >= MinWidth ? settings.WindowWidth : Math.Min(Width, work.Width + 2 * GlowPad);
            Height = settings.WindowHeight >= MinHeight ? settings.WindowHeight : Math.Min(Height, work.Height + 2 * GlowPad);

            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            bool saved = !double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop) &&
                         screen.IntersectsWith(new Rect(settings.WindowLeft + GlowPad, settings.WindowTop + GlowPad, 200, 120));

            Left = saved ? settings.WindowLeft : work.Left + (work.Width - Width) / 2;
            Top = saved ? settings.WindowTop : work.Top + (work.Height - Height) / 2;
        }

        private void SavePlacement()
        {
            if (WindowState != WindowState.Normal || !IsLoaded)
            {
                return;
            }

            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.Save();
        }

        // ================================================================== форма корпуса

        private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
        {
            Geometry shape = BuildBodyShape(BodyHost.ActualWidth, BodyHost.ActualHeight, HeroWidth);
            BodyGlow.Data = shape;
            BodyShadow.Data = shape;
            BodyFill.Data = shape;
            BodyRim.Data = shape;
            BodyEdge.Data = shape;
            BodyContent.Clip = shape;
        }

        /// <summary>
        /// Корпус: правая колонка приподнята сверху на <c>rise</c>, а левая — опущена снизу на столько же.
        /// Получается «ступенька» с плавными S-образными переходами, без единого острого угла.
        /// </summary>
        private static Geometry BuildBodyShape(double w, double h, double heroWidth)
        {
            const double rise = 18;   // высота ступеньки
            const double radius = 28; // скругление углов
            const double curve = 30;  // ширина плавного перехода

            double x0 = w - heroWidth;

            if (w < heroWidth + 2 * radius + curve || h < 2 * (rise + radius) + 1)
            {
                return Geometry.Empty;
            }

            var g = new StreamGeometry();

            using (StreamGeometryContext c = g.Open())
            {
                // верх: левая часть ниже, правая выше
                c.BeginFigure(new Point(radius, rise), true, true);
                c.LineTo(new Point(x0 - curve, rise), true, true);
                c.BezierTo(new Point(x0 - curve * 0.45, rise), new Point(x0 - curve * 0.55, 0), new Point(x0, 0), true, true);
                c.LineTo(new Point(w - radius, 0), true, true);
                c.ArcTo(new Point(w, radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);

                // правый край и низ правой колонки (она короче снизу)
                c.LineTo(new Point(w, h - rise - radius), true, true);
                c.ArcTo(new Point(w - radius, h - rise), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(x0, h - rise), true, true);
                c.BezierTo(new Point(x0 - curve * 0.45, h - rise), new Point(x0 - curve * 0.55, h), new Point(x0 - curve, h), true, true);

                // низ левой части и левый край
                c.LineTo(new Point(radius, h), true, true);
                c.ArcTo(new Point(0, h - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(0, rise + radius), true, true);
                c.ArcTo(new Point(radius, rise), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            }

            g.Freeze();
            return g;
        }

        // ================================================================== свечение (RTX)

        private SolidColorBrush liveBrush;
        private double glowPeriod = -1;
        private bool breathing;

        /// <summary>Как сейчас должен выглядеть свет: два цвета градиента, яркость и скорость «бега» по кромке.</summary>
        private (Color A, Color B, double Strength, double Period, bool Breathe) CurrentLook()
        {
            Color cyan = Color.FromRgb(0x22, 0xD3, 0xEE);
            Color pink = Color.FromRgb(0xFF, 0x5F, 0xA2);
            Color lime = Color.FromRgb(0xA3, 0xE6, 0x35);
            Color orange = Color.FromRgb(0xFF, 0x7A, 0x3D);

            switch (state)
            {
                case State.Connecting:
                case State.Reconnecting:
                    return (Amber, pink, 0.85, 2.2, false);
                case State.Switching:
                    return (Blue, Violet, 0.8, 2.6, false);
                case State.On when noResponseWarned:
                    return (Amber, orange, 0.8, 3.0, false);
                case State.On:
                    // Цвет зависит от качества связи: быстрый — зелёно-бирюзовый, средний — с лаймом, медленный — тёплый.
                    if (lastDelay is int d && d >= 250)
                    {
                        return (Green, Amber, 0.7, 5.0, true);
                    }

                    if (lastDelay is int m && m >= 120)
                    {
                        return (Green, lime, 0.7, 6.0, true);
                    }

                    return (Green, cyan, 0.72, 7.0, true);
                default:
                    return lastError != null ? (Red, orange, 0.7, 4.0, false) : (Violet, Blue, 0.42, 10.0, false);
            }
        }

        /// <summary>Перекрасить всё «светящееся»: кромку, ореол, логотип, разделитель, кольцо кнопки, шкалу.</summary>
        private void ApplyLook(bool animate)
        {
            if (liveBrush == null)
            {
                return;
            }

            var (a, b, strength, period, breathe) = CurrentLook();
            Duration d = TimeSpan.FromMilliseconds(animate ? 600 : 1);

            void Paint(Animatable target, DependencyProperty property, Color to) =>
                target.BeginAnimation(property, new ColorAnimation(to, d) { EasingFunction = new CubicEase() });

            Paint(liveBrush, SolidColorBrush.ColorProperty, a);
            Paint(GlowStop1, GradientStop.ColorProperty, a);
            Paint(GlowStop2, GradientStop.ColorProperty, b);
            Paint(GlowStop3, GradientStop.ColorProperty, a);
            Paint(LogoStop, GradientStop.ColorProperty, a);
            Paint(LogoGlow, DropShadowEffect.ColorProperty, a);
            Paint(DividerStop, GradientStop.ColorProperty, WithAlpha(a, 0x66));
            Paint(AuraStop, GradientStop.ColorProperty, WithAlpha(a, (byte)(state == State.Off && lastError == null ? 0x26 : 0x34)));
            Paint(GlowStop, GradientStop.ColorProperty, WithAlpha(a, 0x66));
            Paint(RingGlow, DropShadowEffect.ColorProperty, a);
            Paint(IconGlow, DropShadowEffect.ColorProperty, a);
            Paint(TickStop1, GradientStop.ColorProperty, a);
            Paint(TickStop2, GradientStop.ColorProperty, b);

            BodyGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(strength, d));
            bool lit = state != State.Off || lastError != null;
            RingGlow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(lit ? 0.75 : 0, d));
            IconGlow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(state == State.On ? 0.9 : lit ? 0.5 : 0, d));
            TickLive.BeginAnimation(OpacityProperty, new DoubleAnimation(state == State.Off ? 0 : 0.9, d));

            glowPeriodWanted = period;
            breathing = breathe;
            UpdateGlowMotion();
        }

        private double glowPeriodWanted = 10;

        /// <summary>Запустить или остановить бег света по кромке, вращение шкалы и «дыхание» кнопки.</summary>
        private void UpdateGlowMotion()
        {
            bool visible = IsVisible && WindowState != WindowState.Minimized;

            if (!visible)
            {
                GlowRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                TickRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                PowerBreath(false);
                glowPeriod = -1;
                return;
            }

            if (Math.Abs(glowPeriod - glowPeriodWanted) > 0.01)
            {
                glowPeriod = glowPeriodWanted;
                double from = GlowRotate.Angle % 360;
                var spin = new DoubleAnimation(from, from + 360, TimeSpan.FromSeconds(glowPeriod)) { RepeatBehavior = RepeatBehavior.Forever };
                // 30 кадров хватает для плавного света и вдвое меньше нагрузки.
                Timeline.SetDesiredFrameRate(spin, 30);
                GlowRotate.BeginAnimation(RotateTransform.AngleProperty, spin);

                var ticks = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(glowPeriod * 3)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(ticks, 30);
                TickRotate.BeginAnimation(RotateTransform.AngleProperty, state == State.Off ? null : ticks);
            }

            PowerBreath(breathing && state == State.On);
        }

        /// <summary>Сейчас «дышит» (true), стоит (false) или ещё не выставлено (null).</summary>
        private bool? breathOn;

        /// <summary>Мягкая «пульсация» свечения кнопки, пока подключено.</summary>
        private void PowerBreath(bool on)
        {
            if (breathOn == on)
            {
                return;
            }

            breathOn = on;

            if (on)
            {
                var breath = new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(2.4))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Timeline.SetDesiredFrameRate(breath, 30);
                Glow.BeginAnimation(OpacityProperty, breath);
            }
            else
            {
                double rest = state == State.Switching ? 0.5 : state == State.On ? 0.4 : 0;
                Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(rest, TimeSpan.FromMilliseconds(400)));
            }
        }

        private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        // ================================================================== перетаскивание

        /// <summary>Окно тянется за любое «пустое» место корпуса, рельсы и шапок.</summary>
        private void OnDragArea(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed || e.Handled)
            {
                return;
            }

            for (DependencyObject d = e.OriginalSource as DependencyObject; d != null; d = TreeParent(d))
            {
                if (d == PowerButton || d is ButtonBase || d is TextBoxBase || d is ScrollBar || d is Thumb || d is ListBoxItem || d is ScrollViewer)
                {
                    return;
                }

                if (d == sender)
                {
                    break;
                }
            }

            e.Handled = true;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // кнопку уже отпустили
            }
        }

        private static DependencyObject TreeParent(DependencyObject d) =>
            d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        // ================================================================== разделы

        private void OnNavClick(object sender, RoutedEventArgs e)
        {
            Page target = sender == NavTunnels ? Page.Tunnels
                : sender == NavSubs ? Page.Subs
                : sender == NavSettings ? Page.Settings
                : Page.Servers;
            OpenPage(target);
        }

        private void OpenPage(Page target)
        {
            bool changed = target != page || !IsLoaded;
            page = target;
            NavServers.IsChecked = target == Page.Servers;
            NavTunnels.IsChecked = target == Page.Tunnels;
            NavSubs.IsChecked = target == Page.Subs;
            NavSettings.IsChecked = target == Page.Settings;

            FrameworkElement show = target switch
            {
                Page.Tunnels => PageTunnels,
                Page.Subs => PageSubs,
                Page.Settings => PageSettings,
                _ => PageServers
            };

            PageTitle.Text = target switch
            {
                Page.Tunnels => "Туннели",
                Page.Subs => "Подписки",
                Page.Settings => "Настройки",
                _ => "Серверы"
            };

            foreach (FrameworkElement p in new FrameworkElement[] { PageServers, PageTunnels, PageSubs, PageSettings })
            {
                p.Visibility = p == show ? Visibility.Visible : Visibility.Collapsed;
            }

            PickerOverlay.Visibility = Visibility.Collapsed;
            OnLogClose(null, null);
            UpdatePageHint();

            if (changed)
            {
                // Плавное появление со сдвигом снизу.
                var shift = new TranslateTransform(0, 10);
                show.RenderTransform = shift;
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                Fade(show, 1, 220, from: 0);
            }
        }

        /// <summary>Подзаголовок раздела: что в нём сейчас.</summary>
        private void UpdatePageHint()
        {
            switch (page)
            {
                case Page.Servers:
                    int subs = settings.Subscriptions.Count(s => s.Servers.Count > 0);
                    PageHint.Text = settings.Servers.Count == 0
                        ? "добавь подписку или ссылку на сервер"
                        : settings.Servers.Count + " " + Plural(settings.Servers.Count, "сервер", "сервера", "серверов") +
                          (subs > 1 ? " в " + subs + " " + Plural(subs, "подписке", "подписках", "подписках") : "");
                    break;
                case Page.Tunnels:
                    PageHint.Text = "профиль «" + settings.Active.Name + "» · " + (settings.DefaultViaVpn ? "всё через VPN" : "только выбранное");
                    break;
                case Page.Subs:
                    PageHint.Text = "Ctrl + V в любом месте окна — добавить ссылку из буфера";
                    break;
                default:
                    PageHint.Text = "запуск, подключение, ссылки";
                    break;
            }
        }

        private static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10;
            int m100 = n % 100;
            return m10 == 1 && m100 != 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        }

        // ================================================================== кнопки окна

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
                PinButton.Foreground = (Brush)FindResource("Accent2");
                PinButton.ToolTip = "Открепить";
            }
            else
            {
                PinButton.ClearValue(ForegroundProperty);
                PinButton.ToolTip = "Поверх остальных окон";
            }
        }

        // ================================================================== внешние ссылки и буфер обмена

        /// <summary>Зарегистрировать свои ссылки shxvpn:// и, если можно, happ://.</summary>
        private void RegisterLinkSchemes(bool force)
        {
            Task.Run(() =>
            {
                UrlScheme.Register(UrlScheme.Own, force: true);

                if (settings.HandleHappLinks)
                {
                    UrlScheme.Register(UrlScheme.Happ, force);
                }
            });
        }

        /// <summary>Сообщение от второго запуска программы: показать окно и/или обработать ссылку.</summary>
        public void OnExternalMessage(string message)
        {
            ShowFromTray();

            if (!string.IsNullOrWhiteSpace(message) && message != "show")
            {
                _ = HandleExternalAsync(message);
            }
        }

        /// <summary>В буфере ссылка, которую можно добавить, — предложим (один раз на каждую новую).</summary>
        private void OfferClipboard()
        {
            if (!settings.WatchClipboard || loading)
            {
                return;
            }

            string text;

            try
            {
                if (!Clipboard.ContainsText())
                {
                    return;
                }

                text = Clipboard.GetText().Trim();
            }
            catch
            {
                return;
            }

            if (text.Length == 0 || text == lastClipboardOffer || !LinkImport.LooksImportable(text) || AlreadyKnown(text))
            {
                return;
            }

            lastClipboardOffer = text;
            string what = text.StartsWith("happ://routing", StringComparison.OrdinalIgnoreCase) ? "профиль маршрутизации"
                : text.Contains("://") && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("happ://add", StringComparison.OrdinalIgnoreCase) ? "сервер"
                : "подписку";
            ShowToast("В буфере есть ссылка — добавить " + what + "?", ToastKind.Info, "Добавить", () => _ = ImportTextAsync(text));
        }

        // ================================================================== трей

        private void HideToTray()
        {
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

            // Серверы и профили прямо из трея: списки собираем при каждом открытии — они могли поменяться.
            trayServers = new System.Windows.Forms.ToolStripMenuItem("Сервер");
            trayServers.DropDownItems.Add("…");
            trayServers.DropDownOpening += (_, _) => FillTrayServers();
            menu.Items.Add(trayServers);

            trayProfiles = new System.Windows.Forms.ToolStripMenuItem("Маршрутизация");
            trayProfiles.DropDownItems.Add("…");
            trayProfiles.DropDownOpening += (_, _) => FillTrayProfiles();
            menu.Items.Add(trayProfiles);

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
            string lastGroup = null;

            foreach (ServerItem item in list)
            {
                if (shown++ >= 80)
                {
                    break;
                }

                if (item.Tag != "auto" && item.GroupKey != lastGroup && lastGroup != null)
                {
                    trayServers.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
                    trayServers.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem(item.GroupKey) { Enabled = false });
                }

                lastGroup = item.GroupKey;
                string label = item.Tag == "auto"
                    ? "Самый быстрый (авто)"
                    : (item.IsFavorite ? "★ " : "") + item.Title + (item.PingText.Length > 0 && item.PingText != "…" ? "   " + item.PingText : "");
                ServerItem target = item;
                var menuItem = new System.Windows.Forms.ToolStripMenuItem(label) { Checked = item.IsSelected };
                menuItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ChooseServer(target)));
                trayServers.DropDownItems.Add(menuItem);
            }
        }

        private void FillTrayProfiles()
        {
            trayProfiles.DropDownItems.Clear();

            foreach (RoutingProfile p in settings.Profiles)
            {
                RoutingProfile target = p;
                var item = new System.Windows.Forms.ToolStripMenuItem(p.Name) { Checked = p.Id == settings.ActiveProfileId };
                item.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => SwitchProfile(target)));
                trayProfiles.DropDownItems.Add(item);
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
                    ["off"] = System.Drawing.Color.FromArgb(0x8B, 0x6C, 0xFF),
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

                        using var outline = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x10, 0x13, 0x1B));
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
