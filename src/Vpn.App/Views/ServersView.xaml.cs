using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NeonVpn.Core;

namespace NeonVpn.Views
{
    public partial class ServersView : UserControl
    {
        private readonly ObservableCollection<SourceVm> sources = new ObservableCollection<SourceVm>();
        private readonly ObservableCollection<ServerVm> servers = new ObservableCollection<ServerVm>();
        private readonly Dictionary<string, ServerVm> byKey = new Dictionary<string, ServerVm>();
        private bool reloading;

        private static VpnService Vpn => App.Vpn;
        private static Settings S => App.Vpn.Settings;
        private Window Owner => Window.GetWindow(this);

        public ServersView()
        {
            InitializeComponent();
            SourceList.ItemsSource = sources;
            ServerList.ItemsSource = servers;
            SortChip.IsChecked = true;
        }

        private SourceVm Selected => SourceList.SelectedItem as SourceVm;

        // ================================================================== списки

        /// <summary>Пересобирает источники и список серверов, сохраняя выбор.</summary>
        public void Reload()
        {
            if (!IsLoaded && Visibility != Visibility.Visible)
            {
                // Страницу ещё не открывали — соберём при первом показе.
            }

            reloading = true;
            SourceVm was = Selected;
            sources.Clear();

            sources.Add(new SourceVm { Kind = ScopeKind.All, Name = "Все серверы" });

            foreach (SubscriptionInfo sub in S.Subscriptions)
            {
                sources.Add(new SourceVm { Kind = ScopeKind.Subscription, Id = sub.Id });
            }

            foreach (ServerGroup g in S.Groups)
            {
                sources.Add(new SourceVm { Kind = ScopeKind.Group, Id = g.Id });
            }

            RefreshSourceInfo();
            SourceList.SelectedItem = sources.FirstOrDefault(x => was != null && x.Kind == was.Kind && x.Id == was.Id) ?? sources[0];
            reloading = false;
            LoadServers();
        }

        private void RefreshSourceInfo()
        {
            Target t = S.Target;

            foreach (SourceVm src in sources)
            {
                switch (src.Kind)
                {
                    case ScopeKind.All:
                    {
                        int all = S.Subscriptions.Sum(x => x.Servers.Count);
                        src.Count = S.AllActive().Count() + "/" + all;
                        src.Info = S.Subscriptions.Count + " источн. · " + S.Groups.Count + " групп";
                        break;
                    }

                    case ScopeKind.Subscription:
                    {
                        SubscriptionInfo sub = S.Subscriptions.FirstOrDefault(x => x.Id == src.Id);

                        if (sub == null) continue;

                        src.Name = sub.Name + (sub.Enabled ? "" : " (выкл.)");
                        src.Count = sub.Active.Count() + "/" + sub.Servers.Count;
                        src.Info = SubInfo(sub);
                        break;
                    }

                    default:
                    {
                        ServerGroup g = S.Groups.FirstOrDefault(x => x.Id == src.Id);

                        if (g == null) continue;

                        src.Name = g.Name;
                        src.Count = S.InScope(ScopeKind.Group, g.Id).Count + "/" + g.Members.Count;
                        src.Info = "своя группа";
                        break;
                    }
                }

                src.IsAutoTarget = t.Auto && t.Scope == src.Kind && (src.Kind == ScopeKind.All || t.ScopeId == src.Id);
            }
        }

        private static string SubInfo(SubscriptionInfo sub)
        {
            if (sub.Id == "manual") return "добавлены ссылками";
            if (sub.Error.Length > 0) return "⚠ " + sub.Error;

            List<string> parts = new List<string>();

            if (sub.Total > 0) parts.Add(Format.Size(sub.Used) + " из " + Format.Size(sub.Total));
            else if (sub.Used > 0) parts.Add(Format.Size(sub.Used));

            if (sub.Expire > 0)
            {
                DateTime exp = DateTimeOffset.FromUnixTimeSeconds(sub.Expire).LocalDateTime;
                int days = (int)Math.Ceiling((exp - DateTime.Now).TotalDays);
                parts.Add(days > 0 ? "ещё " + days + " дн." : "срок истёк");
            }

            if (parts.Count == 0 && sub.Updated != null) parts.Add("обновлена " + sub.Updated.Value.ToString("dd.MM HH:mm"));
            return string.Join(" · ", parts);
        }

        /// <summary>Серверы выбранного источника.</summary>
        private List<ServerNode> SourceServers(SourceVm src)
        {
            if (src == null) return new List<ServerNode>();

            switch (src.Kind)
            {
                case ScopeKind.Subscription:
                    return S.Subscriptions.FirstOrDefault(x => x.Id == src.Id)?.Servers.ToList() ?? new List<ServerNode>();
                case ScopeKind.Group:
                {
                    ServerGroup g = S.Groups.FirstOrDefault(x => x.Id == src.Id);
                    if (g == null) return new List<ServerNode>();
                    Dictionary<string, ServerNode> all = S.Subscriptions.SelectMany(x => x.Servers).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.First());
                    return g.Members.Where(all.ContainsKey).Select(k => all[k]).ToList();
                }
                default:
                    return S.Subscriptions.SelectMany(x => x.Servers).ToList();
            }
        }

        private void LoadServers()
        {
            if (reloading)
            {
                return;
            }

            SourceVm src = Selected;
            IEnumerable<ServerNode> list = SourceServers(src);
            string q = Search.Text.Trim().ToLowerInvariant();

            if (q.Length > 0)
            {
                list = list.Where(n => (n.DisplayName + " " + n.Country + " " + n.Summary + " " + n.Host).ToLowerInvariant().Contains(q));
            }

            if (OnlyOnChip.IsChecked == true)
            {
                list = list.Where(IsIncluded);
            }

            if (SortChip.IsChecked == true)
            {
                list = list.OrderBy(n => Vpn.PingRank(n.Key));
            }

            Dictionary<string, string> subNames = S.Subscriptions.ToDictionary(x => x.Id, x => x.Name);
            Dictionary<string, List<string>> groupsOf = new Dictionary<string, List<string>>();

            foreach (ServerGroup g in S.Groups)
            {
                foreach (string k in g.Members)
                {
                    if (!groupsOf.TryGetValue(k, out var l)) groupsOf[k] = l = new List<string>();
                    l.Add(g.Name);
                }
            }

            servers.Clear();
            byKey.Clear();

            foreach (ServerNode n in list)
            {
                ServerVm vm = new ServerVm
                {
                    Node = n,
                    Summary = n.Summary,
                    Source = src?.Kind == ScopeKind.Subscription ? "" : "  ·  " + (subNames.TryGetValue(n.SourceId, out string sn) ? sn : ""),
                    Included = IsIncluded(n),
                    Groups = groupsOf.TryGetValue(n.Key, out var gl) ? "  ◆ " + string.Join(", ", gl) : ""
                };

                vm.SetPing(Vpn.PingOf(n.Key));
                byKey[n.Key] = vm;
                servers.Add(vm);
            }

            OnCurrentChanged();
            OnPings();
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            SourceVm src = Selected;
            Title.Text = src?.Name ?? "";
            UpdateButton.Visibility = src?.Kind == ScopeKind.Group || (src?.Kind == ScopeKind.Subscription && src.Id == "manual") ? Visibility.Collapsed : Visibility.Visible;

            int total = SourceServers(src).Count;
            int on = SourceServers(src).Count(IsIncluded);
            SubTitle.Text = total == 0 ? "" : $"{on} из {total} включено · «Авто» и «Лучший» смотрят только на включённые";

            bool empty = servers.Count == 0;
            Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = S.Subscriptions.Sum(x => x.Servers.Count) == 0
                ? "Серверов пока нет. Добавь ссылку подписки от провайдера или вставь ссылки vless://, vmess://, trojan://, ss://, hy2://"
                : src?.Kind == ScopeKind.Group
                    ? "Группа пустая. Выдели серверы в любом списке → правая кнопка → «В группу»."
                    : "Ничего не найдено.";
        }

        private bool IsIncluded(ServerNode n)
        {
            SubscriptionInfo sub = S.Subscriptions.FirstOrDefault(x => x.Id == n.SourceId);
            return sub != null && sub.Enabled && !sub.Excluded.Contains(n.Key);
        }

        public void OnPings()
        {
            lock (Vpn.Pinging)
            {
                foreach (ServerVm vm in servers)
                {
                    vm.Pinging = Vpn.Pinging.Contains(vm.Key);
                    vm.SetPing(Vpn.PingOf(vm.Key));
                }
            }
        }

        public void OnCurrentChanged()
        {
            string current = Vpn.Current?.Key;
            Target t = S.Target;

            foreach (ServerVm vm in servers)
            {
                vm.IsCurrent = vm.Key == current && Vpn.IsOn;
                vm.IsFixed = !t.Auto && t.ServerKey == vm.Key;
            }

            RefreshSourceInfo();
        }

        // ================================================================== события списка

        private void OnSourceChanged(object sender, SelectionChangedEventArgs e) => LoadServers();

        private void OnSearch(object sender, TextChangedEventArgs e) => LoadServers();

        private void OnSort(object sender, RoutedEventArgs e) => LoadServers();

        private void OnIncludeClick(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is ServerVm vm)
            {
                // Если отмечено несколько строк и кликнули по одной из них — меняем все выделенные.
                List<ServerVm> targets = ServerList.SelectedItems.Contains(vm) ? ServerList.SelectedItems.Cast<ServerVm>().ToList() : new List<ServerVm> { vm };
                SetIncluded(targets, cb.IsChecked == true);
            }
        }

        private void SetIncluded(IEnumerable<ServerVm> items, bool on)
        {
            foreach (ServerVm vm in items)
            {
                SubscriptionInfo sub = S.Subscriptions.FirstOrDefault(x => x.Id == vm.Node.SourceId);

                if (sub == null) continue;

                if (on) sub.Excluded.Remove(vm.Key);
                else sub.Excluded.Add(vm.Key);

                vm.Included = on;
            }

            Vpn.Save();
            RefreshSourceInfo();
            UpdateHeader();
        }

        private void OnAllOn(object sender, RoutedEventArgs e) => SetIncluded(servers.ToList(), true);

        private void OnAllOff(object sender, RoutedEventArgs e) => SetIncluded(servers.ToList(), false);

        private async void OnServerDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is ServerVm vm)
            {
                await Vpn.SelectServerAsync(vm.Key);
            }
        }

        private void OnServerMenu(object sender, ContextMenuEventArgs e)
        {
            List<ServerVm> sel = ServerList.SelectedItems.Cast<ServerVm>().ToList();

            if ((e.OriginalSource as FrameworkElement)?.DataContext is ServerVm clicked && !sel.Contains(clicked))
            {
                ServerList.SelectedItems.Clear();
                ServerList.SelectedItem = clicked;
                sel = new List<ServerVm> { clicked };
            }

            if (sel.Count == 0)
            {
                e.Handled = true;
                return;
            }

            ContextMenu menu = ServerList.ContextMenu;
            menu.Items.Clear();

            if (sel.Count == 1)
            {
                menu.Items.Add(Item("Подключить", () => _ = Vpn.SelectServerAsync(sel[0].Key)));
                menu.Items.Add(Item("Пинг", () => _ = Vpn.PingAsync(sel.Select(x => x.Node), 2)));
            }
            else
            {
                menu.Items.Add(Item($"Пинг ({sel.Count})", () => _ = Vpn.PingAsync(sel.Select(x => x.Node), 2)));
                menu.Items.Add(Item($"Лучший из выделенных ({sel.Count})", () => _ = BestOfSelection(sel)));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Включить", () => SetIncluded(sel, true)));
            menu.Items.Add(Item("Выключить", () => SetIncluded(sel, false)));
            menu.Items.Add(new Separator());

            MenuItem toGroup = new MenuItem { Header = "В группу" };

            foreach (ServerGroup g in S.Groups)
            {
                bool all = sel.All(x => g.Members.Contains(x.Key));
                toGroup.Items.Add(Item(g.Name, () => AddToGroup(g, sel), all));
            }

            if (S.Groups.Count > 0) toGroup.Items.Add(new Separator());
            toGroup.Items.Add(Item("Новая группа…", () => NewGroup(sel)));
            menu.Items.Add(toGroup);

            if (Selected?.Kind == ScopeKind.Group)
            {
                ServerGroup g = S.Groups.FirstOrDefault(x => x.Id == Selected.Id);

                if (g != null)
                {
                    menu.Items.Add(Item("Убрать из группы «" + g.Name + "»", () =>
                    {
                        g.Members.RemoveAll(k => sel.Any(x => x.Key == k));
                        Vpn.Save();
                        Reload();
                    }));
                }
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Item(sel.Count == 1 ? "Копировать ссылку" : $"Копировать ссылки ({sel.Count})", () =>
            {
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, sel.Select(x => x.Node.Link)));
                    App.Notify("Скопировано.", false);
                }
                catch
                {
                    // буфер занят другой программой
                }
            }));

            if (sel.All(x => x.Node.SourceId == "manual"))
            {
                menu.Items.Add(Item("Удалить", () =>
                {
                    S.Manual().Servers.RemoveAll(n => sel.Any(x => x.Key == n.Key));
                    Subscriptions.PruneGroups(S);
                    Vpn.Save();
                    Reload();
                }));
            }
        }

        private async System.Threading.Tasks.Task BestOfSelection(List<ServerVm> sel)
        {
            // Временная группа не нужна: пингуем выделенные и фиксируем лучший.
            var pings = await Vpn.PingAsync(sel.Select(x => x.Node), 2);
            ServerVm best = sel.Where(x => pings.TryGetValue(x.Key, out int? p) && p != null).OrderBy(x => pings[x.Key]).FirstOrDefault();

            if (best == null)
            {
                App.Notify("Ни один из выделенных серверов не ответил.", true);
                return;
            }

            S.Target = new Target { Auto = false, ServerKey = best.Key, PickedAsBest = true };
            await Vpn.SelectServerAsync(best.Key);
            S.Target.PickedAsBest = true;
            Vpn.Save();
            App.Vm.Refresh();
        }

        private static MenuItem Item(string header, Action click, bool check = false)
        {
            MenuItem i = new MenuItem { Header = header, IsChecked = check };
            i.Click += (_, _) => click();
            return i;
        }

        // ================================================================== группы

        private void AddToGroup(ServerGroup g, List<ServerVm> sel)
        {
            bool all = sel.All(x => g.Members.Contains(x.Key));

            if (all)
            {
                g.Members.RemoveAll(k => sel.Any(x => x.Key == k));
            }
            else
            {
                foreach (ServerVm vm in sel.Where(x => !g.Members.Contains(x.Key)))
                {
                    g.Members.Add(vm.Key);
                }
            }

            Vpn.Save();
            App.Notify(all ? $"Убрано из «{g.Name}»: {sel.Count}" : $"В группе «{g.Name}»: {g.Members.Count}", false);
            Reload();
        }

        private void NewGroup(List<ServerVm> sel)
        {
            string name = InputDialog.AskOne(Owner, "Новая группа", sel.Count > 0 ? $"Выделенные серверы ({sel.Count}) попадут в неё." : "Потом добавляй серверы правой кнопкой → «В группу».",
                "", "Например: Игры, Европа, Быстрые", "Создать");

            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            ServerGroup g = new ServerGroup { Name = name.Trim() };
            g.Members.AddRange(sel.Select(x => x.Key));
            S.Groups.Add(g);
            Vpn.Save();
            Reload();
            SourceList.SelectedItem = sources.FirstOrDefault(x => x.Kind == ScopeKind.Group && x.Id == g.Id);
        }

        private void OnNewGroup(object sender, RoutedEventArgs e) => NewGroup(ServerList.SelectedItems.Cast<ServerVm>().ToList());

        // ================================================================== подписки и ссылки

        private async void OnAddSubscription(object sender, RoutedEventArgs e)
        {
            string clip = "";

            try
            {
                string c = Clipboard.GetText().Trim();
                if (c.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !c.Contains('\n')) clip = c;
            }
            catch
            {
                // буфер недоступен
            }

            string[] r = InputDialog.Ask(Owner, "Новая подписка", "Ссылка от провайдера (https://…). Имя можно оставить пустым — возьму из подписки.", "Добавить",
                new InputDialog.Field { Label = "Ссылка", Value = clip, Placeholder = "https://panel.example.com/sub/…" },
                new InputDialog.Field { Label = "Имя", Placeholder = "необязательно" });

            if (r == null || r[0].Length == 0)
            {
                return;
            }

            if (!r[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (LinkParser.LooksLikeLink(r[0]))
                {
                    AddLinks(r[0]);
                    return;
                }

                App.Notify("Это не похоже на ссылку подписки (должна начинаться с https://).", true);
                return;
            }

            SubscriptionInfo sub = new SubscriptionInfo { Url = r[0], Name = r[1] };
            S.Subscriptions.Add(sub);
            Vpn.Save();
            Reload();
            SourceList.SelectedItem = sources.FirstOrDefault(x => x.Kind == ScopeKind.Subscription && x.Id == sub.Id);
            SubTitle.Text = "Загружаю…";

            string res = await Vpn.UpdateSubscriptionAsync(sub);
            Reload();
            App.Notify("Подписка «" + sub.Name + "»: " + res, res.StartsWith("ошибка"));

            if (sub.Servers.Count > 0)
            {
                _ = Vpn.PingAsync(sub.Servers);
            }
        }

        private void OnPasteLinks(object sender, RoutedEventArgs e)
        {
            string clip = "";

            try
            {
                string c = Clipboard.GetText();
                if (c.Contains("://")) clip = c.Trim();
            }
            catch
            {
                // буфер недоступен
            }

            string text = InputDialog.Ask(Owner, "Добавить серверы ссылками", "По одной в строке: vless://, vmess://, trojan://, ss://, hysteria2://, tuic://, socks://", "Добавить",
                new InputDialog.Field { Value = clip, Multiline = true, Placeholder = "vless://…" })?.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(text))
            {
                AddLinks(text);
            }
        }

        private void AddLinks(string text)
        {
            int added = Subscriptions.AddManual(S, text);
            Vpn.Save();
            Reload();
            SourceList.SelectedItem = sources.FirstOrDefault(x => x.Kind == ScopeKind.Subscription && x.Id == "manual");
            App.Notify(added > 0 ? $"Добавлено серверов: {added}" : "Не нашёл новых ссылок, которые я понимаю.", added == 0);

            if (added > 0)
            {
                _ = Vpn.PingAsync(S.Manual().Servers.TakeLast(added));

                if (Vpn.IsOn)
                {
                    _ = Vpn.ReconnectAsync();
                }
            }
        }

        private async void OnUpdate(object sender, RoutedEventArgs e)
        {
            SourceVm src = Selected;
            IEnumerable<SubscriptionInfo> subs = src?.Kind == ScopeKind.Subscription
                ? S.Subscriptions.Where(x => x.Id == src.Id)
                : S.Subscriptions.Where(x => x.Url.Length > 0);

            SubTitle.Text = "Обновляю…";

            foreach (SubscriptionInfo sub in subs.ToList())
            {
                string res = await Vpn.UpdateSubscriptionAsync(sub);
                App.Notify("«" + sub.Name + "»: " + res, res.StartsWith("ошибка"));
            }

            Reload();
        }

        // ================================================================== кнопки области

        private (ScopeKind, string) Scope()
        {
            SourceVm src = Selected;
            return src == null ? (ScopeKind.All, "") : (src.Kind, src.Id);
        }

        private async void OnBestHere(object sender, RoutedEventArgs e)
        {
            var (kind, id) = Scope();
            await Vpn.ConnectBestAsync(kind, id);
            OnCurrentChanged();
        }

        private async void OnAutoHere(object sender, RoutedEventArgs e)
        {
            var (kind, id) = Scope();
            await Vpn.SetAutoAsync(kind, id);
            OnCurrentChanged();
            App.Notify("«Авто» выбирает среди: " + S.ScopeName(kind, id), false);
        }

        private async void OnPing(object sender, RoutedEventArgs e)
        {
            List<ServerNode> list = servers.Select(x => x.Node).ToList();

            if (list.Count > 0)
            {
                await Vpn.PingAsync(list);
                Vpn.Save();

                if (SortChip.IsChecked == true)
                {
                    LoadServers();
                }
            }
        }

        private void OnMore(object sender, RoutedEventArgs e)
        {
            SourceVm src = Selected;
            ContextMenu menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };

            if (src?.Kind == ScopeKind.Subscription)
            {
                SubscriptionInfo sub = S.Subscriptions.FirstOrDefault(x => x.Id == src.Id);

                if (sub == null) return;

                menu.Items.Add(Item("Переименовать…", () =>
                {
                    string name = InputDialog.AskOne(Owner, "Имя подписки", "", sub.Name, "Имя");
                    if (!string.IsNullOrWhiteSpace(name)) { sub.Name = name; Vpn.Save(); Reload(); }
                }));

                if (sub.Id != "manual")
                {
                    menu.Items.Add(Item("Изменить ссылку…", () =>
                    {
                        string url = InputDialog.AskOne(Owner, "Ссылка подписки", "", sub.Url, "https://…");
                        if (!string.IsNullOrWhiteSpace(url)) { sub.Url = url; Vpn.Save(); OnUpdate(null, null); }
                    }));
                    menu.Items.Add(Item("Копировать ссылку подписки", () => { try { Clipboard.SetText(sub.Url); } catch { } }));
                    menu.Items.Add(Item("Новые серверы из обновлений — сразу выключены", () => { sub.NewServersOff = !sub.NewServersOff; Vpn.Save(); }, sub.NewServersOff));

                    MenuItem every = new MenuItem { Header = "Обновлять" };

                    foreach (int h in new[] { 0, 1, 6, 12, 24 })
                    {
                        int hh = h;
                        every.Items.Add(Item(h == 0 ? "вручную" : "каждые " + h + " ч", () => { sub.UpdateHours = hh; Vpn.Save(); }, sub.UpdateHours == h));
                    }

                    menu.Items.Add(every);
                }

                menu.Items.Add(Item(sub.Enabled ? "Выключить подписку целиком" : "Включить подписку", () =>
                {
                    sub.Enabled = !sub.Enabled;
                    Vpn.Save();
                    Reload();

                    if (Vpn.IsOn) _ = Vpn.ReconnectAsync();
                }));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Удалить подписку", () =>
                {
                    if (!InputDialog.Confirm(Owner, "Удалить «" + sub.Name + "»?", "Серверы подписки пропадут и из групп.", "Удалить")) return;

                    S.Subscriptions.Remove(sub);
                    Subscriptions.PruneGroups(S);

                    if (S.Target.Scope == ScopeKind.Subscription && S.Target.ScopeId == sub.Id)
                    {
                        S.Target.Scope = ScopeKind.All;
                    }

                    Vpn.Save();
                    Reload();

                    if (Vpn.IsOn) _ = Vpn.ReconnectAsync();
                }));
            }
            else if (src?.Kind == ScopeKind.Group)
            {
                ServerGroup g = S.Groups.FirstOrDefault(x => x.Id == src.Id);

                if (g == null) return;

                menu.Items.Add(Item("Переименовать…", () =>
                {
                    string name = InputDialog.AskOne(Owner, "Имя группы", "", g.Name, "Имя");
                    if (!string.IsNullOrWhiteSpace(name)) { g.Name = name; Vpn.Save(); Reload(); }
                }));
                menu.Items.Add(Item("Удалить группу", () =>
                {
                    if (!InputDialog.Confirm(Owner, "Удалить группу «" + g.Name + "»?", "Сами серверы останутся в подписках.", "Удалить")) return;

                    S.Groups.Remove(g);

                    if (S.Target.Scope == ScopeKind.Group && S.Target.ScopeId == g.Id)
                    {
                        S.Target.Scope = ScopeKind.All;
                    }

                    Vpn.Save();
                    Reload();
                }));
            }
            else
            {
                menu.Items.Add(Item("Обновить все подписки", () => OnUpdate(null, null)));
                menu.Items.Add(Item("Добавить подписку…", () => OnAddSubscription(null, null)));
                menu.Items.Add(Item("Вставить ссылки…", () => OnPasteLinks(null, null)));
            }

            menu.IsOpen = true;
        }
    }
}
