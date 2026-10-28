using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VPN.Core;

namespace VPN
{
    /// <summary>Подписки (несколько сразу), серверы, добавленные ссылкой, и импорт из буфера и ссылок happ://.</summary>
    public partial class MainWindow
    {
        private readonly ObservableCollection<SubItem> subItems = new ObservableCollection<SubItem>();
        private bool refreshingAll;

        private void InitSubs()
        {
            SubsList.ItemsSource = subItems;
            Upd6.IsChecked = settings.SubscriptionUpdateHours == 6;
            Upd12.IsChecked = settings.SubscriptionUpdateHours != 6 && settings.SubscriptionUpdateHours != 24;
            Upd24.IsChecked = settings.SubscriptionUpdateHours == 24;
        }

        /// <summary>Карточки подписок; «Мои серверы» — в конце.</summary>
        private void RebuildSubs()
        {
            subItems.Clear();

            foreach (SubscriptionEntry sub in settings.Subscriptions.OrderBy(s => s.IsManual ? 1 : 0))
            {
                if (sub.IsManual && sub.Servers.Count == 0)
                {
                    continue;
                }

                var item = new SubItem(sub);
                item.Refresh();
                subItems.Add(item);
            }

            SubsEmpty.Visibility = subItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================================================================== обновление

        /// <summary>Обновить подписки, которым пора (по таймеру и при запуске).</summary>
        private async Task RefreshDueSubscriptionsAsync()
        {
            List<SubscriptionEntry> due = settings.Subscriptions
                .Where(s => !s.IsManual && (s.Servers.Count == 0 || DateTime.Now - s.LastUpdate > TimeSpan.FromHours(settings.SubscriptionUpdateHours)))
                .ToList();

            if (due.Count > 0)
            {
                await RefreshManyAsync(due, userAsked: false);
            }
        }

        private async Task RefreshAllSubscriptionsAsync(bool userAsked)
        {
            List<SubscriptionEntry> all = settings.Subscriptions.Where(s => !s.IsManual).ToList();

            if (all.Count == 0)
            {
                if (userAsked)
                {
                    ShowToast("Сначала добавь подписку — скопируй ссылку и нажми Ctrl + V", ToastKind.Warning, "Открыть", () => OpenPage(Page.Subs));
                }

                return;
            }

            await RefreshManyAsync(all, userAsked);
        }

        /// <summary>Обновить несколько подписок и один раз перезапустить движок, если что-то поменялось.</summary>
        private async Task RefreshManyAsync(List<SubscriptionEntry> list, bool userAsked)
        {
            if (refreshingAll)
            {
                return;
            }

            refreshingAll = true;
            RefreshAllButton.IsEnabled = false;
            int ok = 0;
            int servers = 0;
            var errors = new List<string>();

            try
            {
                foreach (SubscriptionEntry sub in list)
                {
                    string error = await LoadSubscriptionAsync(sub);

                    if (error == null)
                    {
                        ok++;
                        servers += sub.Servers.Count;
                    }
                    else
                    {
                        errors.Add(sub.DisplayName + ": " + error);
                    }
                }
            }
            finally
            {
                refreshingAll = false;
                RefreshAllButton.IsEnabled = true;
            }

            AfterServersChanged();

            if (errors.Count > 0)
            {
                ShowToast("Не удалось обновить — " + Shorten(errors[0], 140), ToastKind.Error);
            }
            else if (userAsked || ok > 0)
            {
                ShowToast(list.Count == 1
                    ? "Подписка обновлена: серверов " + servers
                    : "Обновлено подписок: " + ok + " · серверов " + servers, ToastKind.Success);
            }

            if (ok > 0)
            {
                await ApplyServersAsync();
            }
        }

        /// <summary>Скачать одну подписку и заменить её серверы. Возвращает текст ошибки или null.</summary>
        private async Task<string> LoadSubscriptionAsync(SubscriptionEntry sub)
        {
            SubItem card = subItems.FirstOrDefault(i => i.Entry == sub);

            if (card != null)
            {
                card.NotBusy = false;
                card.Info = "обновляю…";
            }

            try
            {
                SubscriptionResult result = await Task.Run(() => Subscription.LoadAsync(sub.Url));

                if (result.Servers.Count == 0)
                {
                    string why = "в подписке нет серверов" + (result.Skipped > 0 ? " (непонятных ссылок: " + result.Skipped + ")" : "");
                    Log.Write(sub.DisplayName + ": " + why);
                    return why;
                }

                // Выбранный сервер этой подписки запоминаем по имени: теги после обновления новые.
                ServerProfile selected = sub.Servers.FirstOrDefault(s => s.Tag == settings.SelectedServer);

                sub.Servers = result.Servers;
                sub.Retag();

                if (!string.IsNullOrWhiteSpace(result.Title))
                {
                    sub.Title = result.Title;
                }

                sub.Used = result.Used;
                sub.Total = result.Total;
                sub.Expire = result.Expire;
                sub.LastUpdate = DateTime.Now;

                if (selected != null)
                {
                    settings.SelectedServer = sub.Servers.FirstOrDefault(s => s.Name == selected.Name)?.Tag ?? "auto";
                }

                settings.Flatten();
                settings.Save();
                Log.Write("Подписка " + sub.DisplayName + ": серверов " + result.Servers.Count + (result.Skipped > 0 ? ", пропущено " + result.Skipped : ""));
                WarnIfExpiring(sub);
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("Подписка " + sub.DisplayName + ": " + ex.Message);
                return ex.Message;
            }
            finally
            {
                if (card != null)
                {
                    card.NotBusy = true;
                    card.Refresh();
                }
            }
        }

        /// <summary>Списки серверов и карточки — заново.</summary>
        private void AfterServersChanged()
        {
            settings.Flatten();

            if (settings.SelectedServer != "auto" && settings.Servers.All(s => s.Tag != settings.SelectedServer))
            {
                settings.SelectedServer = "auto";
            }

            settings.Save();
            RebuildServers();
            RebuildSubs();

            if (state == State.Off)
            {
                SetState(State.Off, animate: false);
            }
        }

        /// <summary>Серверы поменялись: если подключены — перезапустить движок, иначе обновить пинги.</summary>
        private async Task ApplyServersAsync()
        {
            if (state == State.On)
            {
                await ApplyChangesAsync("Серверы обновлены");
            }
            else if (state == State.Off)
            {
                _ = PingAllAsync();
            }
        }

        /// <summary>Подписка скоро закончится или трафик на исходе — напоминаем.</summary>
        private void WarnIfExpiring(SubscriptionEntry sub)
        {
            if (sub.Expire > 0)
            {
                int days = (int)Math.Ceiling((DateTimeOffset.FromUnixTimeSeconds(sub.Expire).LocalDateTime - DateTime.Now).TotalDays);

                if (days <= 3)
                {
                    string text = days > 0 ? "закончится через " + days + " дн." : "срок истёк";
                    SystemNotify("Подписка " + sub.DisplayName, text, System.Windows.Forms.ToolTipIcon.Warning);
                    return;
                }
            }

            if (sub.Total > 0 && sub.Used >= sub.Total * 0.9)
            {
                SystemNotify("Подписка " + sub.DisplayName, "Осталось меньше 10% трафика", System.Windows.Forms.ToolTipIcon.Warning);
            }
        }

        // ================================================================== импорт

        /// <summary>Разобрать текст (ссылку, список, JSON) и добавить то, что в нём есть.</summary>
        private async Task ImportTextAsync(string text)
        {
            ImportResult r = LinkImport.Classify(text);

            switch (r.Kind)
            {
                case ImportKind.Subscription:
                    await AddSubscriptionAsync(r.Url);
                    break;

                case ImportKind.Servers:
                    AddManualServers(r.Servers);
                    await ApplyServersAsync();
                    break;

                case ImportKind.Routing:
                    ImportRouting(r.Profile, r.Skipped);
                    break;

                case ImportKind.Encrypted:
                    ShowToast("Это зашифрованная ссылка Happ (happ://crypt…) — её может открыть только сам Happ. Попроси у продавца обычную ссылку на подписку.", ToastKind.Warning);
                    break;

                default:
                    ShowToast("Не понял, что это за ссылка. Нужна подписка https://…, сервер vless:// и т. п. или happ://…", ToastKind.Warning);
                    break;
            }
        }

        private async Task ImportClipboardAsync(bool explicitRequest)
        {
            string text = "";

            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch
            {
                // буфер занят другой программой
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                if (explicitRequest)
                {
                    ShowToast("В буфере обмена пусто — скопируй ссылку на подписку или сервер", ToastKind.Info);
                }

                return;
            }

            lastClipboardOffer = text.Trim();
            await ImportTextAsync(text);
        }

        /// <summary>Ссылка, переданная программе (happ://…, shxvpn://…) или при запуске.</summary>
        private async Task HandleExternalAsync(string link)
        {
            Log.Write("Ссылка из другой программы: " + Shorten(link.Split('?')[0], 60));
            await ImportTextAsync(link);
        }

        /// <summary>Подписка с такой ссылкой уже есть — предлагать её из буфера не нужно.</summary>
        private bool AlreadyKnown(string text)
        {
            ImportResult r = LinkImport.Classify(text);
            return r.Kind == ImportKind.Subscription && settings.Subscriptions.Any(s => s.Url.Equals(r.Url, StringComparison.OrdinalIgnoreCase));
        }

        private async Task AddSubscriptionAsync(string url)
        {
            SubscriptionEntry existing = settings.Subscriptions.FirstOrDefault(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                ShowToast("Такая подписка уже есть — обновляю", ToastKind.Info);
                await RefreshManyAsync(new List<SubscriptionEntry> { existing }, userAsked: true);
                return;
            }

            var sub = new SubscriptionEntry { Url = url };
            // Ручная группа — всегда последней.
            int manualIndex = settings.Subscriptions.FindIndex(s => s.IsManual);
            settings.Subscriptions.Insert(manualIndex >= 0 ? manualIndex : settings.Subscriptions.Count, sub);
            RebuildSubs();
            OpenPage(Page.Subs);

            string error = await LoadSubscriptionAsync(sub);

            if (error != null)
            {
                settings.Subscriptions.Remove(sub);
                AfterServersChanged();
                ShowToast("Не удалось загрузить подписку: " + Shorten(error, 140), ToastKind.Error);
                return;
            }

            AfterServersChanged();
            ShowToast("Подписка «" + sub.DisplayName + "» добавлена: серверов " + sub.Servers.Count, ToastKind.Success, "К серверам", () => OpenPage(Page.Servers));
            await ApplyServersAsync();
        }

        /// <summary>Серверы из ссылок — в группу «Мои серверы» (без дублей).</summary>
        private void AddManualServers(SubscriptionResult result)
        {
            SubscriptionEntry manual = settings.Manual();
            var known = new HashSet<string>(manual.Servers.Select(Key));
            var added = new List<ServerProfile>();

            foreach (ServerProfile s in result.Servers)
            {
                if (known.Add(Key(s)))
                {
                    manual.Servers.Add(s);
                    added.Add(s);
                }
            }

            if (added.Count == 0)
            {
                ShowToast("Эти серверы уже есть в «Моих серверах»", ToastKind.Info);
                return;
            }

            manual.Retag();
            manual.LastUpdate = DateTime.Now;
            AfterServersChanged();
            OpenPage(Page.Servers);

            if (added.Count == 1)
            {
                ServerItem item = servers.FirstOrDefault(s => s.Tag == added[0].Tag);
                ShowToast("Сервер «" + Subscription.CleanName(added[0].Name) + "» добавлен", ToastKind.Success, "Выбрать", () => ChooseServer(item));
            }
            else
            {
                ShowToast("Добавлено серверов: " + added.Count, ToastKind.Success);
            }

            static string Key(ServerProfile s) => s.Protocol + "|" + s.Host + "|" + s.Port + "|" + s.Outbound;
        }

        // ================================================================== кнопки раздела

        private async void OnImportAdd(object sender, RoutedEventArgs e) => await ImportFromInputAsync();

        private async void OnImportKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await ImportFromInputAsync();
            }
        }

        private async Task ImportFromInputAsync()
        {
            string text = ImportInput.Text.Trim();

            if (text.Length == 0)
            {
                await ImportClipboardAsync(explicitRequest: true);
                return;
            }

            ImportInput.Text = "";
            await ImportTextAsync(text);
        }

        private async void OnPasteImport(object sender, RoutedEventArgs e) => await ImportClipboardAsync(explicitRequest: true);

        private async void OnSubRefresh(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SubItem item && !item.Entry.IsManual)
            {
                await RefreshManyAsync(new List<SubscriptionEntry> { item.Entry }, userAsked: true);
            }
        }

        private void OnSubCopy(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is SubItem item && item.Entry.Url.Length > 0)
            {
                CopyText(item.Entry.Url, "Ссылка скопирована");
            }
        }

        /// <summary>Удаление — сразу, но с кнопкой «Вернуть» в уведомлении.</summary>
        private async void OnSubDelete(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SubItem item)
            {
                return;
            }

            SubscriptionEntry sub = item.Entry;
            int index = settings.Subscriptions.IndexOf(sub);
            string selected = settings.SelectedServer;
            settings.Subscriptions.Remove(sub);
            AfterServersChanged();
            await ApplyServersAsync();

            ShowToast("Удалено: " + sub.DisplayName, ToastKind.Info, "Вернуть", async () =>
            {
                if (settings.Subscriptions.Contains(sub))
                {
                    return;
                }

                settings.Subscriptions.Insert(Math.Min(index, settings.Subscriptions.Count), sub);
                settings.SelectedServer = selected;
                AfterServersChanged();
                await ApplyServersAsync();
            });
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
    }

    /// <summary>Карточка подписки в разделе «Подписки».</summary>
    public sealed class SubItem : Notify
    {
        private static readonly Brush DangerBrush = Frozen(0xFF, 0x5C, 0x6C);
        private static readonly Brush WarnBrush = Frozen(0xFF, 0xB3, 0x47);
        private static readonly Brush OkBrush = Frozen(0x8B, 0x95, 0xAA);

        private string title = "";
        private string trafficText = "";
        private string info = "";
        private string expireText = "";
        private Brush expireBrush = OkBrush;
        private Brush expireBack = Brushes.Transparent;
        private double fraction;
        private Brush barBrush;
        private bool notBusy = true;

        public SubItem(SubscriptionEntry entry)
        {
            Entry = entry;
        }

        public SubscriptionEntry Entry { get; }

        public string Title { get => title; set => Set(ref title, value); }
        public string TrafficText { get => trafficText; set => Set(ref trafficText, value); }
        public string Info { get => info; set => Set(ref info, value); }
        public string ExpireText { get => expireText; set { Set(ref expireText, value); Raise(nameof(ExpireVisibility)); } }
        public Brush ExpireBrush { get => expireBrush; set => Set(ref expireBrush, value); }
        public Brush ExpireBack { get => expireBack; set => Set(ref expireBack, value); }
        public double Fraction { get => fraction; set => Set(ref fraction, value); }
        public Brush BarBrush { get => barBrush; set => Set(ref barBrush, value); }
        public bool NotBusy { get => notBusy; set => Set(ref notBusy, value); }

        public Visibility ExpireVisibility => expireText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BarVisibility => Entry.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RefreshVisibility => Entry.IsManual ? Visibility.Collapsed : Visibility.Visible;

        public void Refresh()
        {
            SubscriptionEntry e = Entry;
            Title = e.DisplayName;

            if (e.IsManual)
            {
                TrafficText = "Серверы, добавленные ссылкой";
                Info = "Серверов: " + e.Servers.Count;
                ExpireText = "";
                Raise(nameof(BarVisibility));
                return;
            }

            if (e.Total > 0)
            {
                TrafficText = "Использовано " + Format.Size(e.Used) + " из " + Format.Size(e.Total);
                double f = Math.Clamp((double)e.Used / e.Total, 0, 1);
                Fraction = f;
                BarBrush = f >= 0.9 ? DangerBrush : f >= 0.75 ? WarnBrush : (Brush)Application.Current.FindResource("AccentGradient");
            }
            else
            {
                TrafficText = e.LastUpdate > DateTime.MinValue ? "Использовано " + Format.Size(e.Used) + " · без лимита" : "ещё не загружалась";
                Fraction = 0;
            }

            Raise(nameof(BarVisibility));

            if (e.Expire > 0)
            {
                DateTime expire = DateTimeOffset.FromUnixTimeSeconds(e.Expire).LocalDateTime;
                int days = (int)Math.Ceiling((expire - DateTime.Now).TotalDays);
                ExpireText = days > 0 ? "до " + expire.ToString("dd.MM.yyyy") + " · " + days + " дн." : "срок истёк";
                ExpireBrush = days <= 0 ? DangerBrush : days <= 3 ? WarnBrush : OkBrush;
                ExpireBack = Frozen(days <= 0 ? (byte)0x3A : days <= 3 ? (byte)0x3A : (byte)0x1F, days <= 0 ? (byte)0x1A : days <= 3 ? (byte)0x2E : (byte)0x27,
                    days <= 0 ? (byte)0x22 : days <= 3 ? (byte)0x1A : (byte)0x38);
            }
            else
            {
                ExpireText = "";
            }

            Info = "Серверов: " + e.Servers.Count +
                   (e.LastUpdate > DateTime.MinValue ? "   ·   обновлено " + e.LastUpdate.ToString("dd.MM HH:mm") : "") +
                   "   ·   " + HostOf(e.Url);
        }

        private static string HostOf(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                return "";
            }
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
