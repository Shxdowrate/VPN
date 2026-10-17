using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VPN.Core;

namespace VPN
{
    public partial class MainWindow : Window
    {
        private enum State
        {
            Off,
            Connecting,
            On
        }

        private readonly AppSettings settings;
        private readonly CoreRunner runner = new CoreRunner();
        private readonly ObservableCollection<ServerItem> servers = new ObservableCollection<ServerItem>();
        private readonly ObservableCollection<RuleItem> apps = new ObservableCollection<RuleItem>();
        private readonly ObservableCollection<RuleItem> sites = new ObservableCollection<RuleItem>();
        private readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private readonly DispatcherTimer subTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        private readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly Queue<double> downHistory = new Queue<double>();
        private readonly Queue<double> upHistory = new Queue<double>();
        private readonly Queue<bool> pingHistory = new Queue<bool>();

        private State state = State.Off;
        private CoreApi api;
        private CancellationTokenSource sessionCts;
        private DateTime connectedAt;
        private int tickCount;
        private bool loading = true;
        private bool busy;
        private bool reallyExit;
        private bool trayHintShown;
        private System.Windows.Forms.NotifyIcon tray;
        private System.Windows.Forms.ToolStripMenuItem trayToggle;
        private Storyboard pulse;
        private List<ProcessCandidate> candidates = new List<ProcessCandidate>();

        public MainWindow(bool startMinimized)
        {
            InitializeComponent();
            settings = AppSettings.Load();

            ServersList.ItemsSource = servers;
            AppsList.ItemsSource = apps;
            SitesList.ItemsSource = sites;

            tick.Tick += OnTick;
            applyDebounce.Tick += async (_, _) =>
            {
                applyDebounce.Stop();
                await ApplyChangesAsync();
            };
            subTimer.Tick += async (_, _) => await MaybeRefreshSubscriptionAsync(false);
            toastTimer.Tick += (_, _) =>
            {
                toastTimer.Stop();
                Fade(Toast, 0, 250);
            };

            runner.Crashed += tail => Dispatcher.Invoke(async () =>
            {
                if (state != State.Off)
                {
                    await DisconnectAsync();
                    ShowToast("VPN отключился: " + FirstLine(tail));
                }
            });

            Log.Added += line => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PageLog.Visibility == Visibility.Visible)
                {
                    LogBox.AppendText(line + Environment.NewLine);
                    LogBox.ScrollToEnd();
                }
            }));

            LoadIntoUi();
            CreateTray();
            SetState(State.Off, animate: false);
            loading = false;

            Loaded += async (_, _) => await OnStartedAsync();

            if (startMinimized)
            {
                // Окно не показываем, но запускаем всё, что нужно (автоподключение, подписка).
                Dispatcher.BeginInvoke(new Action(async () => await OnStartedAsync()), DispatcherPriority.ApplicationIdle);
            }

            subTimer.Start();
        }

        private bool started;

        private async Task OnStartedAsync()
        {
            if (started)
            {
                return;
            }

            started = true;
            await MaybeRefreshSubscriptionAsync(false);

            if (settings.AutoConnect && servers.Count > 0)
            {
                await ConnectAsync();
            }
            else
            {
                _ = PingAllAsync();
            }
        }

        // ================================================================== загрузка в интерфейс

        private void LoadIntoUi()
        {
            DefaultVpn.IsChecked = settings.DefaultViaVpn;
            DefaultDirect.IsChecked = !settings.DefaultViaVpn;
            RussiaCheck.IsChecked = settings.RussiaDirect;
            AdsCheck.IsChecked = settings.BlockAds;
            UpdateDefaultHint();

            foreach (RouteRule rule in settings.Rules)
            {
                (rule.Kind == RuleKind.App ? apps : sites).Add(new RuleItem(rule));
            }

            UpdateRuleEmpties();

            SubUrl.Text = settings.SubscriptionUrl;
            Upd6.IsChecked = settings.SubscriptionUpdateHours == 6;
            Upd12.IsChecked = settings.SubscriptionUpdateHours == 12;
            Upd24.IsChecked = settings.SubscriptionUpdateHours == 24;

            AutostartCheck.IsChecked = settings.Autostart;
            MinimizedCheck.IsChecked = settings.StartMinimized;
            AutoConnectCheck.IsChecked = settings.AutoConnect;

            string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
            AboutText.Text = "VPN " + version + " — свой клиент на движке sing-box.\n" +
                             "Работает через виртуальный сетевой адаптер, поэтому просит права администратора.";

            RebuildServers();
            UpdateSubscriptionCard();
        }

        private void RebuildServers()
        {
            string current = settings.SelectedServer;
            servers.Clear();
            servers.Add(new ServerItem
            {
                Tag = "auto",
                Code = "AUTO",
                Title = "Самый быстрый",
                Subtitle = "выбирается сам",
                IsSelected = current == "auto" || settings.Servers.All(s => s.Tag != current)
            });

            foreach (ServerProfile s in settings.Servers)
            {
                string code = Subscription.CountryCode(s.Name);
                servers.Add(new ServerItem
                {
                    Tag = s.Tag,
                    Code = code.Length > 0 ? code : "··",
                    Title = Subscription.CleanName(s.Name),
                    Subtitle = s.Protocol + " · " + s.Port,
                    Host = s.Host,
                    Port = s.Port,
                    IsSelected = s.Tag == current
                });
            }

            ServerCount.Text = settings.Servers.Count > 0 ? settings.Servers.Count.ToString() : "добавь подписку в меню слева";
        }

        private void UpdateSubscriptionCard()
        {
            SubTitleText.Text = string.IsNullOrWhiteSpace(settings.SubscriptionTitle) ? "Подписка" : settings.SubscriptionTitle;

            if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            {
                SubTrafficText.Text = "не добавлена";
                SubExpireText.Text = "";
                SubBar.Width = 0;
                SubInfo.Text = "Вставь ссылку-подписку от своего VPN-сервиса.";
                return;
            }

            if (settings.TrafficTotal > 0)
            {
                SubTrafficText.Text = Format.Size(settings.TrafficUsed) + " из " + Format.Size(settings.TrafficTotal);
                double fraction = Math.Clamp((double)settings.TrafficUsed / settings.TrafficTotal, 0, 1);
                AnimateWidth(SubBar, 248 * fraction);
            }
            else
            {
                SubTrafficText.Text = Format.Size(settings.TrafficUsed) + " · безлимит";
                AnimateWidth(SubBar, 248);
            }

            if (settings.ExpireUnix > 0)
            {
                DateTime expire = DateTimeOffset.FromUnixTimeSeconds(settings.ExpireUnix).LocalDateTime;
                int days = (int)Math.Ceiling((expire - DateTime.Now).TotalDays);
                SubExpireText.Text = days > 0 ? "до " + expire.ToString("dd.MM") + " · " + days + " дн." : "истекла";
            }
            else
            {
                SubExpireText.Text = "";
            }

            SubInfo.Text = "Серверов: " + settings.Servers.Count +
                           (settings.LastSubscriptionUpdate > DateTime.MinValue ? "\nОбновлено: " + settings.LastSubscriptionUpdate.ToString("dd.MM HH:mm") : "");
        }

        // ================================================================== подключение

        private async void OnPowerUp(object sender, MouseButtonEventArgs e)
        {
            AnimateScale(PowerScale, 1.03, 120);

            if (busy)
            {
                return;
            }

            if (state == State.Off)
            {
                await ConnectAsync();
            }
            else
            {
                await DisconnectAsync();
            }
        }

        private async Task ConnectAsync()
        {
            if (busy)
            {
                return;
            }

            busy = true;
            SetState(State.Connecting);

            try
            {
                if (settings.Servers.Count == 0)
                {
                    await MaybeRefreshSubscriptionAsync(true);
                }

                string error = await runner.StartAsync(settings);

                if (error != null)
                {
                    Log.Write(error);
                    SetState(State.Off);
                    SubStatus.Text = error.Length > 220 ? error.Substring(0, 220) + "…" : error;
                    return;
                }

                api?.Dispose();
                api = new CoreApi(runner.ApiSecret);
                sessionCts = new CancellationTokenSource();
                connectedAt = DateTime.Now;
                downHistory.Clear();
                upHistory.Clear();
                pingHistory.Clear();
                tickCount = 0;

                CancellationToken token = sessionCts.Token;
                _ = Task.Run(() => api.StreamTrafficAsync((up, down) => Dispatcher.BeginInvoke(new Action(() => OnTraffic(up, down))), token));

                SetState(State.On);
                tick.Start();
                _ = MeasurePingAsync();
                _ = PingAllAsync();
            }
            finally
            {
                busy = false;
            }
        }

        private async Task DisconnectAsync()
        {
            busy = true;

            try
            {
                tick.Stop();
                sessionCts?.Cancel();
                sessionCts = null;
                await runner.StopAsync();
                api?.Dispose();
                api = null;
                SetState(State.Off);
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>Правила поменялись: если подключены — тихо перезапускаем движок.</summary>
        private async Task ApplyChangesAsync()
        {
            settings.Save();

            if (state != State.On || busy)
            {
                return;
            }

            busy = true;

            try
            {
                sessionCts?.Cancel();
                string error = await runner.StartAsync(settings);

                if (error != null)
                {
                    Log.Write(error);
                    busy = false;
                    await DisconnectAsync();
                    SubStatus.Text = error;
                    return;
                }

                api?.Dispose();
                api = new CoreApi(runner.ApiSecret);
                sessionCts = new CancellationTokenSource();
                CancellationToken token = sessionCts.Token;
                _ = Task.Run(() => api.StreamTrafficAsync((up, down) => Dispatcher.BeginInvoke(new Action(() => OnTraffic(up, down))), token));
                ShowToast("Правила применены");
            }
            finally
            {
                busy = false;
            }
        }

        private void ScheduleApply()
        {
            if (loading)
            {
                return;
            }

            settings.Rules = apps.Concat(sites).Select(r => r.Rule).ToList();
            settings.Save();
            applyDebounce.Stop();
            applyDebounce.Start();
        }

        // ================================================================== статистика

        private void OnTraffic(long up, long down)
        {
            if (state != State.On)
            {
                return;
            }

            DownText.Text = Format.Speed(down);
            UpText.Text = Format.Speed(up);
            Push(downHistory, down);
            Push(upHistory, up);
            DrawSpark(DownLine, downHistory);
            DrawSpark(UpLine, upHistory);
        }

        private static void Push(Queue<double> q, double v)
        {
            q.Enqueue(v);

            while (q.Count > 40)
            {
                q.Dequeue();
            }
        }

        private static void DrawSpark(System.Windows.Shapes.Polyline line, Queue<double> values)
        {
            double max = Math.Max(values.DefaultIfEmpty(0).Max(), 1);
            var points = new PointCollection();
            int i = 0;
            int count = values.Count;

            foreach (double v in values)
            {
                // Новые точки справа, график «въезжает» слева направо.
                double x = line.Width * (i + 40 - count) / 39.0;
                double y = line.Height - 2 - (line.Height - 4) * v / max;
                points.Add(new Point(x, y));
                i++;
            }

            line.Points = points;
        }

        private async void OnTick(object sender, EventArgs e)
        {
            if (state != State.On || api == null)
            {
                return;
            }

            tickCount++;
            UptimeText.Text = Format.Duration(DateTime.Now - connectedAt);

            if (tickCount % 3 == 0)
            {
                var (up, down) = await api.TotalsAsync();

                if (api != null)
                {
                    TotalText.Text = Format.Size(up + down);
                }
            }

            if (tickCount % 5 == 0)
            {
                await MeasurePingAsync();
            }
        }

        private async Task MeasurePingAsync()
        {
            if (api == null)
            {
                return;
            }

            int? delay = await api.DelayAsync("proxy");
            string current = await api.CurrentAsync();

            if (state != State.On)
            {
                return;
            }

            pingHistory.Enqueue(delay.HasValue);

            while (pingHistory.Count > 20)
            {
                pingHistory.Dequeue();
            }

            PingText.Text = delay.HasValue ? delay + " мс" : "нет ответа";
            PingText.Foreground = PingBrush(delay);
            int lost = pingHistory.Count(ok => !ok);
            LossText.Text = (pingHistory.Count == 0 ? 0 : lost * 100 / pingHistory.Count) + " %";
            LossText.Foreground = lost == 0 ? (Brush)FindResource("Text") : (Brush)FindResource(lost * 4 > pingHistory.Count ? "Danger" : "Warn");

            ServerProfile server = settings.Servers.FirstOrDefault(s => s.Tag == current);

            if (server != null)
            {
                string name = Subscription.CleanName(server.Name);
                SubStatus.Text = settings.SelectedServer == "auto" ? "Авто → " + name : name;
            }
        }

        private async Task PingAllAsync()
        {
            PingAllButton.IsEnabled = false;

            try
            {
                CoreApi current = api;
                var tasks = servers.Where(s => s.Tag != "auto").Select(async item =>
                {
                    item.PingText = "…";
                    item.PingBrush = (Brush)FindResource("Faint");
                    int? ms = current != null && state == State.On
                        ? await current.DelayAsync(item.Tag)
                        : await CoreApi.TcpPingAsync(item.Host, item.Port);
                    item.PingText = ms.HasValue ? ms + " мс" : "✕";
                    item.PingBrush = PingBrush(ms);
                    item.Ping = ms ?? int.MaxValue;
                }).ToList();

                await Task.WhenAll(tasks);

                ServerItem best = servers.Where(s => s.Tag != "auto" && s.Ping < int.MaxValue).OrderBy(s => s.Ping).FirstOrDefault();
                ServerItem auto = servers.FirstOrDefault(s => s.Tag == "auto");

                if (auto != null)
                {
                    auto.PingText = best != null ? best.Ping + " мс" : "";
                    auto.PingBrush = PingBrush(best?.Ping);
                    auto.Subtitle = best != null ? "сейчас: " + best.Title : "выбирается сам";
                }
            }
            finally
            {
                PingAllButton.IsEnabled = true;
            }
        }

        private Brush PingBrush(int? ms)
        {
            if (!ms.HasValue || ms == int.MaxValue)
            {
                return (Brush)FindResource("Danger");
            }

            return (Brush)FindResource(ms < 120 ? "Accent" : ms < 250 ? "Warn" : "Danger");
        }

        // ================================================================== внешний вид состояний

        private void SetState(State next, bool animate = true)
        {
            state = next;
            int ms = animate ? 400 : 0;

            Color ring, icon;

            switch (next)
            {
                case State.Connecting:
                    ring = Color.FromRgb(0x4A, 0x3E, 0x24);
                    icon = Color.FromRgb(0xFF, 0xB3, 0x47);
                    StatusText.Text = "Подключение…";
                    SubStatus.Text = "Запускаю движок";
                    StartSpinner();
                    StopPulse();
                    Animate(CoreGlow, DropShadowEffect.OpacityProperty, 0.25, ms);
                    CoreGlow.Color = Color.FromRgb(0xFF, 0xB3, 0x47);
                    TitleDot.Fill = (Brush)FindResource("Warn");
                    break;

                case State.On:
                    ring = Color.FromRgb(0x3D, 0xDC, 0x97);
                    icon = Color.FromRgb(0x3D, 0xDC, 0x97);
                    StatusText.Text = "Подключено";
                    SubStatus.Text = SelectedName();
                    StopSpinner();
                    StartPulse();
                    CoreGlow.Color = Color.FromRgb(0x3D, 0xDC, 0x97);
                    Animate(CoreGlow, DropShadowEffect.OpacityProperty, 0.55, ms);
                    TitleDot.Fill = (Brush)FindResource("Accent");
                    break;

                default:
                    ring = Color.FromRgb(0x2A, 0x32, 0x42);
                    icon = Color.FromRgb(0x56, 0x60, 0x7A);
                    StatusText.Text = "Отключено";
                    SubStatus.Text = settings.Servers.Count > 0 ? "Нажми, чтобы подключиться" : "Добавь подписку в меню слева";
                    StopSpinner();
                    StopPulse();
                    Animate(CoreGlow, DropShadowEffect.OpacityProperty, 0, ms);
                    TitleDot.Fill = (Brush)FindResource("Faint");
                    DownText.Text = "—";
                    UpText.Text = "—";
                    PingText.Text = "—";
                    PingText.Foreground = (Brush)FindResource("Text");
                    LossText.Text = "—";
                    LossText.Foreground = (Brush)FindResource("Text");
                    TotalText.Text = "—";
                    UptimeText.Text = "—";
                    DownLine.Points = new PointCollection();
                    UpLine.Points = new PointCollection();
                    break;
            }

            RingBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ring, TimeSpan.FromMilliseconds(Math.Max(1, ms))));
            IconBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(icon, TimeSpan.FromMilliseconds(Math.Max(1, ms))));

            if (trayToggle != null)
            {
                trayToggle.Text = next == State.Off ? "Подключить" : "Отключить";
                tray.Text = next == State.On ? "VPN — подключено" : "VPN — отключено";
            }
        }

        private string SelectedName()
        {
            if (settings.SelectedServer == "auto")
            {
                return "Авто — самый быстрый сервер";
            }

            ServerProfile s = settings.Servers.FirstOrDefault(x => x.Tag == settings.SelectedServer);
            return s != null ? Subscription.CleanName(s.Name) : "";
        }

        private void StartSpinner()
        {
            Animate(Spinner, OpacityProperty, 1, 200);
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        private void StopSpinner()
        {
            Animate(Spinner, OpacityProperty, 0, 200);
        }

        private void StartPulse()
        {
            StopPulse();
            pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

            void Add(DependencyObject target, string path, double from, double to)
            {
                var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(2.2)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(a, target);
                Storyboard.SetTargetProperty(a, new PropertyPath(path));
                pulse.Children.Add(a);
            }

            Add(Halo, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", 0.95, 1.2);
            Add(Halo, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", 0.95, 1.2);
            Add(Halo, "Opacity", 0.9, 0);
            pulse.Begin(this, true);
        }

        private void StopPulse()
        {
            pulse?.Stop(this);
            pulse = null;
            Halo.Opacity = 0;
        }

        // ================================================================== кнопка питания: отклик

        private void OnPowerDown(object sender, MouseButtonEventArgs e) => AnimateScale(PowerScale, 0.95, 90);

        private void OnPowerEnter(object sender, MouseEventArgs e) => AnimateScale(PowerScale, 1.03, 160);

        private void OnPowerLeave(object sender, MouseEventArgs e) => AnimateScale(PowerScale, 1.0, 160);

        // ================================================================== серверы

        private async void OnServerClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ServerItem item)
            {
                return;
            }

            foreach (ServerItem s in servers)
            {
                s.IsSelected = s == item;
            }

            settings.SelectedServer = item.Tag;
            settings.Save();

            if (state == State.On && api != null)
            {
                bool ok = await api.SelectAsync(item.Tag);
                ShowToast(ok ? "Сервер: " + item.Title : "Не получилось переключить сервер");
                SubStatus.Text = SelectedName();
                await MeasurePingAsync();
            }
            else if (state == State.Off)
            {
                SubStatus.Text = "Выбран: " + item.Title;
            }
        }

        private async void OnPingAll(object sender, RoutedEventArgs e) => await PingAllAsync();

        private async void OnRefreshSubscription(object sender, RoutedEventArgs e) => await MaybeRefreshSubscriptionAsync(true);

        private void OnServersWheel(object sender, MouseWheelEventArgs e)
        {
            // Колесо мыши листает серверы по горизонтали.
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.6);
                e.Handled = true;
            }
        }

        // ================================================================== подписка

        private async Task MaybeRefreshSubscriptionAsync(bool force)
        {
            if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            {
                return;
            }

            bool due = settings.Servers.Count == 0 || DateTime.Now - settings.LastSubscriptionUpdate > TimeSpan.FromHours(settings.SubscriptionUpdateHours);

            if (!force && !due)
            {
                return;
            }

            RefreshButton.IsEnabled = false;
            RefreshButton.Content = "Обновляю…";

            try
            {
                SubscriptionResult result = await Subscription.LoadAsync(settings.SubscriptionUrl);

                if (result.Servers.Count == 0)
                {
                    ShowToast("В подписке не нашлось серверов" + (result.Skipped > 0 ? " (непонятных ссылок: " + result.Skipped + ")" : ""));
                    return;
                }

                // Сохраняем выбор сервера по имени — теги могут поменяться.
                string selectedName = settings.Servers.FirstOrDefault(s => s.Tag == settings.SelectedServer)?.Name;
                settings.Servers = result.Servers;
                settings.SelectedServer = result.Servers.FirstOrDefault(s => s.Name == selectedName)?.Tag ?? "auto";
                settings.SubscriptionTitle = result.Title;
                settings.TrafficUsed = result.Used;
                settings.TrafficTotal = result.Total;
                settings.ExpireUnix = result.Expire;
                settings.LastSubscriptionUpdate = DateTime.Now;
                settings.Save();

                RebuildServers();
                UpdateSubscriptionCard();
                Log.Write("Подписка обновлена: серверов " + result.Servers.Count + (result.Skipped > 0 ? ", пропущено " + result.Skipped : ""));
                ShowToast("Подписка обновлена: серверов " + result.Servers.Count);

                if (state == State.On)
                {
                    await ApplyChangesAsync();
                }
                else if (state == State.Off)
                {
                    SetState(State.Off, animate: false);
                    _ = PingAllAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Write("Подписка: " + ex.Message);
                ShowToast("Не удалось загрузить подписку: " + ex.Message);
            }
            finally
            {
                RefreshButton.IsEnabled = true;
                RefreshButton.Content = "Обновить подписку";
            }
        }

        private async void OnSubSave(object sender, RoutedEventArgs e)
        {
            settings.SubscriptionUrl = SubUrl.Text.Trim();
            settings.Save();
            UpdateSubscriptionCard();
            await MaybeRefreshSubscriptionAsync(true);
        }

        private void OnUpdateInterval(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }

            settings.SubscriptionUpdateHours = Upd6.IsChecked == true ? 6 : Upd24.IsChecked == true ? 24 : 12;
            settings.Save();
        }

        // ================================================================== туннели

        private void OnDefaultRoute(object sender, RoutedEventArgs e)
        {
            settings.DefaultViaVpn = DefaultVpn.IsChecked == true;
            UpdateDefaultHint();
            ScheduleApply();
        }

        private void UpdateDefaultHint()
        {
            DefaultHint.Text = settings.DefaultViaVpn
                ? "Всё идёт через VPN, кроме того, что ниже помечено «напрямую»."
                : "Всё идёт напрямую — через VPN только то, что ниже помечено «через VPN».";
        }

        private void OnTunnelOption(object sender, RoutedEventArgs e)
        {
            settings.RussiaDirect = RussiaCheck.IsChecked == true;
            settings.BlockAds = AdsCheck.IsChecked == true;
            ScheduleApply();
        }

        private void OnRuleToggle(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RuleItem item)
            {
                item.ViaVpn = !item.ViaVpn;
                ScheduleApply();
            }
        }

        private void OnRuleDelete(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RuleItem item)
            {
                apps.Remove(item);
                sites.Remove(item);
                UpdateRuleEmpties();
                ScheduleApply();
            }
        }

        private void UpdateRuleEmpties()
        {
            AppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SitesEmpty.Visibility = sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddRule(RuleKind kind, string value, string path, bool viaVpn)
        {
            value = kind == RuleKind.Site ? ConfigBuilder.NormalizeDomain(value) : value.Trim();

            if (value.Length == 0)
            {
                return;
            }

            ObservableCollection<RuleItem> list = kind == RuleKind.App ? apps : sites;
            RuleItem existing = list.FirstOrDefault(r => r.Rule.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ViaVpn = viaVpn;
            }
            else
            {
                list.Add(new RuleItem(new RouteRule { Kind = kind, Value = value, Path = path ?? "", ViaVpn = viaVpn }));
            }

            UpdateRuleEmpties();
            ScheduleApply();
        }

        private void OnSiteAddVpn(object sender, RoutedEventArgs e) => AddSite(true);

        private void OnSiteAddDirect(object sender, RoutedEventArgs e) => AddSite(false);

        private void OnSiteKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddSite(true);
            }
        }

        private void AddSite(bool viaVpn)
        {
            foreach (string part in SiteInput.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddRule(RuleKind.Site, part, null, viaVpn);
            }

            SiteInput.Text = "";
        }

        // --- выбор программы ---

        private sealed class ProcessCandidate
        {
            public string Name { get; set; }
            public string Hint { get; set; }
            public string Path { get; set; }
        }

        private async void OnAddAppOpen(object sender, RoutedEventArgs e)
        {
            AppPicker.Visibility = Visibility.Visible;
            Fade(AppPicker, 1, 200, from: 0);
            AppSearch.Text = "";
            candidates = await Task.Run(ListProcesses);
            FilterCandidates();
            AppSearch.Focus();
        }

        private void OnAddAppClose(object sender, RoutedEventArgs e) => AppPicker.Visibility = Visibility.Collapsed;

        private static List<ProcessCandidate> ListProcesses()
        {
            var result = new Dictionary<string, ProcessCandidate>(StringComparer.OrdinalIgnoreCase);

            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string path = "";

                    try
                    {
                        path = p.MainModule?.FileName ?? "";
                    }
                    catch
                    {
                        // системные процессы не отдают путь
                    }

                    if (path.Length == 0 || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string exe = System.IO.Path.GetFileName(path);

                    if (result.ContainsKey(exe))
                    {
                        if (!string.IsNullOrEmpty(p.MainWindowTitle))
                        {
                            result[exe].Hint = p.MainWindowTitle;
                        }

                        continue;
                    }

                    result[exe] = new ProcessCandidate
                    {
                        Name = exe,
                        Hint = string.IsNullOrEmpty(p.MainWindowTitle) ? path : p.MainWindowTitle,
                        Path = path
                    };
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }

            return result.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void OnAppSearch(object sender, TextChangedEventArgs e) => FilterCandidates();

        private void FilterCandidates()
        {
            string q = AppSearch.Text.Trim();
            AppCandidates.ItemsSource = candidates
                .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (c.Hint ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private void OnAppPick(object sender, MouseButtonEventArgs e) => AddPickedApp(true);

        private void OnAppAddVpn(object sender, RoutedEventArgs e) => AddPickedApp(true);

        private void OnAppAddDirect(object sender, RoutedEventArgs e) => AddPickedApp(false);

        private void AddPickedApp(bool viaVpn)
        {
            if (AppCandidates.SelectedItem is ProcessCandidate c)
            {
                AddRule(RuleKind.App, c.Name, c.Path, viaVpn);
                ShowToast(c.Name + (viaVpn ? " → через VPN" : " → напрямую"));
            }
            else
            {
                ShowToast("Выбери программу в списке");
            }
        }

        private void OnAppBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выбери программу" };

            if (dialog.ShowDialog(this) == true)
            {
                AddRule(RuleKind.App, System.IO.Path.GetFileName(dialog.FileName), dialog.FileName, true);
            }
        }

        // ================================================================== настройки

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            bool autostart = AutostartCheck.IsChecked == true;

            if (autostart != settings.Autostart)
            {
                Autostart.Set(autostart);
            }

            settings.Autostart = autostart;
            settings.StartMinimized = MinimizedCheck.IsChecked == true;
            settings.AutoConnect = AutoConnectCheck.IsChecked == true;
            settings.Save();
        }

        private void OnOpenData(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(Paths.Data);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Paths.Data + "\"") { UseShellExecute = true });
        }

        private void OnLogCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(Log.All());
                ShowToast("Журнал скопирован");
            }
            catch
            {
                ShowToast("Не удалось скопировать");
            }
        }

        // ================================================================== боковое меню

        private void OnMenuOpen(object sender, RoutedEventArgs e)
        {
            Drawer.Visibility = Visibility.Visible;
            Backdrop.Visibility = Visibility.Visible;
            Slide(-440, 0);
            Fade(Backdrop, 1, 250);
        }

        private void OnMenuClose(object sender, RoutedEventArgs e) => CloseMenu();

        private void OnBackdropClick(object sender, MouseButtonEventArgs e) => CloseMenu();

        private void CloseMenu()
        {
            Slide(0, -440, () =>
            {
                Drawer.Visibility = Visibility.Collapsed;
                Backdrop.Visibility = Visibility.Collapsed;
            });
            Fade(Backdrop, 0, 220);
        }

        private void Slide(double from, double to, Action done = null)
        {
            var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = to > from ? EasingMode.EaseOut : EasingMode.EaseIn }
            };

            if (done != null)
            {
                a.Completed += (_, _) => done();
            }

            DrawerShift.BeginAnimation(TranslateTransform.XProperty, a);
        }

        private void OnNav(object sender, RoutedEventArgs e)
        {
            if (PageTunnels == null)
            {
                return;
            }

            FrameworkElement show = NavSub.IsChecked == true ? PageSub
                : NavSettings.IsChecked == true ? PageSettings
                : NavLog.IsChecked == true ? PageLog
                : PageTunnels;

            foreach (FrameworkElement page in new FrameworkElement[] { PageTunnels, PageSub, PageSettings, PageLog })
            {
                page.Visibility = page == show ? Visibility.Visible : Visibility.Collapsed;
            }

            Fade(show, 1, 220, from: 0);

            if (show == PageLog)
            {
                LogBox.Text = Log.All() + Environment.NewLine;
                LogBox.ScrollToEnd();
            }
        }

        // ================================================================== окно и трей

        private void OnTitleDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e) => HideToTray();

        private void HideToTray()
        {
            Hide();

            if (!trayHintShown)
            {
                trayHintShown = true;
                tray.ShowBalloonTip(3000, "VPN работает в трее", "Двойной клик по значку — открыть окно. Выход — через меню значка.", System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void CreateTray()
        {
            tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "VPN — отключено",
                Visible = true,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
            trayToggle = new System.Windows.Forms.ToolStripMenuItem("Подключить", null, (_, _) => Dispatcher.Invoke(async () =>
            {
                if (state == State.Off)
                {
                    await ConnectAsync();
                }
                else
                {
                    await DisconnectAsync();
                }
            }));
            menu.Items.Add(trayToggle);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(async () => await ExitAsync()));
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        }

        private async Task ExitAsync()
        {
            reallyExit = true;
            await DisconnectAsync();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!reallyExit)
            {
                // Alt+F4 тоже прячет в трей.
                e.Cancel = true;
                HideToTray();
            }

            base.OnClosing(e);
        }

        // ================================================================== мелкие анимации

        private void ShowToast(string text)
        {
            ToastText.Text = text;
            Fade(Toast, 1, 200);
            toastTimer.Stop();
            toastTimer.Start();
        }

        private static void Fade(UIElement element, double to, int ms, double? from = null)
        {
            var a = from.HasValue
                ? new DoubleAnimation(from.Value, to, TimeSpan.FromMilliseconds(ms))
                : new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            element.BeginAnimation(OpacityProperty, a);
        }

        private static void Animate(Animatable target, DependencyProperty property, double to, int ms)
        {
            target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Max(1, ms))));
        }

        private static void Animate(UIElement target, DependencyProperty property, double to, int ms)
        {
            target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Max(1, ms))));
        }

        private static void AnimateScale(ScaleTransform scale, double to, int ms)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }

        private static void AnimateWidth(FrameworkElement element, double to)
        {
            element.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private static string FirstLine(string text) => (text ?? "").Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
    }

    // ====================================================================== модели для списков

    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    public sealed class ServerItem : Notify
    {
        private string pingText = "";
        private Brush pingBrush = Brushes.Gray;
        private bool isSelected;
        private string subtitle = "";

        public string Tag { get; set; }
        public string Code { get; set; }
        public string Title { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public int Ping { get; set; } = int.MaxValue;

        public string Subtitle { get => subtitle; set => Set(ref subtitle, value); }
        public string PingText { get => pingText; set => Set(ref pingText, value); }
        public Brush PingBrush { get => pingBrush; set => Set(ref pingBrush, value); }
        public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    }

    public sealed class RuleItem : Notify
    {
        public RouteRule Rule { get; }

        public RuleItem(RouteRule rule)
        {
            Rule = rule;
        }

        public string Title => Rule.Value;

        public string Subtitle => Rule.Kind == RuleKind.App ? Rule.Path ?? "" : "";

        public bool ViaVpn
        {
            get => Rule.ViaVpn;
            set
            {
                bool v = Rule.ViaVpn;
                Set(ref v, value);
                Rule.ViaVpn = v;
            }
        }
    }
}
