using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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

        /// <summary>Готовые наборы сайтов, которые обычно нужны через VPN.</summary>
        private static readonly (string Name, string[] Domains)[] PresetSites =
        {
            ("YouTube", new[] { "youtube.com", "googlevideo.com", "ytimg.com", "youtu.be", "ggpht.com" }),
            ("Discord", new[] { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media" }),
            ("Instagram", new[] { "instagram.com", "cdninstagram.com", "fbcdn.net" }),
            ("Facebook", new[] { "facebook.com", "fbcdn.net", "messenger.com" }),
            ("X / Twitter", new[] { "x.com", "twitter.com", "twimg.com", "t.co" }),
            ("ChatGPT", new[] { "chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com" }),
            ("Telegram", new[] { "telegram.org", "t.me", "telegram.me" }),
            ("Spotify", new[] { "spotify.com", "scdn.co", "spotifycdn.com" })
        };

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
        private bool started;
        private bool reallyExit;
        private bool trayHintShown;
        private bool selectingServer;
        private System.Windows.Forms.NotifyIcon tray;
        private System.Windows.Forms.ToolStripMenuItem trayToggle;
        private List<Candidate> candidates = new List<Candidate>();

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

            runner.Crashed += tail => Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (state != State.Off)
                {
                    await DisconnectAsync();
                    ShowToast("VPN отключился: " + LastLine(tail));
                }
            }));

            // Журнал обновляет экран только когда он открыт — иначе поток интерфейса не трогаем.
            Log.Added += line =>
            {
                if (logVisible)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        LogBox.AppendText(line + Environment.NewLine);
                        LogBox.ScrollToEnd();
                    }), DispatcherPriority.Background);
                }
            };

            SourceInitialized += (_, _) => ApplyWindowLook();
            StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

            BuildPresets();
            LoadIntoUi();
            CreateTray();
            SetState(State.Off, animate: false);
            loading = false;

            Loaded += async (_, _) => await OnStartedAsync();

            if (startMinimized)
            {
                Dispatcher.BeginInvoke(new Action(async () => await OnStartedAsync()), DispatcherPriority.ApplicationIdle);
            }

            subTimer.Start();
        }

        private volatile bool logVisible;

        private async Task OnStartedAsync()
        {
            if (started)
            {
                return;
            }

            started = true;
            await MaybeRefreshSubscriptionAsync(false);

            if (settings.AutoConnect && settings.Servers.Count > 0)
            {
                await ConnectAsync();
            }
            else
            {
                _ = PingAllAsync();
            }
        }

        // ================================================================== внешний вид окна

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Тёмная рамка и скруглённые углы Windows 11 (на Windows 10 просто тёмная рамка).</summary>
        private void ApplyWindowLook()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                int border = 0x00383023; // цвет рамки (BGR) — под цвет обводок
                DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
            }
            catch
            {
                // старая Windows — не страшно
            }
        }

        // ================================================================== загрузка в интерфейс

        private void LoadIntoUi()
        {
            ModeAll.IsChecked = settings.DefaultViaVpn;
            ModeSelected.IsChecked = !settings.DefaultViaVpn;
            RussiaCheck.IsChecked = settings.RussiaDirect;
            AdsCheck.IsChecked = settings.BlockAds;

            foreach (RouteRule rule in settings.Rules)
            {
                (rule.Kind == RuleKind.App ? apps : sites).Add(new RuleItem(rule));
            }

            UpdateRuleEmpties();
            LoadRuleIconsAsync();

            SubUrl.Text = settings.SubscriptionUrl;
            Upd6.IsChecked = settings.SubscriptionUpdateHours == 6;
            Upd12.IsChecked = settings.SubscriptionUpdateHours == 12;
            Upd24.IsChecked = settings.SubscriptionUpdateHours == 24;

            AutostartCheck.IsChecked = settings.Autostart;
            MinimizedCheck.IsChecked = settings.StartMinimized;
            AutoConnectCheck.IsChecked = settings.AutoConnect;

            string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
            AboutText.Text = "VPN " + version + " — свой клиент на движке sing-box.\n" +
                             "Работает через виртуальный сетевой адаптер, поэтому просит права администратора.\n" +
                             "Закрытие окна прячет программу в трей; выход — через меню значка в трее.";

            RebuildServers();
            UpdateSubscriptionCard();
        }

        private async void LoadRuleIconsAsync()
        {
            foreach (RuleItem item in apps.ToList())
            {
                string path = item.Rule.Path;
                item.Icon = await Task.Run(() => IconFor(path));
            }
        }

        private void RebuildServers()
        {
            selectingServer = true;
            string current = settings.SelectedServer;
            servers.Clear();
            servers.Add(new ServerItem
            {
                Tag = "auto",
                Code = "AUTO",
                Title = "Самый быстрый",
                Subtitle = "выбирается автоматически",
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
                    Subtitle = s.Protocol + " · " + s.Host,
                    Host = s.Host,
                    Port = s.Port,
                    IsSelected = s.Tag == current
                });
            }

            ServersList.SelectedItem = servers.FirstOrDefault(s => s.IsSelected);
            selectingServer = false;
            ServerCount.Text = settings.Servers.Count > 0 ? settings.Servers.Count + " шт." : "добавь подписку";
            FilterServers();
            UpdateCurrentServerCard();
        }

        private void UpdateCurrentServerCard()
        {
            ServerItem selected = servers.FirstOrDefault(s => s.IsSelected) ?? servers.FirstOrDefault();

            if (selected == null)
            {
                return;
            }

            CurrentCode.Text = selected.Code;
            CurrentServer.Text = selected.Tag == "auto" && !string.IsNullOrEmpty(autoChoice) ? "Авто → " + autoChoice : selected.Title;
            CurrentPing.Text = selected.PingText == "…" ? "" : selected.PingText;
            CurrentPing.Foreground = selected.PingBrush;
        }

        private string autoChoice = "";

        private void UpdateSubscriptionCard()
        {
            SubTitleText.Text = string.IsNullOrWhiteSpace(settings.SubscriptionTitle) ? "Подписка" : settings.SubscriptionTitle;

            if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            {
                SubTrafficText.Text = "Ссылка не добавлена";
                SubExpireText.Text = "";
                SubBar.Width = 0;
                SubInfo.Text = "";
                return;
            }

            double barWidth = 600;

            if (settings.TrafficTotal > 0)
            {
                SubTrafficText.Text = "Использовано " + Format.Size(settings.TrafficUsed) + " из " + Format.Size(settings.TrafficTotal);
                AnimateWidth(SubBar, barWidth * Math.Clamp((double)settings.TrafficUsed / settings.TrafficTotal, 0, 1));
            }
            else
            {
                SubTrafficText.Text = "Использовано " + Format.Size(settings.TrafficUsed) + " · трафик без лимита";
                SubBar.Width = 0;
            }

            if (settings.ExpireUnix > 0)
            {
                DateTime expire = DateTimeOffset.FromUnixTimeSeconds(settings.ExpireUnix).LocalDateTime;
                int days = (int)Math.Ceiling((expire - DateTime.Now).TotalDays);
                SubExpireText.Text = days > 0 ? "до " + expire.ToString("dd.MM.yyyy") + " (" + days + " дн.)" : "срок истёк";
            }
            else
            {
                SubExpireText.Text = "";
            }

            SubInfo.Text = "Серверов: " + settings.Servers.Count +
                           (settings.LastSubscriptionUpdate > DateTime.MinValue ? "   ·   обновлено " + settings.LastSubscriptionUpdate.ToString("dd.MM HH:mm") : "");
        }

        // ================================================================== подключение

        private async void OnPowerClick(object sender, MouseButtonEventArgs e)
        {
            // Короткий «нажим» кнопки.
            var press = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
            PowerScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
            PowerScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);

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
                    SubStatus.Text = Shorten(error, 260);
                    return;
                }

                StartSession();
                connectedAt = DateTime.Now;
                pingHistory.Clear();
                tickCount = 0;
                SetState(State.On);
                tick.Start();
                _ = MeasurePingAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Подключение: " + ex);
                SetState(State.Off);
                SubStatus.Text = Shorten(ex.Message, 260);
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>Связь с движком и поток скорости.</summary>
        private void StartSession()
        {
            sessionCts?.Cancel();
            api?.Dispose();
            api = new CoreApi(runner.ApiSecret);
            sessionCts = new CancellationTokenSource();
            downHistory.Clear();
            upHistory.Clear();
            CoreApi current = api;
            CancellationToken token = sessionCts.Token;
            _ = Task.Run(() => current.StreamTrafficAsync(
                (up, down) => Dispatcher.BeginInvoke(new Action(() => OnTraffic(up, down)), DispatcherPriority.Background), token));
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
                autoChoice = "";
                SetState(State.Off);
                UpdateCurrentServerCard();
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
                    SubStatus.Text = Shorten(error, 260);
                    return;
                }

                StartSession();
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

            while (q.Count > 30)
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
                double x = line.Width * (i + 30 - count) / 29.0;
                double y = line.Height - 1 - (line.Height - 2) * v / max;
                points.Add(new Point(x, y));
                i++;
            }

            points.Freeze();
            line.Points = points;
        }

        private async void OnTick(object sender, EventArgs e)
        {
            if (state != State.On || api == null)
            {
                return;
            }

            tickCount++;
            UptimeRun.Text = Format.Duration(DateTime.Now - connectedAt);

            if (tickCount % 3 == 0)
            {
                var (up, down) = await api.TotalsAsync();

                if (state == State.On)
                {
                    TotalRun.Text = " · " + Format.Size(up + down);
                }
            }

            if (tickCount % 5 == 0)
            {
                await MeasurePingAsync();
            }
        }

        private async Task MeasurePingAsync()
        {
            CoreApi current = api;

            if (current == null)
            {
                return;
            }

            int? delay = await current.DelayAsync("proxy");
            string now = await current.CurrentAsync();

            if (state != State.On || current != api)
            {
                return;
            }

            pingHistory.Enqueue(delay.HasValue);

            while (pingHistory.Count > 20)
            {
                pingHistory.Dequeue();
            }

            PingRun.Text = delay.HasValue ? delay + " мс" : "нет ответа";
            PingRun.Foreground = PingBrush(delay);
            int lost = pingHistory.Count(ok => !ok);
            LossRun.Text = " · " + (lost * 100 / pingHistory.Count) + "%";
            SideDetail.Text = (delay.HasValue ? delay + " мс" : "нет ответа") + " · " + Format.Duration(DateTime.Now - connectedAt);

            ServerProfile server = settings.Servers.FirstOrDefault(s => s.Tag == now);
            autoChoice = server != null && settings.SelectedServer == "auto" ? Subscription.CleanName(server.Name) : "";
            UpdateCurrentServerCard();

            if (delay.HasValue)
            {
                CurrentPing.Text = delay + " мс";
                CurrentPing.Foreground = PingBrush(delay);
            }
        }

        private async Task PingAllAsync()
        {
            if (servers.Count <= 1)
            {
                return;
            }

            PingAllButton.IsEnabled = false;

            try
            {
                CoreApi current = state == State.On ? api : null;
                Brush faint = (Brush)FindResource("Faint");

                foreach (ServerItem item in servers.Where(s => s.Tag != "auto"))
                {
                    item.PingText = "…";
                    item.PingBrush = faint;
                }

                // Не больше 8 проверок одновременно.
                using var gate = new SemaphoreSlim(8);
                var tasks = servers.Where(s => s.Tag != "auto").ToList().Select(async item =>
                {
                    await gate.WaitAsync();

                    try
                    {
                        int? ms = current != null ? await current.DelayAsync(item.Tag) : await CoreApi.TcpPingAsync(item.Host, item.Port);
                        item.Ping = ms ?? int.MaxValue;
                        item.PingText = ms.HasValue ? ms + " мс" : "нет";
                        item.PingBrush = PingBrush(ms);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                ServerItem best = servers.Where(s => s.Tag != "auto" && s.Ping < int.MaxValue).OrderBy(s => s.Ping).FirstOrDefault();
                ServerItem auto = servers.FirstOrDefault(s => s.Tag == "auto");

                if (auto != null)
                {
                    auto.PingText = best != null ? best.Ping + " мс" : "";
                    auto.PingBrush = PingBrush(best?.Ping);
                    auto.Subtitle = best != null ? "сейчас лучший: " + best.Title : "выбирается автоматически";
                }

                UpdateCurrentServerCard();
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

        // ================================================================== состояния кнопки

        private void SetState(State next, bool animate = true)
        {
            state = next;
            int ms = animate ? 350 : 1;
            Color ring;
            Color icon;
            double glow;

            switch (next)
            {
                case State.Connecting:
                    ring = Color.FromRgb(0x4A, 0x3E, 0x24);
                    icon = Color.FromRgb(0xFF, 0xB3, 0x47);
                    glow = 0;
                    StatusText.Text = "Подключение…";
                    SubStatus.Text = "Запускаю туннель";
                    SideStatus.Text = "Подключение…";
                    SideDot.Fill = (Brush)FindResource("Warn");
                    SideDetail.Text = "";
                    StartSpinner();
                    break;

                case State.On:
                    ring = Color.FromRgb(0x3D, 0xDC, 0x97);
                    icon = Color.FromRgb(0x3D, 0xDC, 0x97);
                    glow = 1;
                    StatusText.Text = "Подключено";
                    SubStatus.Text = "Трафик защищён · правила туннелей работают";
                    SideStatus.Text = "Подключено";
                    SideDot.Fill = (Brush)FindResource("Accent");
                    StopSpinner();
                    break;

                default:
                    ring = Color.FromRgb(0x2A, 0x32, 0x42);
                    icon = Color.FromRgb(0x56, 0x60, 0x7A);
                    glow = 0;
                    StatusText.Text = "Отключено";
                    SubStatus.Text = settings.Servers.Count > 0 ? "Нажми на кнопку, чтобы подключиться" : "Сначала добавь подписку — раздел «Подписка» слева";
                    SideStatus.Text = "Отключено";
                    SideDot.Fill = (Brush)FindResource("Faint");
                    SideDetail.Text = "";
                    StopSpinner();
                    DownText.Text = "—";
                    UpText.Text = "—";
                    PingRun.Text = "—";
                    PingRun.Foreground = (Brush)FindResource("Text");
                    LossRun.Text = "";
                    UptimeRun.Text = "—";
                    TotalRun.Text = "";
                    DownLine.Points = new PointCollection();
                    UpLine.Points = new PointCollection();
                    break;
            }

            RingBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ring, TimeSpan.FromMilliseconds(ms)));
            IconBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(icon, TimeSpan.FromMilliseconds(ms)));
            Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, TimeSpan.FromMilliseconds(ms * 2)));

            if (trayToggle != null)
            {
                trayToggle.Text = next == State.Off ? "Подключить" : "Отключить";
                tray.Text = next == State.On ? "VPN — подключено" : "VPN — отключено";
            }
        }

        private void StartSpinner()
        {
            Spinner.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopSpinner()
        {
            // Важно: полностью останавливаем вращение, а не просто прячем его.
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            Spinner.BeginAnimation(OpacityProperty, null);
            Spinner.Opacity = 0;
        }

        // ================================================================== навигация

        private void OnNav(object sender, RoutedEventArgs e)
        {
            if (PageHome == null)
            {
                return;
            }

            FrameworkElement show = NavServers.IsChecked == true ? PageServers
                : NavTunnels.IsChecked == true ? PageTunnels
                : NavSub.IsChecked == true ? PageSub
                : NavSettings.IsChecked == true ? PageSettings
                : PageHome;

            foreach (FrameworkElement page in new FrameworkElement[] { PageHome, PageServers, PageTunnels, PageSub, PageSettings })
            {
                page.Visibility = page == show ? Visibility.Visible : Visibility.Collapsed;
            }

            Fade(show, 1, 160, from: 0);
        }

        private void OnOpenServers(object sender, RoutedEventArgs e) => NavServers.IsChecked = true;

        // ================================================================== серверы

        private async void OnServerSelected(object sender, SelectionChangedEventArgs e)
        {
            if (selectingServer || ServersList.SelectedItem is not ServerItem item || item.IsSelected)
            {
                return;
            }

            foreach (ServerItem s in servers)
            {
                s.IsSelected = s == item;
            }

            settings.SelectedServer = item.Tag;
            settings.Save();
            autoChoice = "";
            UpdateCurrentServerCard();

            if (state == State.On && api != null)
            {
                bool ok = await api.SelectAsync(item.Tag);
                ShowToast(ok ? "Сервер: " + item.Title : "Не получилось переключить сервер");
                await MeasurePingAsync();
            }
            else
            {
                ShowToast("Выбран: " + item.Title);
            }
        }

        private void OnServerSearch(object sender, TextChangedEventArgs e) => FilterServers();

        private void FilterServers()
        {
            string q = ServerSearch.Text.Trim();
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(servers);
            view.Filter = q.Length == 0
                ? null
                : o => o is ServerItem s && (s.Tag == "auto" || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Code.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        private async void OnPingAll(object sender, RoutedEventArgs e) => await PingAllAsync();

        private async void OnRefreshSubscription(object sender, RoutedEventArgs e) => await MaybeRefreshSubscriptionAsync(true);

        // ================================================================== подписка

        private async Task MaybeRefreshSubscriptionAsync(bool force)
        {
            if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            {
                if (force)
                {
                    ShowToast("Сначала вставь ссылку в разделе «Подписка»");
                }

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
                SubscriptionResult result = await Task.Run(() => Subscription.LoadAsync(settings.SubscriptionUrl));

                if (result.Servers.Count == 0)
                {
                    ShowToast("В подписке не нашлось серверов" + (result.Skipped > 0 ? " (непонятных ссылок: " + result.Skipped + ")" : ""));
                    return;
                }

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

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }

            settings.DefaultViaVpn = ModeAll.IsChecked == true;
            ScheduleApply();
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

        /// <summary>Новое правило по умолчанию делает «обратное» режиму: в «Всё через VPN» — напрямую, и наоборот.</summary>
        private bool DefaultForNewRule => !settings.DefaultViaVpn;

        private RuleItem AddRule(RuleKind kind, string value, string path, bool viaVpn)
        {
            value = kind == RuleKind.Site ? ConfigBuilder.NormalizeDomain(value) : value.Trim();

            if (value.Length == 0)
            {
                return null;
            }

            ObservableCollection<RuleItem> list = kind == RuleKind.App ? apps : sites;
            RuleItem existing = list.FirstOrDefault(r => r.Rule.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.ViaVpn = viaVpn;
                return existing;
            }

            var item = new RuleItem(new RouteRule { Kind = kind, Value = value, Path = path ?? "", ViaVpn = viaVpn });
            list.Insert(0, item);
            UpdateRuleEmpties();
            return item;
        }

        private void OnSiteAdd(object sender, RoutedEventArgs e) => AddSitesFromInput();

        private void OnSiteKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddSitesFromInput();
            }
        }

        private void AddSitesFromInput()
        {
            int added = 0;

            foreach (string part in SiteInput.Text.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (AddRule(RuleKind.Site, part, null, DefaultForNewRule) != null)
                {
                    added++;
                }
            }

            if (added > 0)
            {
                SiteInput.Text = "";
                ScheduleApply();
            }
        }

        private void BuildPresets()
        {
            foreach (var preset in PresetSites)
            {
                var button = new Button { Content = "+ " + preset.Name, Style = (Style)FindResource("ChipButton"), Tag = preset.Domains };
                button.Click += (s, _) =>
                {
                    foreach (string domain in (string[])((Button)s).Tag)
                    {
                        AddRule(RuleKind.Site, domain, null, true);
                    }

                    ScheduleApply();
                    ShowToast(preset.Name + " → через VPN");
                };
                Presets.Children.Add(button);
            }
        }

        // --- окно выбора программ ---

        private async void OnPickerOpen(object sender, RoutedEventArgs e)
        {
            PickerOverlay.Visibility = Visibility.Visible;
            Fade(PickerOverlay, 1, 150, from: 0);
            PickerSearch.Text = "";
            PickerHint.Text = "Загружаю список…";
            PickerList.ItemsSource = null;
            candidates = await Task.Run(ListProcesses);
            PickerHint.Text = DefaultForNewRule ? "будут идти через VPN" : "будут идти напрямую";
            FilterCandidates();
            PickerSearch.Focus();
        }

        private void OnPickerClose(object sender, RoutedEventArgs e) => PickerOverlay.Visibility = Visibility.Collapsed;

        private void OnPickerSearch(object sender, TextChangedEventArgs e) => FilterCandidates();

        private void FilterCandidates()
        {
            string q = PickerSearch.Text.Trim();
            PickerList.ItemsSource = candidates
                .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (c.Hint ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>Клик по строке отмечает/снимает галочку.</summary>
        private void OnPickerRowClick(object sender, SelectionChangedEventArgs e)
        {
            if (PickerList.SelectedItem is Candidate c)
            {
                c.Checked = !c.Checked;
                PickerList.SelectedItem = null;
            }
        }

        private void OnPickerAdd(object sender, RoutedEventArgs e)
        {
            List<Candidate> chosen = candidates.Where(c => c.Checked).ToList();

            if (chosen.Count == 0)
            {
                ShowToast("Отметь программы галочками");
                return;
            }

            foreach (Candidate c in chosen)
            {
                RuleItem item = AddRule(RuleKind.App, c.Name, c.Path, DefaultForNewRule);

                if (item != null)
                {
                    item.Icon = c.Icon;
                }
            }

            PickerOverlay.Visibility = Visibility.Collapsed;
            ScheduleApply();
            ShowToast("Добавлено программ: " + chosen.Count);
        }

        private void OnPickerBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выбери программу" };

            if (dialog.ShowDialog(this) == true)
            {
                RuleItem item = AddRule(RuleKind.App, System.IO.Path.GetFileName(dialog.FileName), dialog.FileName, DefaultForNewRule);

                if (item != null)
                {
                    item.Icon = IconFor(dialog.FileName);
                }

                PickerOverlay.Visibility = Visibility.Collapsed;
                ScheduleApply();
            }
        }

        private static List<Candidate> ListProcesses()
        {
            var result = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string path;

                    try
                    {
                        path = p.MainModule?.FileName ?? "";
                    }
                    catch
                    {
                        continue;
                    }

                    if (path.Length == 0 || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string exe = System.IO.Path.GetFileName(path);
                    string title = "";

                    try
                    {
                        title = p.MainWindowTitle;
                    }
                    catch
                    {
                    }

                    if (result.TryGetValue(exe, out Candidate existing))
                    {
                        if (!string.IsNullOrEmpty(title))
                        {
                            existing.Hint = title;
                            existing.HasWindow = true;
                        }

                        continue;
                    }

                    result[exe] = new Candidate
                    {
                        Name = exe,
                        Hint = string.IsNullOrEmpty(title) ? path : title,
                        Path = path,
                        HasWindow = !string.IsNullOrEmpty(title),
                        Icon = IconFor(path)
                    };
                }
                finally
                {
                    p.Dispose();
                }
            }

            // Программы с окнами — наверху: их обычно и ищут.
            return result.Values.OrderByDescending(c => c.HasWindow).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Значок exe. Возвращает замороженную картинку — её можно создавать в фоновом потоке.</summary>
        private static ImageSource IconFor(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            lock (IconCache)
            {
                if (IconCache.TryGetValue(path, out ImageSource cached))
                {
                    return cached;
                }
            }

            try
            {
                using System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(path);

                if (icon == null)
                {
                    return null;
                }

                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();

                lock (IconCache)
                {
                    IconCache[path] = source;
                }

                return source;
            }
            catch
            {
                return null;
            }
        }

        // ================================================================== настройки и журнал

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            bool autostart = AutostartCheck.IsChecked == true;

            if (autostart != settings.Autostart)
            {
                Task.Run(() => Autostart.Set(autostart));
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

        private void OnLogOpen(object sender, RoutedEventArgs e)
        {
            LogBox.Text = Log.All() + Environment.NewLine;
            LogOverlay.Visibility = Visibility.Visible;
            Fade(LogOverlay, 1, 150, from: 0);
            LogBox.ScrollToEnd();
            logVisible = true;
        }

        private void OnLogClose(object sender, RoutedEventArgs e)
        {
            logVisible = false;
            LogOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnLogCopy(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(Log.All());
                ShowToast("Журнал скопирован — вставь его в чат");
            }
            catch
            {
                ShowToast("Не удалось скопировать");
            }
        }

        // ================================================================== окно и трей

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximize(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnClose(object sender, RoutedEventArgs e) => HideToTray();

        private void HideToTray()
        {
            Hide();

            if (!trayHintShown)
            {
                trayHintShown = true;
                tray.ShowBalloonTip(3000, "VPN работает в трее", "Клик по значку — открыть окно. Выход — правой кнопкой по значку.", System.Windows.Forms.ToolTipIcon.Info);
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
            tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "VPN — отключено",
                Visible = true,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
            trayToggle = new System.Windows.Forms.ToolStripMenuItem("Подключить", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (state == State.Off)
                {
                    await ConnectAsync();
                }
                else
                {
                    await DisconnectAsync();
                }
            })));
            menu.Items.Add(trayToggle);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(new Action(async () => await ExitAsync())));
            tray.ContextMenuStrip = menu;

            // Один клик левой кнопкой — открыть окно.
            tray.MouseClick += (_, args) =>
            {
                if (args.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    Dispatcher.BeginInvoke(new Action(ShowFromTray));
                }
            };
        }

        private async Task ExitAsync()
        {
            reallyExit = true;
            tick.Stop();

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
                // Alt+F4 и крестик прячут в трей.
                e.Cancel = true;
                HideToTray();
            }

            base.OnClosing(e);
        }

        // ================================================================== мелочи

        private void ShowToast(string text)
        {
            ToastText.Text = text;
            Fade(Toast, 1, 180);
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

        private static void AnimateWidth(FrameworkElement element, double to)
        {
            element.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private static string Shorten(string text, int max) => text.Length > max ? text.Substring(0, max) + "…" : text;

        private static string LastLine(string text) => (text ?? "").Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
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
                Raise(name);
            }
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                Set(ref isSelected, value);
                Raise(nameof(CheckVisibility));
            }
        }

        public Visibility CheckVisibility => isSelected ? Visibility.Visible : Visibility.Hidden;
    }

    public sealed class RuleItem : Notify
    {
        private ImageSource icon;

        public RouteRule Rule { get; }

        public RuleItem(RouteRule rule)
        {
            Rule = rule;
        }

        public string Title => Rule.Value;

        public string Subtitle => Rule.Kind == RuleKind.App ? Rule.Path ?? "" : "";

        public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        public ImageSource Icon { get => icon; set => Set(ref icon, value); }

        public bool ViaVpn
        {
            get => Rule.ViaVpn;
            set
            {
                if (Rule.ViaVpn != value)
                {
                    Rule.ViaVpn = value;
                    Raise(nameof(ViaVpn));
                }
            }
        }
    }

    public sealed class Candidate : Notify
    {
        private bool isChecked;

        public string Name { get; set; }
        public string Hint { get; set; }
        public string Path { get; set; }
        public bool HasWindow { get; set; }
        public ImageSource Icon { get; set; }
        public bool Checked { get => isChecked; set => Set(ref isChecked, value); }
    }
}
