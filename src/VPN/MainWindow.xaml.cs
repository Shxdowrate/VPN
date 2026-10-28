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
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VPN.Core;

namespace VPN
{
    /// <summary>
    /// Главное окно. Код разложен по файлам:
    /// здесь — подключение, статусы, статистика и список серверов;
    /// MainWindow.Shell.cs — окно, форма, свечение, навигация, трей, горячая клавиша, внешние ссылки;
    /// MainWindow.Subs.cs — подписки и импорт; MainWindow.Tunnels.cs — правила и профили маршрутизации.
    /// </summary>
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

        /// <summary>Паузы между попытками переподключения.</summary>
        private static readonly int[] ReconnectDelays = { 1, 3, 5, 10, 20 };

        private readonly AppSettings settings;
        private readonly CoreRunner runner = new CoreRunner();
        private readonly SemaphoreSlim engineGate = new SemaphoreSlim(1, 1);
        private readonly ObservableCollection<ServerItem> servers = new ObservableCollection<ServerItem>();
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
        private volatile bool logVisible;

        /// <summary>Ссылка, переданная при запуске (happ://…), — обработаем, когда окно поднимется.</summary>
        private string pendingLink;

        public MainWindow(bool startMinimized, string link = null)
        {
            InitializeComponent();
            settings = AppSettings.Load();
            pendingLink = link;

            ServersList.ItemsSource = servers;

            tick.Tick += OnTick;
            applyDebounce.Tick += async (_, _) =>
            {
                applyDebounce.Stop();
                await ApplyChangesAsync();
            };
            subTimer.Tick += async (_, _) => await RefreshDueSubscriptionsAsync();
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
            InitTunnels();
            InitSubs();
            LoadIntoUi();
            CreateTray();
            SetState(State.Off, animate: false);
            OpenPage(Page.Servers);
            loading = false;

            Loaded += async (_, _) => await OnStartedAsync();

            if (startMinimized)
            {
                Dispatcher.BeginInvoke(new Action(async () => await OnStartedAsync()), DispatcherPriority.ApplicationIdle);
            }

            subTimer.Start();
        }

        private async Task OnStartedAsync()
        {
            if (started)
            {
                return;
            }

            started = true;
            RegisterLinkSchemes(force: false);

            if (pendingLink != null)
            {
                string link = pendingLink;
                pendingLink = null;
                await HandleExternalAsync(link);
            }

            await RefreshDueSubscriptionsAsync();

            if (settings.AutoConnect && settings.Servers.Count > 0 && state == State.Off)
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
            AutostartCheck.IsChecked = settings.Autostart;
            MinimizedCheck.IsChecked = settings.StartMinimized;
            AutoConnectCheck.IsChecked = settings.AutoConnect;
            ReconnectCheck.IsChecked = settings.AutoReconnect;
            NotifyCheck.IsChecked = settings.Notifications;
            HotkeyCheck.IsChecked = settings.Hotkey;
            TopmostCheck.IsChecked = settings.Topmost;
            HappLinksCheck.IsChecked = settings.HandleHappLinks;
            ClipboardCheck.IsChecked = settings.WatchClipboard;
            SortPing.IsChecked = settings.SortByPing;
            SortList.IsChecked = !settings.SortByPing;

            string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
            AboutText.Text = "VPN " + version + " — свой клиент на движке sing-box.\n" +
                             "Работает через виртуальный сетевой адаптер, поэтому просит права администратора.\n" +
                             "Закрытие окна прячет программу в трей; выход — через меню значка в трее.";

            LoadProfileIntoUi();
            RebuildServers();
            RebuildSubs();
        }

        /// <summary>Пересобрать список серверов из подписок: группы по подпискам, избранное — отдельной группой сверху.</summary>
        private void RebuildServers()
        {
            selectingServer = true;
            string current = settings.SelectedServer;
            var favorites = new HashSet<string>(settings.Favorites, StringComparer.OrdinalIgnoreCase);
            var oldPings = servers.Where(s => s.Ping != int.MaxValue).ToDictionary(s => s.Tag, s => s);
            servers.Clear();

            servers.Add(new ServerItem
            {
                Tag = "auto",
                Code = "AUTO",
                Title = "Самый быстрый",
                Subtitle = "выбирается автоматически",
                Index = -1,
                GroupKey = "Автовыбор",
                GroupOrder = 0,
                IsSelected = current == "auto" || settings.Servers.All(s => s.Tag != current)
            });

            int index = 0;
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Автовыбор", "Избранное" };

            for (int g = 0; g < settings.Subscriptions.Count; g++)
            {
                SubscriptionEntry sub = settings.Subscriptions[g];
                string groupName = sub.DisplayName;

                // Две подписки с одинаковым названием не должны слиться в одну группу.
                for (int n = 2; !usedNames.Add(groupName); n++)
                {
                    groupName = sub.DisplayName + " (" + n + ")";
                }

                foreach (ServerProfile s in sub.Servers)
                {
                    string code = Subscription.CountryCode(s.Name);
                    bool favorite = favorites.Contains(s.Name);
                    var item = new ServerItem
                    {
                        Tag = s.Tag,
                        Name = s.Name,
                        Code = code.Length > 0 ? code : "··",
                        Title = Subscription.CleanName(s.Name),
                        Subtitle = s.Host + ":" + s.Port + (favorite ? "  ·  " + sub.DisplayName : ""),
                        Host = s.Host,
                        Port = s.Port,
                        Protocol = s.Protocol,
                        Index = index++,
                        SubGroupKey = groupName,
                        SubGroupOrder = 2 + g,
                        IsFavorite = favorite,
                        IsSelected = s.Tag == current
                    };
                    item.UpdateGroup();

                    if (oldPings.TryGetValue(s.Tag, out ServerItem old) && old.Host == s.Host)
                    {
                        item.Ping = old.Ping;
                        item.PingText = old.PingText;
                        item.PingBrush = old.PingBrush;
                    }

                    servers.Add(item);
                }
            }

            ServersList.SelectedItem = servers.FirstOrDefault(s => s.IsSelected);
            selectingServer = false;
            ServersEmpty.Visibility = settings.Servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ServersList.Visibility = settings.Servers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdatePageHint();
            ApplyServerView();
            UpdateCurrentServerCard();
        }

        /// <summary>Порядок: «Авто», избранные, подписки по очереди; внутри — как в подписке или по пингу.</summary>
        private void ApplyServerView()
        {
            if (CollectionViewSource.GetDefaultView(servers) is not ListCollectionView view)
            {
                return;
            }

            string q = ServerSearch.Text.Trim();

            using (view.DeferRefresh())
            {
                if (view.GroupDescriptions.Count == 0)
                {
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServerItem.GroupKey)));
                }

                view.CustomSort = new ServerOrder(settings.SortByPing);
                view.Filter = q.Length == 0
                    ? null
                    : o => o is ServerItem s && (s.Tag == "auto" ||
                                                 s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                                 s.Code.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                                 s.Proto.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                                 s.SubGroupKey.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void UpdateCurrentServerCard()
        {
            ServerItem selected = SelectedServerItem();

            if (selected == null)
            {
                return;
            }

            ServerItem real = selected.Tag == "auto" ? ActualServer() : selected;
            CurrentCode.Text = real?.Code ?? selected.Code;
            CurrentServer.Text = selected.Tag == "auto" && !string.IsNullOrEmpty(autoChoice) ? "Авто → " + autoChoice : selected.Title;
            CurrentGroup.Text = selected.Tag == "auto" ? "Сервер · автовыбор" : "Сервер · " + selected.SubGroupKey;

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

        // ================================================================== подключение

        private async void OnPowerClick(object sender, MouseButtonEventArgs e)
        {
            // Короткий «нажим» кнопки.
            var press = new DoubleAnimation(0.93, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
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
                    await RefreshAllSubscriptionsAsync(true);
                }

                await EnsureRuleSetsAsync(quiet: true);
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

        /// <summary>Правила или серверы поменялись: если подключены — тихо перезапускаем движок.</summary>
        private async Task ApplyChangesAsync(string doneText = "Правила применены")
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
                ShowToast(doneText, ToastKind.Success);
            }
            finally
            {
                busy = false;
            }
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
            // Свечение подстраивается под качество связи.
            ApplyLook(animate: true);
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

            CopyText(exitIp, "IP скопирован");
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
        private static readonly Color Violet = Color.FromRgb(0x8B, 0x6C, 0xFF);

        private void SetState(State next, string detail = null, bool animate = true)
        {
            state = next;
            int ms = animate ? 350 : 1;
            Color ring;
            Color icon;
            double glow;
            bool spin = false;
            Color spinColor = Amber;

            switch (next)
            {
                case State.Connecting:
                    ring = Color.FromRgb(0x4A, 0x3E, 0x24);
                    icon = Amber;
                    glow = 0;
                    spin = true;
                    StatusText.Text = "Подключение…";
                    SubStatus.Text = detail ?? "Запускаю туннель";
                    break;

                case State.Reconnecting:
                    ring = Color.FromRgb(0x4A, 0x3E, 0x24);
                    icon = Amber;
                    glow = 0;
                    spin = true;
                    StatusText.Text = "Переподключение…";
                    SubStatus.Text = "Связь оборвалась · " + (detail ?? "пробую снова");
                    break;

                case State.Switching:
                    ring = Color.FromRgb(0x1E, 0x3A, 0x4E);
                    icon = Blue;
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
                        glow = 0.4;
                        StatusText.Text = "Нет ответа";
                        SubStatus.Text = "VPN включён, но сервер молчит · " + ActualServerTitle();
                    }
                    else
                    {
                        ring = Green;
                        icon = Green;
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
                        StatusText.Text = "Ошибка";
                        SubStatus.Text = Shorten(lastError, 200);
                    }
                    else
                    {
                        ring = RingGrey;
                        icon = Grey;
                        StatusText.Text = "Отключено";
                        SubStatus.Text = settings.Servers.Count > 0 ? "Нажми на кнопку, чтобы подключиться" : "Сначала добавь подписку — «Подписки» слева";
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
            // Свечение кнопки (и его «дыхание») ведёт ApplyLook → PowerBreath.
            _ = glow;
            breathOn = null;

            if (spin)
            {
                SpinnerBrush.Color = spinColor;
                StartSpinner();
            }
            else
            {
                StopSpinner();
            }

            ApplyLook(animate);
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

        private void OnOpenServers(object sender, RoutedEventArgs e) => OpenPage(Page.Servers);

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
            item.UpdateGroup();
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

        private async void OnRefreshAll(object sender, RoutedEventArgs e) => await RefreshAllSubscriptionsAsync(true);

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
            settings.WatchClipboard = ClipboardCheck.IsChecked == true;

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

            bool happ = HappLinksCheck.IsChecked == true;

            if (happ != settings.HandleHappLinks)
            {
                settings.HandleHappLinks = happ;

                if (happ)
                {
                    // Пользователь сам попросил — забираем ссылки, даже если их открывал установленный Happ.
                    bool ok = UrlScheme.Register(UrlScheme.Happ, force: true);
                    ShowToast(ok ? "Ссылки happ:// теперь открываются здесь" : "Не получилось — подробности в журнале", ok ? ToastKind.Success : ToastKind.Error);
                }
                else
                {
                    UrlScheme.Unregister(UrlScheme.Happ);
                }
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

        private void OnLogCopy(object sender, RoutedEventArgs e) => CopyText(Log.All(), "Журнал скопирован — вставь его в чат");

        // ================================================================== уведомление в окне

        private enum ToastKind
        {
            Info,
            Success,
            Warning,
            Error
        }

        /// <summary>Всплывашка внизу корпуса. Можно добавить кнопку действия («Вернуть», «Добавить»).</summary>
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
                new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            toastTimer.Stop();
            toastTimer.Interval = TimeSpan.FromSeconds(action != null ? 7 : 3.2);
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

        private void CopyText(string text, string done)
        {
            try
            {
                Clipboard.SetText(text);
                ShowToast(done, ToastKind.Success);
            }
            catch
            {
                ShowToast("Не удалось скопировать", ToastKind.Error);
            }
        }

        private static void Fade(UIElement element, double to, int ms, double? from = null)
        {
            var a = from.HasValue
                ? new DoubleAnimation(from.Value, to, TimeSpan.FromMilliseconds(ms))
                : new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            element.BeginAnimation(OpacityProperty, a);
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
        private static readonly Dictionary<string, (string Label, Color Color)> Protocols = new Dictionary<string, (string, Color)>
        {
            ["vless"] = ("VLESS", Color.FromRgb(0x8B, 0x6C, 0xFF)),
            ["vmess"] = ("VMESS", Color.FromRgb(0x4F, 0x8B, 0xFF)),
            ["trojan"] = ("TROJAN", Color.FromRgb(0xFF, 0x5F, 0xA2)),
            ["shadowsocks"] = ("SS", Color.FromRgb(0x22, 0xD3, 0xEE)),
            ["hysteria2"] = ("HY2", Color.FromRgb(0xFF, 0x9F, 0x43)),
            ["tuic"] = ("TUIC", Color.FromRgb(0x2D, 0xD4, 0xBF)),
            ["anytls"] = ("ANYTLS", Color.FromRgb(0xA3, 0xE6, 0x35)),
            ["socks"] = ("SOCKS", Color.FromRgb(0x8B, 0x95, 0xAA)),
            ["http"] = ("HTTP", Color.FromRgb(0x8B, 0x95, 0xAA))
        };

        private string pingText = "";
        private Brush pingBrush = Brushes.Gray;
        private bool isSelected;
        private bool isFavorite;
        private string subtitle = "";
        private string protocol = "";

        public string Tag { get; set; }
        public string Name { get; set; } = "";
        public string Code { get; set; }
        public string Title { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public int Index { get; set; }
        public int Ping { get; set; } = int.MaxValue;

        /// <summary>Группа в списке: подписка или «Избранное».</summary>
        public string GroupKey { get; set; } = "";
        public int GroupOrder { get; set; }

        /// <summary>Подписка, откуда сервер (группа без учёта избранного).</summary>
        public string SubGroupKey { get; set; } = "";
        public int SubGroupOrder { get; set; }

        public string Protocol
        {
            get => protocol;
            set
            {
                protocol = value ?? "";
                Raise(nameof(Proto));
                Raise(nameof(ProtoBrush));
            }
        }

        public string Proto => Protocols.TryGetValue(protocol, out var p) ? p.Label : protocol.ToUpperInvariant();

        public Brush ProtoBrush
        {
            get
            {
                Color c = Protocols.TryGetValue(protocol, out var p) ? p.Color : Color.FromRgb(0x8B, 0x95, 0xAA);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }

        public Visibility ProtoVisibility => protocol.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        public string Subtitle { get => subtitle; set => Set(ref subtitle, value); }

        public string PingText
        {
            get => pingText;
            set
            {
                Set(ref pingText, value);
                Raise(nameof(PingDotVisibility));
            }
        }

        public Brush PingBrush
        {
            get => pingBrush;
            set
            {
                Set(ref pingBrush, value);
                Raise(nameof(PingColor));
            }
        }

        /// <summary>Цвет свечения точки пинга.</summary>
        public Color PingColor => pingBrush is SolidColorBrush s ? s.Color : Colors.Gray;

        public Visibility PingDotVisibility => pingText.Length > 0 && pingText != "…" ? Visibility.Visible : Visibility.Collapsed;

        public bool IsFavorite { get => isFavorite; set => Set(ref isFavorite, value); }

        public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }

        /// <summary>У «Авто» звёздочки нет.</summary>
        public Visibility StarVisibility => Tag == "auto" ? Visibility.Collapsed : Visibility.Visible;

        public void UpdateGroup()
        {
            if (Tag == "auto")
            {
                return;
            }

            GroupKey = isFavorite ? "Избранное" : SubGroupKey;
            GroupOrder = isFavorite ? 1 : SubGroupOrder;
            Raise(nameof(GroupKey));
        }
    }

    /// <summary>«Авто» → избранные → подписки по порядку; внутри групп — по пингу или как в подписке.</summary>
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

            int group = a.GroupOrder.CompareTo(b.GroupOrder);

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
