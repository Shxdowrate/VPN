using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        /// <summary>Что сейчас происходит с туннелем.</summary>
        private enum State
        {
            Off,
            Connecting,
            On,
            Switching,
            Reconnecting
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

        /// <summary>Паузы между попытками переподключения.</summary>
        private static readonly int[] ReconnectDelays = { 1, 3, 5, 10, 20 };

        private readonly AppSettings settings;
        private readonly CoreRunner runner = new CoreRunner();
        private readonly SemaphoreSlim engineGate = new SemaphoreSlim(1, 1);
        private readonly ObservableCollection<ServerItem> servers = new ObservableCollection<ServerItem>();
        private readonly ObservableCollection<RuleItem> apps = new ObservableCollection<RuleItem>();
        private readonly ObservableCollection<RuleItem> sites = new ObservableCollection<RuleItem>();
        private readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private readonly DispatcherTimer subTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        private readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        private readonly Queue<double> downHistory = new Queue<double>();
        private readonly Queue<double> upHistory = new Queue<double>();
        private readonly Queue<bool> pingHistory = new Queue<bool>();

        private State state = State.Off;
        private CoreApi api;
        private CancellationTokenSource sessionCts;
        private CancellationTokenSource reconnectCts;
        private DateTime connectedAt;
        private int tickCount;
        private bool loading = true;
        private bool busy;
        private bool started;
        private bool selectingServer;
        private List<Candidate> candidates = new List<Candidate>();

        /// <summary>Последняя ошибка подключения — показываем её, пока не нажмут кнопку снова.</summary>
        private string lastError;

        /// <summary>Сервер, через который реально идёт трафик (в «Авто» его выбирает движок).</summary>
        private string actualTag = "";
        private string autoChoice = "";
        private int? lastDelay;
        private int failStreak;
        private bool noResponseWarned;
        private int switchVersion;
        private string exitIp = "";
        private Action toastAction;

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
            toastTimer.Tick += (_, _) => HideToast();

            runner.Crashed += tail => Dispatcher.BeginInvoke(new Action(async () => await OnEngineCrashedAsync(tail)));

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

            InitShell();
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
            ReconnectCheck.IsChecked = settings.AutoReconnect;
            NotifyCheck.IsChecked = settings.Notifications;
            HotkeyCheck.IsChecked = settings.Hotkey;
            TopmostCheck.IsChecked = settings.Topmost;
            SortPing.IsChecked = settings.SortByPing;
            SortList.IsChecked = !settings.SortByPing;

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
            var favorites = new HashSet<string>(settings.Favorites, StringComparer.OrdinalIgnoreCase);
            servers.Clear();
            servers.Add(new ServerItem
            {
                Tag = "auto",
                Code = "AUTO",
                Title = "Самый быстрый",
                Subtitle = "выбирается автоматически",
                Index = -1,
                IsSelected = current == "auto" || settings.Servers.All(s => s.Tag != current)
            });

            int index = 0;

            foreach (ServerProfile s in settings.Servers)
            {
                string code = Subscription.CountryCode(s.Name);
                servers.Add(new ServerItem
                {
                    Tag = s.Tag,
                    Name = s.Name,
                    Code = code.Length > 0 ? code : "··",
                    Title = Subscription.CleanName(s.Name),
                    Subtitle = s.Protocol + " · " + s.Host,
                    Host = s.Host,
                    Port = s.Port,
                    Index = index++,
                    IsFavorite = favorites.Contains(s.Name),
                    IsSelected = s.Tag == current
                });
            }

            ServersList.SelectedItem = servers.FirstOrDefault(s => s.IsSelected);
            selectingServer = false;
            ServerCount.Text = settings.Servers.Count > 0 ? settings.Servers.Count + " серверов" : "добавь подписку";
            ApplyServerView();
            UpdateCurrentServerCard();
        }

        /// <summary>Порядок: «Авто», избранные, остальные; внутри — как в подписке или по пингу.</summary>
        private void ApplyServerView()
        {
            if (CollectionViewSource.GetDefaultView(servers) is not ListCollectionView view)
            {
                return;
            }

            string q = ServerSearch.Text.Trim();

            using (view.DeferRefresh())
            {
                view.CustomSort = new ServerOrder(settings.SortByPing);
                view.Filter = q.Length == 0
                    ? null
                    : o => o is ServerItem s && (s.Tag == "auto" || s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                                 s.Code.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void UpdateCurrentServerCard()
        {
            ServerItem selected = SelectedServerItem();

            if (selected == null)
            {
                return;
            }

            CurrentCode.Text = selected.Tag == "auto" && ActualServer() is ServerItem real ? real.Code : selected.Code;
            CurrentServer.Text = selected.Tag == "auto" && !string.IsNullOrEmpty(autoChoice) ? "Авто → " + autoChoice : selected.Title;

            if (IsConnected && lastDelay.HasValue)
            {
                CurrentPing.Text = lastDelay + " мс";
                CurrentPing.Foreground = PingBrush(lastDelay);
            }
            else
            {
                CurrentPing.Text = selected.PingText == "…" ? "" : selected.PingText;
                CurrentPing.Foreground = selected.PingBrush;
            }
        }

        private ServerItem SelectedServerItem() => servers.FirstOrDefault(s => s.IsSelected) ?? servers.FirstOrDefault();

        private ServerItem ActualServer() => string.IsNullOrEmpty(actualTag) ? null : servers.FirstOrDefault(s => s.Tag == actualTag);

        /// <summary>Понятное имя сервера, через который идёт трафик.</summary>
        private string ActualServerTitle()
        {
            ServerItem selected = SelectedServerItem();

            if (selected?.Tag == "auto")
            {
                return string.IsNullOrEmpty(autoChoice) ? "авто" : autoChoice;
            }

            return selected?.Title ?? "";
        }

        private bool IsConnected => state == State.On || state == State.Switching;

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

            double barWidth = SubBarTrack.ActualWidth > 0 ? SubBarTrack.ActualWidth : 340;

            if (settings.TrafficTotal > 0)
            {
                SubTrafficText.Text = "Использовано " + Format.Size(settings.TrafficUsed) + " из " + Format.Size(settings.TrafficTotal);
                AnimateWidth(SubBar, barWidth * Math.Clamp((double)settings.TrafficUsed / settings.TrafficTotal, 0, 1));
            }
            else
            {
                SubTrafficText.Text = "Использовано " + Format.Size(settings.TrafficUsed) + " · без лимита";
                SubBar.Width = 0;
            }

            if (settings.ExpireUnix > 0)
            {
                DateTime expire = DateTimeOffset.FromUnixTimeSeconds(settings.ExpireUnix).LocalDateTime;
                int days = (int)Math.Ceiling((expire - DateTime.Now).TotalDays);
                SubExpireText.Text = days > 0 ? "до " + expire.ToString("dd.MM.yyyy") + " · " + days + " дн." : "срок истёк";
                SubExpireText.Foreground = (Brush)FindResource(days <= 0 ? "Danger" : days <= 3 ? "Warn" : "SubText");
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
            await ToggleAsync();
        }

        /// <summary>Кнопка, трей и горячая клавиша делают одно и то же.</summary>
        private async Task ToggleAsync()
        {
            if (state == State.Reconnecting)
            {
                // Переподключение можно прервать — пользователь передумал.
                await DisconnectAsync();
                return;
            }

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

        private async Task<bool> ConnectAsync()
        {
            if (busy)
            {
                return false;
            }

            busy = true;
            lastError = null;
            SetState(State.Connecting);

            try
            {
                if (settings.Servers.Count == 0)
                {
                    await MaybeRefreshSubscriptionAsync(true);
                }

                string error = await StartEngineAsync();

                if (error != null)
                {
                    Log.Write(error);
                    lastError = error;
                    SetState(State.Off);
                    SystemNotify("Не удалось подключиться", Shorten(LastLine(error), 120), System.Windows.Forms.ToolTipIcon.Error);
                    return false;
                }

                await OnEngineUpAsync(resetSession: true);
                SystemNotify("VPN подключён", "Сервер: " + ActualServerTitle(), System.Windows.Forms.ToolTipIcon.Info);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Подключение: " + ex);
                lastError = ex.Message;
                SetState(State.Off);
                return false;
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>Движок поднялся: связь с API, нужный сервер, статистика.</summary>
        private async Task OnEngineUpAsync(bool resetSession)
        {
            StartSession();

            if (resetSession)
            {
                connectedAt = DateTime.Now;
                pingHistory.Clear();
                tickCount = 0;
            }

            failStreak = 0;
            noResponseWarned = false;
            actualTag = "";

            // Движок помнит последний выбранный сервер в своём кэше и при старте берёт его, а не тот, что в конфиге.
            // Поэтому выбор из программы ставим явно, иначе после переподключения тихо оказываемся не на том сервере.
            await SelectWithRetryAsync(settings.SelectedServer);

            SetState(State.On);
            tick.Start();
            await MeasurePingAsync();
            _ = RefreshExitInfoAsync();
        }

        private async Task<bool> SelectWithRetryAsync(string tag)
        {
            for (int i = 0; i < 4; i++)
            {
                if (api == null)
                {
                    return false;
                }

                if (await api.SelectAsync(tag))
                {
                    return true;
                }

                await Task.Delay(400);
            }

            Log.Write("Не удалось выбрать сервер " + tag + " через API движка");
            return false;
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

        private async Task<string> StartEngineAsync()
        {
            await engineGate.WaitAsync();

            try
            {
                sessionCts?.Cancel();
                return await runner.StartAsync(settings);
            }
            finally
            {
                engineGate.Release();
            }
        }

        private async Task StopEngineAsync()
        {
            await engineGate.WaitAsync();

            try
            {
                await runner.StopAsync();
            }
            finally
            {
                engineGate.Release();
            }
        }

        private async Task DisconnectAsync()
        {
            bool wasOn = state != State.Off;
            reconnectCts?.Cancel();
            busy = true;

            try
            {
                tick.Stop();
                sessionCts?.Cancel();
                sessionCts = null;
                await StopEngineAsync();
                api?.Dispose();
                api = null;
                autoChoice = "";
                actualTag = "";
                lastDelay = null;
                exitIp = "";
                SetState(State.Off);
                UpdateCurrentServerCard();

                if (wasOn)
                {
                    SystemNotify("VPN отключён", "Трафик идёт напрямую", System.Windows.Forms.ToolTipIcon.None);
                }
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>Движок упал сам. Если можно — тихо поднимаем заново, иначе честно говорим, что VPN выключен.</summary>
        private async Task OnEngineCrashedAsync(string tail)
        {
            if (state == State.Off || state == State.Connecting || state == State.Reconnecting)
            {
                return;
            }

            string reason = LastLine(tail);

            if (!settings.AutoReconnect)
            {
                await DisconnectAsync();
                lastError = "Движок остановился: " + reason;
                SetState(State.Off);
                ShowToast("VPN отключился: " + reason, ToastKind.Error);
                SystemNotify("VPN отключился", reason, System.Windows.Forms.ToolTipIcon.Warning);
                return;
            }

            await ReconnectLoopAsync(reason);
        }

        private async Task ReconnectLoopAsync(string reason)
        {
            reconnectCts?.Cancel();
            var cts = new CancellationTokenSource();
            reconnectCts = cts;
            tick.Stop();
            sessionCts?.Cancel();
            SystemNotify("Связь оборвалась", "Переподключаюсь…", System.Windows.Forms.ToolTipIcon.Warning);
            Log.Write("Переподключение: " + reason);

            string error = reason;

            for (int attempt = 1; attempt <= ReconnectDelays.Length; attempt++)
            {
                SetState(State.Reconnecting, "Попытка " + attempt + " из " + ReconnectDelays.Length);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ReconnectDelays[attempt - 1]), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                busy = true;

                try
                {
                    error = await StartEngineAsync();
                }
                finally
                {
                    busy = false;
                }

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (error == null)
                {
                    await OnEngineUpAsync(resetSession: false);
                    ShowToast("Связь восстановлена", ToastKind.Success);
                    SystemNotify("Связь восстановлена", "Сервер: " + ActualServerTitle(), System.Windows.Forms.ToolTipIcon.Info);
                    return;
                }

                Log.Write("Попытка " + attempt + ": " + LastLine(error));
            }

            await DisconnectAsync();
            lastError = "Не удалось переподключиться: " + LastLine(error);
            SetState(State.Off);
            SystemNotify("VPN отключён", "Не удалось переподключиться", System.Windows.Forms.ToolTipIcon.Error);
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
                string error = await StartEngineAsync();

                if (error != null)
                {
                    Log.Write(error);
                    busy = false;
                    await DisconnectAsync();
                    lastError = error;
                    SetState(State.Off);
                    return;
                }

                await OnEngineUpAsync(resetSession: false);
                ShowToast("Правила применены", ToastKind.Success);
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
            if (!IsConnected)
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
            if (!IsConnected || api == null)
            {
                return;
            }

            tickCount++;
            UptimeRun.Text = Format.Duration(DateTime.Now - connectedAt);

            if (tickCount % 3 == 0)
            {
                var (up, down) = await api.TotalsAsync();

                if (IsConnected)
                {
                    TotalRun.Text = " · " + Format.Size(up + down);
                }
            }

            if (tickCount % 5 == 0 && state == State.On)
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

            lastDelay = delay;
            pingHistory.Enqueue(delay.HasValue);

            while (pingHistory.Count > 20)
            {
                pingHistory.Dequeue();
            }

            PingRun.Text = delay.HasValue ? delay + " мс" : "нет ответа";
            PingRun.Foreground = PingBrush(delay);
            int lost = pingHistory.Count(ok => !ok);
            LossRun.Text = " · " + (lost * 100 / pingHistory.Count) + "%";

            TrackActualServer(now);
            UpdateCurrentServerCard();
            TrackHealth(delay);
            UpdatePill();
            UpdateTray();
        }

        /// <summary>Следим, через какой сервер реально идёт трафик, и сообщаем, если он сменился сам.</summary>
        private void TrackActualServer(string now)
        {
            if (string.IsNullOrEmpty(now))
            {
                return;
            }

            bool auto = settings.SelectedServer == "auto";

            if (!auto && now != settings.SelectedServer && now != "auto")
            {
                // Движок оказался не на том сервере (например, из своего кэша) — молча возвращаем выбранный.
                Log.Write("Движок на сервере " + now + ", а выбран " + settings.SelectedServer + " — исправляю");
                _ = api?.SelectAsync(settings.SelectedServer);
                return;
            }

            ServerProfile server = settings.Servers.FirstOrDefault(s => s.Tag == now);
            string previous = actualTag;
            actualTag = now;
            autoChoice = server != null && auto ? Subscription.CleanName(server.Name) : "";

            if (auto && !string.IsNullOrEmpty(previous) && previous != now && server != null)
            {
                string text = "Авто: переключился на " + autoChoice + (lastDelay.HasValue ? " · " + lastDelay + " мс" : "");
                Log.Write(text);
                ShowToast(text, ToastKind.Info);
                SystemNotify("Сервер сменился", text, System.Windows.Forms.ToolTipIcon.Info);
                _ = RefreshExitInfoAsync();
            }
        }

        /// <summary>Сервер перестал отвечать — предупреждаем один раз, пока связь не вернётся.</summary>
        private void TrackHealth(int? delay)
        {
            if (delay.HasValue)
            {
                if (noResponseWarned)
                {
                    noResponseWarned = false;
                    ShowToast("Сервер снова отвечает · " + delay + " мс", ToastKind.Success);
                    SetState(State.On, animate: true);
                }

                failStreak = 0;
                return;
            }

            failStreak++;

            if (failStreak >= 3 && !noResponseWarned)
            {
                noResponseWarned = true;
                SetState(State.On, animate: true);

                if (settings.SelectedServer != "auto" && settings.Servers.Count > 1)
                {
                    ShowToast("Сервер не отвечает", ToastKind.Warning, "На «Авто»", () => ChooseServer(servers.FirstOrDefault(s => s.Tag == "auto")));
                }
                else
                {
                    ShowToast("Сервер не отвечает — проверь интернет", ToastKind.Warning);
                }

                SystemNotify("Сервер не отвечает", "VPN включён, но ответа нет уже " + failStreak * 5 + " с", System.Windows.Forms.ToolTipIcon.Warning);
            }
        }

        /// <summary>Внешний IP и страна через VPN — показываем под кнопкой.</summary>
        private async Task RefreshExitInfoAsync()
        {
            if (!IsConnected)
            {
                return;
            }

            IpChip.Visibility = Visibility.Visible;
            IpText.Text = "определяю IP…";
            int port = runner.ProbePort;
            string secret = runner.ApiSecret;
            var info = await CoreApi.ExitInfoAsync(port, secret);

            if (!IsConnected)
            {
                return;
            }

            if (info == null)
            {
                exitIp = "";
                IpText.Text = "IP не определился";
                return;
            }

            exitIp = info.Value.ip;
            string country = CountryName(info.Value.country);
            IpText.Text = "IP " + exitIp + (country.Length > 0 ? " · " + country : "");
        }

        private static string CountryName(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length != 2)
            {
                return "";
            }

            try
            {
                // На русской Windows — «Нидерланды», на английской — «Netherlands».
                return new RegionInfo(code).DisplayName;
            }
            catch
            {
                return code.ToUpperInvariant();
            }
        }

        private void OnIpCopy(object sender, RoutedEventArgs e)
        {
            if (exitIp.Length == 0)
            {
                _ = RefreshExitInfoAsync();
                return;
            }

            try
            {
                Clipboard.SetText(exitIp);
                ShowToast("IP скопирован", ToastKind.Success);
            }
            catch
            {
                ShowToast("Не удалось скопировать", ToastKind.Error);
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
                CoreApi current = IsConnected ? api : null;
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

                if (settings.SortByPing)
                {
                    ApplyServerView();
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

        // ================================================================== статусы

        private static readonly Color Grey = Color.FromRgb(0x56, 0x60, 0x7A);
        private static readonly Color RingGrey = Color.FromRgb(0x2A, 0x32, 0x42);
        private static readonly Color Green = Color.FromRgb(0x3D, 0xDC, 0x97);
        private static readonly Color Amber = Color.FromRgb(0xFF, 0xB3, 0x47);
        private static readonly Color Blue = Color.FromRgb(0x2B, 0xB3, 0xFF);
        private static readonly Color Red = Color.FromRgb(0xFF, 0x5C, 0x6C);

        private void SetState(State next, string detail = null, bool animate = true)
        {
            state = next;
            int ms = animate ? 350 : 1;
            Color ring;
            Color icon;
            Color aura;
            double glow;
            bool spin = false;
            Color spinColor = Amber;

            switch (next)
            {
                case State.Connecting:
                    ring = Color.FromRgb(0x4A, 0x3E, 0x24);
                    icon = Amber;
                    aura = Amber;
                    glow = 0;
                    spin = true;
                    StatusText.Text = "Подключение…";
                    SubStatus.Text = detail ?? "Запускаю туннель";
                    break;

                case State.Reconnecting:
                    ring = Color.FromRgb(0x4A, 0x3E, 0x24);
                    icon = Amber;
                    aura = Amber;
                    glow = 0;
                    spin = true;
                    StatusText.Text = "Переподключение…";
                    SubStatus.Text = "Связь оборвалась · " + (detail ?? "пробую снова");
                    break;

                case State.Switching:
                    ring = Color.FromRgb(0x1E, 0x3A, 0x4E);
                    icon = Blue;
                    aura = Blue;
                    glow = 0.5;
                    spin = true;
                    spinColor = Blue;
                    StatusText.Text = "Смена сервера…";
                    SubStatus.Text = detail ?? "Переключаю";
                    break;

                case State.On:
                    if (noResponseWarned)
                    {
                        ring = Amber;
                        icon = Amber;
                        aura = Amber;
                        glow = 0.4;
                        StatusText.Text = "Нет ответа";
                        SubStatus.Text = "VPN включён, но сервер молчит · " + ActualServerTitle();
                    }
                    else
                    {
                        ring = Green;
                        icon = Green;
                        aura = Green;
                        glow = 1;
                        StatusText.Text = "Подключено";
                        SubStatus.Text = "Трафик защищён · " + ActualServerTitle();
                    }

                    break;

                default:
                    if (lastError != null)
                    {
                        ring = Color.FromRgb(0x4E, 0x24, 0x2C);
                        icon = Red;
                        aura = Red;
                        StatusText.Text = "Ошибка";
                        SubStatus.Text = Shorten(lastError, 200);
                    }
                    else
                    {
                        ring = RingGrey;
                        icon = Grey;
                        aura = Color.FromRgb(0x2A, 0x35, 0x50);
                        StatusText.Text = "Отключено";
                        SubStatus.Text = settings.Servers.Count > 0 ? "Нажми на кнопку, чтобы подключиться" : "Сначала добавь подписку — «Подписка» внизу";
                    }

                    glow = 0;
                    DownText.Text = "—";
                    UpText.Text = "—";
                    PingRun.Text = "—";
                    PingRun.Foreground = (Brush)FindResource("Text");
                    LossRun.Text = "";
                    UptimeRun.Text = "—";
                    TotalRun.Text = "";
                    DownLine.Points = new PointCollection();
                    UpLine.Points = new PointCollection();
                    IpChip.Visibility = Visibility.Collapsed;
                    break;
            }

            RingBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(ring, TimeSpan.FromMilliseconds(ms)));
            IconBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(icon, TimeSpan.FromMilliseconds(ms)));
            AuraStop.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Color.FromArgb((byte)(next == State.Off && lastError == null ? 0x30 : 0x38), aura.R, aura.G, aura.B), TimeSpan.FromMilliseconds(ms * 2)));
            GlowStop.Color = Color.FromArgb(0x55, aura.R, aura.G, aura.B);
            Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(glow, TimeSpan.FromMilliseconds(ms * 2)));

            if (spin)
            {
                SpinnerBrush.Color = spinColor;
                StartSpinner();
            }
            else
            {
                StopSpinner();
            }

            UpdatePill();
            UpdateTray();
        }

        /// <summary>Статус в «капсуле» сверху: цветная точка, слово и деталь (пинг).</summary>
        private void UpdatePill()
        {
            string text;
            string detail = "";
            Brush dot;
            bool pulse = false;

            switch (state)
            {
                case State.Connecting:
                    text = "Подключение";
                    dot = (Brush)FindResource("Warn");
                    pulse = true;
                    break;
                case State.Reconnecting:
                    text = "Переподключение";
                    dot = (Brush)FindResource("Warn");
                    pulse = true;
                    break;
                case State.Switching:
                    text = "Смена сервера";
                    dot = (Brush)FindResource("Accent2");
                    pulse = true;
                    break;
                case State.On when noResponseWarned:
                    text = "Нет ответа";
                    dot = (Brush)FindResource("Warn");
                    pulse = true;
                    break;
                case State.On:
                    text = "Подключено";
                    detail = lastDelay.HasValue ? lastDelay + " мс" : "";
                    dot = (Brush)FindResource("Accent");
                    break;
                default:
                    text = lastError != null ? "Ошибка" : "Отключено";
                    dot = (Brush)FindResource(lastError != null ? "Danger" : "Faint");
                    break;
            }

            PillText.Text = text;
            PillDetail.Text = detail;
            PillDot.Fill = dot;
            PillDotHalo.Fill = dot;

            if (pulse)
            {
                var grow = new DoubleAnimation(1, 2.6, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever };
                var fade = new DoubleAnimation(0.7, 0, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever };
                PillHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                PillHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                PillDotHalo.BeginAnimation(OpacityProperty, fade);
            }
            else
            {
                // Бесконечные анимации обязательно снимаем, а не просто прячем.
                PillHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                PillHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                PillDotHalo.BeginAnimation(OpacityProperty, null);
                PillDotHalo.Opacity = 0;
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

        // ================================================================== серверы

        private void OnOpenServers(object sender, RoutedEventArgs e) => OpenDrawer(DrawerPage.Servers);

        private void OnServerSelected(object sender, SelectionChangedEventArgs e)
        {
            if (selectingServer || ServersList.SelectedItem is not ServerItem item || item.IsSelected)
            {
                return;
            }

            ChooseServer(item);
        }

        /// <summary>Выбор сервера из списка, трея или уведомления.</summary>
        private async void ChooseServer(ServerItem item)
        {
            if (item == null)
            {
                return;
            }

            ServerItem previous = SelectedServerItem();

            if (previous == item)
            {
                return;
            }

            MarkSelected(item);
            settings.SelectedServer = item.Tag;
            settings.Save();
            autoChoice = "";

            if (IsConnected && api != null)
            {
                await SwitchServerAsync(item, previous);
            }
            else
            {
                UpdateCurrentServerCard();
                // Подключение/переподключение само выставит нужный сервер после старта движка.
                ShowToast(state == State.Off ? "Выбран: " + item.Title : "Сервер: " + item.Title + " — применю после подключения", ToastKind.Info);
            }
        }

        private void MarkSelected(ServerItem item)
        {
            selectingServer = true;

            foreach (ServerItem s in servers)
            {
                s.IsSelected = s == item;
            }

            ServersList.SelectedItem = item;
            selectingServer = false;
        }

        /// <summary>Смена сервера на лету — с явным статусом и подтверждением, что новый сервер отвечает.</summary>
        private async Task SwitchServerAsync(ServerItem item, ServerItem previous)
        {
            int version = ++switchVersion;
            CoreApi current = api;
            SetState(State.Switching, "На " + item.Title);
            UpdateCurrentServerCard();

            bool ok = await current.SelectAsync(item.Tag);

            if (version != switchVersion || current != api)
            {
                return;
            }

            if (!ok)
            {
                if (previous != null)
                {
                    MarkSelected(previous);
                    settings.SelectedServer = previous.Tag;
                    settings.Save();
                }

                SetState(State.On);
                UpdateCurrentServerCard();
                ShowToast("Не получилось переключить сервер", ToastKind.Error);
                SystemNotify("Сервер не переключился", "Остался " + (previous?.Title ?? "прежний"), System.Windows.Forms.ToolTipIcon.Warning);
                return;
            }

            int? delay = await current.DelayAsync("proxy");
            string now = await current.CurrentAsync();

            if (version != switchVersion || current != api)
            {
                return;
            }

            actualTag = now;
            ServerProfile real = settings.Servers.FirstOrDefault(s => s.Tag == now);
            autoChoice = item.Tag == "auto" && real != null ? Subscription.CleanName(real.Name) : "";
            lastDelay = delay;
            failStreak = delay.HasValue ? 0 : failStreak;
            noResponseWarned = false;
            SetState(State.On);
            UpdateCurrentServerCard();

            string title = item.Tag == "auto" ? "Авто" + (autoChoice.Length > 0 ? " → " + autoChoice : "") : item.Title;
            Log.Write("Сервер переключён: " + title + (delay.HasValue ? " (" + delay + " мс)" : " (нет ответа)"));

            if (delay.HasValue)
            {
                PingRun.Text = delay + " мс";
                PingRun.Foreground = PingBrush(delay);
                ShowToast("Сервер переключён: " + title + " · " + delay + " мс", ToastKind.Success);
                SystemNotify("Сервер переключён", title + " · " + delay + " мс", System.Windows.Forms.ToolTipIcon.Info);
            }
            else if (previous != null && previous.Tag != item.Tag)
            {
                ShowToast(title + " не отвечает", ToastKind.Warning, "Вернуть", () => ChooseServer(previous));
                SystemNotify("Сервер переключён, но не отвечает", title, System.Windows.Forms.ToolTipIcon.Warning);
            }

            _ = RefreshExitInfoAsync();
        }

        private void OnFavoriteToggle(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ServerItem item || item.Tag == "auto")
            {
                return;
            }

            item.IsFavorite = !item.IsFavorite;
            settings.Favorites.RemoveAll(n => n.Equals(item.Name, StringComparison.OrdinalIgnoreCase));

            if (item.IsFavorite)
            {
                settings.Favorites.Add(item.Name);
            }

            settings.Save();
            ApplyServerView();
        }

        private void OnSortChanged(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }

            settings.SortByPing = SortPing.IsChecked == true;
            settings.Save();
            ApplyServerView();

            if (settings.SortByPing && servers.Where(s => s.Tag != "auto").All(s => s.Ping == int.MaxValue))
            {
                _ = PingAllAsync();
            }
        }

        private void OnServerSearch(object sender, TextChangedEventArgs e) => ApplyServerView();

        private async void OnPingAll(object sender, RoutedEventArgs e) => await PingAllAsync();

        private async void OnRefreshSubscription(object sender, RoutedEventArgs e) => await MaybeRefreshSubscriptionAsync(true);

        // ================================================================== подписка

        private async Task MaybeRefreshSubscriptionAsync(bool force)
        {
            if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            {
                if (force)
                {
                    ShowToast("Сначала вставь ссылку в «Подписке»", ToastKind.Warning, "Открыть", () => OpenDrawer(DrawerPage.Sub));
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
                    ShowToast("В подписке не нашлось серверов" + (result.Skipped > 0 ? " (непонятных ссылок: " + result.Skipped + ")" : ""), ToastKind.Warning);
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
                ShowToast("Подписка обновлена: серверов " + result.Servers.Count, ToastKind.Success);
                WarnIfExpiring();

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
                ShowToast("Не удалось загрузить подписку: " + ex.Message, ToastKind.Error);
            }
            finally
            {
                RefreshButton.IsEnabled = true;
                RefreshButton.Content = "Обновить";
            }
        }

        /// <summary>Подписка скоро закончится или трафик на исходе — напоминаем.</summary>
        private void WarnIfExpiring()
        {
            if (settings.ExpireUnix > 0)
            {
                int days = (int)Math.Ceiling((DateTimeOffset.FromUnixTimeSeconds(settings.ExpireUnix).LocalDateTime - DateTime.Now).TotalDays);

                if (days <= 3)
                {
                    string text = days > 0 ? "Подписка закончится через " + days + " дн." : "Срок подписки истёк";
                    SystemNotify("Подписка", text, System.Windows.Forms.ToolTipIcon.Warning);
                    return;
                }
            }

            if (settings.TrafficTotal > 0 && settings.TrafficUsed >= settings.TrafficTotal * 0.9)
            {
                SystemNotify("Подписка", "Осталось меньше 10% трафика", System.Windows.Forms.ToolTipIcon.Warning);
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
                    ShowToast(preset.Name + " → через VPN", ToastKind.Success);
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
            PickerHint.Text = DefaultForNewRule ? "пойдут через VPN" : "пойдут напрямую";
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
                ShowToast("Отметь программы галочками", ToastKind.Info);
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
            ShowToast("Добавлено программ: " + chosen.Count, ToastKind.Success);
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
            if (loading)
            {
                return;
            }

            bool autostart = AutostartCheck.IsChecked == true;

            if (autostart != settings.Autostart)
            {
                Task.Run(() => Autostart.Set(autostart));
            }

            settings.Autostart = autostart;
            settings.StartMinimized = MinimizedCheck.IsChecked == true;
            settings.AutoConnect = AutoConnectCheck.IsChecked == true;
            settings.AutoReconnect = ReconnectCheck.IsChecked == true;
            settings.Notifications = NotifyCheck.IsChecked == true;

            bool hotkey = HotkeyCheck.IsChecked == true;

            if (hotkey != settings.Hotkey)
            {
                settings.Hotkey = hotkey;
                ApplyHotkey();
            }

            if (TopmostCheck.IsChecked == true != settings.Topmost)
            {
                SetTopmost(TopmostCheck.IsChecked == true);
            }

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
                ShowToast("Журнал скопирован — вставь его в чат", ToastKind.Success);
            }
            catch
            {
                ShowToast("Не удалось скопировать", ToastKind.Error);
            }
        }

        // ================================================================== уведомление в окне

        private enum ToastKind
        {
            Info,
            Success,
            Warning,
            Error
        }

        /// <summary>Всплывашка внизу корпуса. Можно добавить кнопку действия («Вернуть», «На Авто»).</summary>
        private void ShowToast(string text, ToastKind kind = ToastKind.Info, string actionText = null, Action action = null)
        {
            ToastText.Text = text;
            ToastDot.Fill = (Brush)FindResource(kind switch
            {
                ToastKind.Success => "Accent",
                ToastKind.Warning => "Warn",
                ToastKind.Error => "Danger",
                _ => "Accent2"
            });

            toastAction = action;
            ToastAction.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
            ToastAction.Content = actionText ?? "";
            Toast.IsHitTestVisible = action != null;

            Fade(Toast, 1, 180);
            ToastShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            toastTimer.Stop();
            toastTimer.Interval = TimeSpan.FromSeconds(action != null ? 6 : 3.2);
            toastTimer.Start();
        }

        private void HideToast()
        {
            toastTimer.Stop();
            Toast.IsHitTestVisible = false;
            toastAction = null;
            Fade(Toast, 0, 250);
        }

        private void OnToastAction(object sender, RoutedEventArgs e)
        {
            Action action = toastAction;
            HideToast();
            action?.Invoke();
        }

        // ================================================================== мелочи

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
        private bool isFavorite;
        private string subtitle = "";

        public string Tag { get; set; }
        public string Name { get; set; } = "";
        public string Code { get; set; }
        public string Title { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public int Index { get; set; }
        public int Ping { get; set; } = int.MaxValue;

        public string Subtitle { get => subtitle; set => Set(ref subtitle, value); }
        public string PingText { get => pingText; set => Set(ref pingText, value); }
        public Brush PingBrush { get => pingBrush; set => Set(ref pingBrush, value); }
        public bool IsFavorite { get => isFavorite; set => Set(ref isFavorite, value); }

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

        /// <summary>У «Авто» звёздочки нет.</summary>
        public Visibility StarVisibility => Tag == "auto" ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>«Авто» → избранные → остальные; внутри групп — по пингу или как в подписке.</summary>
    public sealed class ServerOrder : IComparer
    {
        private readonly bool byPing;

        public ServerOrder(bool byPing)
        {
            this.byPing = byPing;
        }

        public int Compare(object x, object y)
        {
            if (x is not ServerItem a || y is not ServerItem b)
            {
                return 0;
            }

            int group = Group(a).CompareTo(Group(b));

            if (group != 0)
            {
                return group;
            }

            if (byPing)
            {
                int ping = a.Ping.CompareTo(b.Ping);

                if (ping != 0)
                {
                    return ping;
                }
            }

            return a.Index.CompareTo(b.Index);
        }

        private static int Group(ServerItem s) => s.Tag == "auto" ? 0 : s.IsFavorite ? 1 : 2;
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
