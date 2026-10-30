using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NeonVpn.Core
{
    public enum VpnState
    {
        Off,
        Connecting,
        On,
        Switching,
        Reconnecting,
        Error
    }

    /// <summary>То, что зависит от Windows: системный прокси и уведомления. Окно даёт свою реализацию.</summary>
    public interface ISystemHooks
    {
        void SetSystemProxy(string host, int port);
        void ClearSystemProxy();
    }

    /// <summary>
    /// «Мозг» клиента: подключение, переключение серверов, «Авто» по области, кнопка «Лучший»,
    /// пинги серверов, раздельная статистика VPN / напрямую, восстановление после падения ядра.
    /// </summary>
    public sealed class VpnService
    {
        public Settings Settings { get; }
        public string SettingsPath { get; set; } = Paths.Settings;

        private readonly ISystemHooks hooks;
        private readonly CoreRunner main = new CoreRunner("main");
        private readonly CoreRunner probe = new CoreRunner("probe");
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim probeGate = new SemaphoreSlim(1, 1);
        private readonly string[] selfProcesses;

        private CancellationTokenSource loops;
        private readonly PathMeter vpnMeter = new PathMeter();
        private readonly PathMeter directMeter = new PathMeter();
        private readonly TrafficSplitter splitter = new TrafficSplitter();
        private DateTime connectedAt;
        private DateTime lastAutoCheck;
        private bool evaluating;
        private int crashRetries;
        private bool proxySet;

        public VpnState State { get; private set; } = VpnState.Off;
        public string StateText { get; private set; } = "Отключено";
        public ServerNode Current { get; private set; }
        public LiveStats Stats { get; private set; } = LiveStats.Empty;
        public bool IsOn => State is VpnState.On or VpnState.Switching;
        public bool Busy => State is VpnState.Connecting or VpnState.Reconnecting;

        /// <summary>Какие серверы прямо сейчас пингуются (ключи) — чтобы показать крутилку в списке.</summary>
        public HashSet<string> Pinging { get; } = new HashSet<string>();

        public event Action StateChanged;
        public event Action StatsUpdated;
        public event Action PingsUpdated;

        /// <summary>Сервер сменился сам (Авто): (было, стало, почему).</summary>
        public event Action<ServerNode, ServerNode, string> AutoSwitched;

        /// <summary>Сообщение для пользователя (ошибка, предупреждение).</summary>
        public event Action<string> Notice;

        public VpnService(Settings settings, ISystemHooks hooks, string[] selfProcesses)
        {
            Settings = settings;
            this.hooks = hooks;
            this.selfProcesses = selfProcesses;
            main.Crashed += OnCrashed;
        }

        public void Save()
        {
            try
            {
                Settings.Save(SettingsPath);
            }
            catch (Exception e)
            {
                Log.Write("Не сохранил настройки: " + e.Message);
            }
        }

        public int? PingOf(string key)
        {
            lock (Settings.LastPing)
            {
                return Settings.LastPing.TryGetValue(key, out int v) ? v : null;
            }
        }

        /// <summary>Текст о том, что выбрано: «Авто · все серверы», «Зафиксирован лучший», «Вручную».</summary>
        public string TargetText
        {
            get
            {
                Target t = Settings.Target;
                return t.Auto ? "Авто · " + Settings.ScopeName(t.Scope, t.ScopeId) : t.PickedAsBest ? "Лучший · зафиксирован" : "Выбран вручную";
            }
        }

        // ================================================================== подключение

        public Task ConnectAsync() => Locked(() => ConnectCore(false));

        public Task DisconnectAsync() => Locked(() => DisconnectCore("Отключено"));

        public Task ToggleAsync() => IsOn || Busy ? DisconnectAsync() : ConnectAsync();

        /// <summary>Применить изменившиеся настройки (ядро, туннели, режим): переподключиться, сохранив сервер.</summary>
        public Task ReconnectAsync() => Locked(async () =>
        {
            if (State == VpnState.Off || State == VpnState.Error)
            {
                return;
            }

            await ConnectCore(true);
        });

        private async Task Locked(Func<Task> action)
        {
            await gate.WaitAsync();

            try
            {
                await action();
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task ConnectCore(bool keepCurrent)
        {
            SetState(keepCurrent ? VpnState.Reconnecting : VpnState.Connecting, keepCurrent ? "Переподключаю…" : "Подключаю…");
            StopLoops();
            await main.StopAsync();
            RestoreProxy();

            List<ServerNode> candidates = Settings.AllActive().ToList();
            Target t = Settings.Target;
            ServerNode start = null;

            if (keepCurrent && Current != null)
            {
                start = Current;
            }
            else if (!t.Auto)
            {
                start = Settings.FindServer(t.ServerKey);

                if (start == null)
                {
                    Notice?.Invoke("Выбранного сервера больше нет в подписке — включаю «Авто».");
                    t.Auto = true;
                    t.Scope = ScopeKind.All;
                }
            }

            if (start == null)
            {
                List<ServerNode> scope = Settings.InScope(t.Scope, t.ScopeId);

                if (scope.Count == 0 && t.Scope != ScopeKind.All)
                {
                    Notice?.Invoke("В «" + Settings.ScopeName(t.Scope, t.ScopeId) + "» нет включённых серверов — беру из всех.");
                    scope = candidates;
                }

                // Лучший по последним пингам; после подключения «Авто» сразу перепроверит всех.
                start = scope.OrderBy(s => PingRank(s.Key)).FirstOrDefault();
            }

            if (start == null)
            {
                Fail("Нет серверов: добавь подписку или ссылку в разделе «Серверы».");
                return;
            }

            // Явно выбранный сервер подключаем, даже если его галочка снята.
            if (candidates.All(c => c.Key != start.Key))
            {
                candidates.Insert(0, start);
            }

            try
            {
                Plan plan = BuildPlan(candidates, start.Key, probeOnly: false);

                if (plan.Nodes.Count == 0)
                {
                    Fail("Ни один сервер не подходит к ядрам: " + string.Join("; ", plan.Skipped.Select(x => x.why).Distinct()));
                    return;
                }

                foreach (var (server, why) in plan.Skipped)
                {
                    Log.Write("Пропущен сервер «" + server.DisplayName + "»: " + why);
                }

                await main.StartAsync(plan);
                Current = plan.ByTag(plan.InitialTag).Server;

                if (Settings.UseSystemProxy)
                {
                    hooks?.SetSystemProxy("127.0.0.1", Settings.ProxyPort);
                    proxySet = true;
                }

                vpnMeter.Reset();
                directMeter.Reset();
                splitter.Reset();
                connectedAt = DateTime.Now;
                lastAutoCheck = Settings.Target.Auto ? DateTime.MinValue : DateTime.Now;
                crashRetries = 0;

                string coreText = Settings.Core == CoreKind.Xray ? "Xray" : "sing-box";
                Log.Write($"Подключено: {Current.DisplayName} ({ModeText}, ядро {coreText}, серверов в работе {plan.Nodes.Count})");
                SetState(VpnState.On, "Подключено");
                StartLoops();
            }
            catch (Exception e)
            {
                await main.StopAsync();
                RestoreProxy();
                Fail("Не подключилось: " + e.Message);
            }
        }

        public string ModeText => Settings.UseTun && Settings.UseSystemProxy ? "TUN + прокси" : Settings.UseTun ? "TUN" : "системный прокси";

        private Plan BuildPlan(List<ServerNode> nodes, string startKey, bool probeOnly)
        {
            int api = CoreRunner.FreePortRange(probeOnly ? 19290 : 19190, 1);
            int xrayCount = nodes.Count;
            int xrayBase = CoreRunner.FreePortRange(probeOnly ? 23100 : 21100, xrayCount);

            return ConfigBuilder.Build(Settings, nodes, startKey, new ConfigBuilder.Options
            {
                ProbeOnly = probeOnly,
                ApiPort = api,
                XrayPortBase = xrayBase,
                SelfProcesses = selfProcesses,
                XrayFragment = Settings.XrayFragment,
                HaveXray = System.IO.File.Exists(Paths.Xray),
                HaveSingBox = true
            });
        }

        private async Task DisconnectCore(string text)
        {
            StopLoops();
            await main.StopAsync();
            RestoreProxy();
            Current = null;
            Stats = LiveStats.Empty;
            StatsUpdated?.Invoke();
            SetState(VpnState.Off, text);
        }

        private void RestoreProxy()
        {
            if (proxySet)
            {
                hooks?.ClearSystemProxy();
                proxySet = false;
            }
        }

        private void Fail(string text)
        {
            Log.Write(text);
            Current = null;
            SetState(VpnState.Error, text);
            Notice?.Invoke(text);
        }

        private void SetState(VpnState state, string text)
        {
            State = state;
            StateText = text;
            StateChanged?.Invoke();
        }

        private async void OnCrashed(string tail)
        {
            if (State is not (VpnState.On or VpnState.Switching))
            {
                return;
            }

            crashRetries++;

            if (crashRetries > 3)
            {
                await Locked(async () =>
                {
                    StopLoops();
                    RestoreProxy();
                    Fail("Ядро падает раз за разом: " + tail);
                    await Task.CompletedTask;
                });
                return;
            }

            SetState(VpnState.Reconnecting, "Ядро перезапускается…");
            await Task.Delay(TimeSpan.FromSeconds(crashRetries * 2));
            int retries = crashRetries;
            await Locked(() => ConnectCore(true));
            crashRetries = retries;
        }

        // ================================================================== выбор сервера

        /// <summary>Выбрать сервер вручную (фиксируется, «Авто» выключается).</summary>
        public async Task SelectServerAsync(string key, bool connect = true)
        {
            ServerNode node = Settings.FindServer(key);

            if (node == null)
            {
                return;
            }

            Settings.Target = new Target { Auto = false, ServerKey = key, Scope = Settings.Target.Scope, ScopeId = Settings.Target.ScopeId };
            Save();

            if (IsOn)
            {
                await SwitchTo(node, "Переключаю на " + node.DisplayName + "…");
            }
            else if (connect)
            {
                await ConnectAsync();
            }
            else
            {
                StateChanged?.Invoke();
            }
        }

        /// <summary>Включить «Авто» среди всех серверов / подписки / группы.</summary>
        public async Task SetAutoAsync(ScopeKind scope, string scopeId, bool connect = true)
        {
            Settings.Target = new Target { Auto = true, Scope = scope, ScopeId = scopeId ?? "" };
            Save();
            StateChanged?.Invoke();

            if (IsOn)
            {
                await EvaluateAuto(force: Settings.InScope(scope, scopeId).All(s => s.Key != Current?.Key), reason: "выбрано «Авто»");
            }
            else if (connect)
            {
                await ConnectAsync();
            }
        }

        /// <summary>
        /// Кнопка «Лучший»: пингует область, берёт самый быстрый и фиксирует его — дальше сервер сам не меняется.
        /// </summary>
        public async Task ConnectBestAsync(ScopeKind scope, string scopeId)
        {
            List<ServerNode> list = Settings.InScope(scope, scopeId);

            if (list.Count == 0)
            {
                Notice?.Invoke("В «" + Settings.ScopeName(scope, scopeId) + "» нет включённых серверов.");
                return;
            }

            if (!IsOn)
            {
                SetState(VpnState.Connecting, "Ищу лучший сервер…");
            }
            else
            {
                SetState(VpnState.Switching, "Ищу лучший сервер…");
            }

            Dictionary<string, int?> pings = await PingAsync(list, attempts: 2);
            ServerNode best = list.Where(s => pings.TryGetValue(s.Key, out int? p) && p != null).OrderBy(s => pings[s.Key]).FirstOrDefault();

            if (best == null)
            {
                if (IsOn)
                {
                    SetState(VpnState.On, "Подключено");
                }
                else
                {
                    SetState(VpnState.Off, "Отключено");
                }

                Notice?.Invoke("Ни один сервер в «" + Settings.ScopeName(scope, scopeId) + "» не ответил.");
                return;
            }

            Settings.Target = new Target { Auto = false, ServerKey = best.Key, PickedAsBest = true, Scope = scope, ScopeId = scopeId ?? "" };
            Save();
            Log.Write($"Лучший в «{Settings.ScopeName(scope, scopeId)}»: {best.DisplayName} ({pings[best.Key]} мс) — зафиксирован");

            if (IsOn)
            {
                await SwitchTo(best, "Переключаю на лучший…");
            }
            else
            {
                await ConnectAsync();
            }
        }

        private async Task SwitchTo(ServerNode node, string text)
        {
            if (Current?.Key == node.Key)
            {
                SetState(VpnState.On, "Подключено");
                return;
            }

            PlanNode pn = main.Plan?.ByKey(node.Key);

            if (pn != null && main.Api != null)
            {
                SetState(VpnState.Switching, text);

                if (await main.Api.SelectAsync(pn.Tag))
                {
                    Current = node;
                    vpnMeter.Reset();
                    Log.Write("Сервер: " + node.DisplayName);
                    SetState(VpnState.On, "Подключено");
                    return;
                }
            }

            // Сервера нет в работающем наборе (его галочка снята) — перезапуск с ним.
            Current = node;
            await Locked(() => ConnectCore(true));
        }

        /// <summary>«Авто»: перепингует область через работающее ядро и переходит на лучший, если он заметно быстрее.</summary>
        private async Task EvaluateAuto(bool force, string reason)
        {
            if (evaluating || !IsOn || !Settings.Target.Auto)
            {
                return;
            }

            evaluating = true;
            lastAutoCheck = DateTime.Now;

            try
            {
                Target t = Settings.Target;
                List<ServerNode> scope = Settings.InScope(t.Scope, t.ScopeId);

                if (scope.Count == 0)
                {
                    return;
                }

                Dictionary<string, int?> pings = await PingAsync(scope, attempts: 1);
                ServerNode best = scope.Where(s => pings.TryGetValue(s.Key, out int? p) && p != null).OrderBy(s => pings[s.Key]).FirstOrDefault();

                if (best == null || best.Key == Current?.Key || !IsOn)
                {
                    return;
                }

                int? cur = Current != null && pings.TryGetValue(Current.Key, out int? c) ? c : null;
                bool currentInScope = scope.Any(s => s.Key == Current?.Key);
                bool better = cur == null || !currentInScope || pings[best.Key] + Settings.AutoTolerance < cur;

                if (force || better)
                {
                    ServerNode old = Current;
                    await SwitchTo(best, "Авто: переключаю…");

                    if (Current?.Key == best.Key)
                    {
                        string why = cur == null ? reason : $"{cur} → {pings[best.Key]} мс";
                        Log.Write($"Авто: {old?.DisplayName} → {best.DisplayName} ({why})");
                        AutoSwitched?.Invoke(old, best, why);
                    }
                }
            }
            finally
            {
                evaluating = false;
            }
        }

        // ================================================================== пинги

        /// <summary>
        /// Пинг серверов настоящим запросом через сам сервер. Если VPN включён — через работающее ядро,
        /// иначе поднимается отдельное «тихое» ядро без TUN только для проверки.
        /// </summary>
        public async Task<Dictionary<string, int?>> PingAsync(IEnumerable<ServerNode> servers, int attempts = 1)
        {
            List<ServerNode> list = servers.GroupBy(s => s.Key).Select(g => g.First()).ToList();
            Dictionary<string, int?> result = new Dictionary<string, int?>();

            if (list.Count == 0)
            {
                return result;
            }

            lock (Pinging)
            {
                foreach (ServerNode s in list) Pinging.Add(s.Key);
            }

            PingsUpdated?.Invoke();

            try
            {
                CoreRunner runner = main;
                Plan plan = main.Plan;
                bool useMain = IsOn && main.Api != null && plan != null && list.All(s => plan.ByKey(s.Key) != null);

                if (!useMain)
                {
                    await probeGate.WaitAsync();
                }

                try
                {
                    if (!useMain)
                    {
                        plan = BuildPlan(list, list[0].Key, probeOnly: true);
                        runner = probe;
                        await probe.StartAsync(plan);
                    }

                    ClashApi api = runner.Api;
                    using SemaphoreSlim limit = new SemaphoreSlim(12);

                    await Task.WhenAll(list.Select(async s =>
                    {
                        PlanNode pn = plan.ByKey(s.Key);
                        int? best = null;

                        if (pn != null && api != null)
                        {
                            await limit.WaitAsync();

                            try
                            {
                                for (int i = 0; i < attempts; i++)
                                {
                                    int? ms = await api.DelayAsync(pn.Tag, Settings.TestUrl, 5000);

                                    if (ms != null && (best == null || ms < best))
                                    {
                                        best = ms;
                                    }
                                }
                            }
                            finally
                            {
                                limit.Release();
                            }
                        }

                        lock (result)
                        {
                            result[s.Key] = best;
                        }

                        lock (Settings.LastPing)
                        {
                            Settings.LastPing[s.Key] = best ?? -1;
                        }

                        lock (Pinging)
                        {
                            Pinging.Remove(s.Key);
                        }

                        PingsUpdated?.Invoke();
                    }));
                }
                finally
                {
                    if (!useMain)
                    {
                        await probe.StopAsync();
                        probeGate.Release();
                    }
                }
            }
            catch (Exception e)
            {
                Notice?.Invoke("Пинг не удался: " + e.Message);
            }
            finally
            {
                lock (Pinging)
                {
                    foreach (ServerNode s in list) Pinging.Remove(s.Key);
                }

                PingsUpdated?.Invoke();
            }

            return result;
        }

        /// <summary>Для сортировки: известный пинг, потом неизвестные, потом «нет ответа».</summary>
        public int PingRank(string key)
        {
            int? p = PingOf(key);
            return p == null ? 50_000 : p < 0 ? 100_000 : p.Value;
        }

        // ================================================================== фоновые циклы

        private void StartLoops()
        {
            loops = new CancellationTokenSource();
            CancellationToken token = loops.Token;
            _ = Task.Run(() => StatsLoop(token));
            _ = Task.Run(() => HealthLoop(token));
        }

        private void StopLoops()
        {
            loops?.Cancel();
            loops = null;
        }

        private async Task StatsLoop(CancellationToken token)
        {
            Stopwatch sw = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Раз в полсекунды: короткие соединения успевают попасть в счёт своего пути.
                    await Task.Delay(500, token);
                    ClashApi api = main.Api;

                    if (api == null)
                    {
                        continue;
                    }

                    ClashApi.Snapshot snap = await api.ConnectionsAsync(token);

                    if (snap == null)
                    {
                        continue;
                    }

                    double seconds = sw.Elapsed.TotalSeconds;
                    sw.Restart();
                    var r = splitter.Feed(snap, seconds);

                    Stats = new LiveStats
                    {
                        Uptime = DateTime.Now - connectedAt,
                        Vpn = Make(r.vpnUp, r.vpnDown, splitter.VpnTotalUp, splitter.VpnTotalDown, vpnMeter, r.vpnConns, splitter.VpnDownHist, splitter.VpnUpHist),
                        Direct = Make(r.directUp, r.directDown, splitter.DirectTotalUp, splitter.DirectTotalDown, directMeter, r.directConns, splitter.DirectDownHist, splitter.DirectUpHist)
                    };

                    StatsUpdated?.Invoke();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log.Write("Статистика: " + e.Message);
                }
            }
        }

        private static PathStats Make(long up, long down, long totalUp, long totalDown, PathMeter m, int conns, Queue<double> downHist, Queue<double> upHist) => new PathStats
        {
            Up = up,
            Down = down,
            TotalUp = totalUp,
            TotalDown = totalDown,
            Ping = m.Last,
            PingAvg = m.Average,
            Jitter = m.Jitter,
            Loss = m.Loss,
            Connections = conns,
            DownHistory = downHist.ToArray(),
            UpHistory = upHist.ToArray()
        };

        /// <summary>Каждые 3 секунды: пинг и потери через VPN и напрямую; «Авто» по расписанию и при обрыве.</summary>
        private async Task HealthLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, token);

                    if (State != VpnState.On)
                    {
                        continue;
                    }

                    if (DateTime.Now - connectedAt > TimeSpan.FromMinutes(1))
                    {
                        crashRetries = 0;
                    }

                    PlanNode pn = Current != null ? main.Plan?.ByKey(Current.Key) : null;
                    Task<int?> vpnTask = pn != null && main.Api != null ? main.Api.DelayAsync(pn.Tag, Settings.TestUrl, 4000, token) : Task.FromResult<int?>(null);
                    Task<int?> directTask = DirectPingAsync(token);
                    await Task.WhenAll(vpnTask, directTask);

                    vpnMeter.Add(vpnTask.Result);
                    directMeter.Add(directTask.Result);

                    // Сервер молчит ~10 секунд, а интернет напрямую есть — значит, дело в сервере.
                    bool serverDead = vpnMeter.FailStreak >= 3 && directMeter.Last != null;

                    if (Settings.Target.Auto && (serverDead || DateTime.Now - lastAutoCheck > TimeSpan.FromMinutes(Math.Max(1, Settings.AutoCheckMinutes))))
                    {
                        _ = EvaluateAuto(force: serverDead, reason: "сервер перестал отвечать");
                    }
                    else if (serverDead && vpnMeter.FailStreak == 3)
                    {
                        Notice?.Invoke("Сервер " + Current?.DisplayName + " не отвечает. Он зафиксирован — сам не меняю.");
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log.Write("Проверка связи: " + e.Message);
                }
            }
        }

        /// <summary>Тот же запрос, но мимо VPN (наша программа всегда идёт напрямую по правилу).</summary>
        private async Task<int?> DirectPingAsync(CancellationToken token)
        {
            try
            {
                using HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };
                Stopwatch sw = Stopwatch.StartNew();
                using HttpResponseMessage r = await http.GetAsync(Settings.TestUrl, HttpCompletionOption.ResponseHeadersRead, token);
                return (int)sw.ElapsedMilliseconds;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        // ================================================================== подписки

        /// <summary>Обновляет подписку. Если серверы поменялись, а VPN включён, — переподключает, сохраняя текущий сервер.</summary>
        public async Task<string> UpdateSubscriptionAsync(SubscriptionInfo sub)
        {
            if (string.IsNullOrWhiteSpace(sub.Url))
            {
                return "";
            }

            try
            {
                Subscriptions.Fetched fetched;

                try
                {
                    fetched = await Subscriptions.FetchAsync(sub.Url);
                }
                catch when (IsOn && (Settings.UseSystemProxy || !Settings.UseTun))
                {
                    // Напрямую не открылась — пробуем через сам VPN.
                    fetched = await Subscriptions.FetchAsync(sub.Url, "http://127.0.0.1:" + Settings.ProxyPort);
                }

                var (added, removed) = Subscriptions.Apply(sub, fetched);
                Subscriptions.PruneGroups(Settings);
                Save();
                Log.Write($"Подписка «{sub.Name}»: {sub.Servers.Count} серверов (+{added} −{removed})");

                if (IsOn && (added > 0 || removed > 0))
                {
                    if (Current != null && Settings.FindServer(Current.Key) == null)
                    {
                        Current = null;
                    }

                    await ReconnectAsync();
                }

                return $"{sub.Servers.Count} серверов" + (added + removed > 0 ? $" (+{added} −{removed})" : "");
            }
            catch (Exception e)
            {
                sub.Error = e.Message;
                Save();
                Log.Write($"Подписка «{sub.Name}» не обновилась: {e.Message}");
                return "ошибка: " + e.Message;
            }
        }

        /// <summary>Обновляет подписки, у которых подошёл срок.</summary>
        public async Task UpdateDueAsync()
        {
            foreach (SubscriptionInfo sub in Settings.Subscriptions.ToList())
            {
                if (sub.UpdateHours > 0 && sub.Url.Length > 0 && (sub.Updated == null || DateTime.Now - sub.Updated > TimeSpan.FromHours(sub.UpdateHours)))
                {
                    await UpdateSubscriptionAsync(sub);
                }
            }
        }

        public async Task ShutdownAsync()
        {
            StopLoops();
            await probe.StopAsync();
            await main.StopAsync();
            RestoreProxy();
        }
    }
}
