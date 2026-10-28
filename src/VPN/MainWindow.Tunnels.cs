using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VPN.Core;

namespace VPN
{
    /// <summary>
    /// Туннели: правила активного профиля маршрутизации (программы, сайты, IP, наборы geosite/geoip),
    /// быстрое добавление одной строкой, перетаскивание, выбор из запущенных программ, сами профили.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Готовые наборы, которые обычно нужны через VPN. Записи — как в строке «Добавить».</summary>
        private static readonly (string Name, string[] Entries)[] PresetSites =
        {
            ("YouTube", new[] { "youtube.com", "googlevideo.com", "ytimg.com", "youtu.be", "ggpht.com", "youtube-nocookie.com" }),
            ("Discord", new[] { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com" }),
            ("Instagram", new[] { "instagram.com", "cdninstagram.com", "fbcdn.net" }),
            ("Facebook", new[] { "facebook.com", "fbcdn.net", "messenger.com", "fb.com" }),
            ("X / Twitter", new[] { "x.com", "twitter.com", "twimg.com", "t.co" }),
            ("ChatGPT", new[] { "chatgpt.com", "openai.com", "oaistatic.com", "oaiusercontent.com" }),
            ("Claude", new[] { "claude.ai", "anthropic.com", "claudeusercontent.com" }),
            ("Telegram", new[] { "telegram.org", "t.me", "telegram.me", "telesco.pe", "91.108.4.0/22", "91.108.8.0/22", "91.108.12.0/22",
                                 "91.108.16.0/22", "91.108.20.0/22", "91.108.56.0/22", "149.154.160.0/20", "95.161.64.0/20", "185.76.151.0/24" }),
            ("Spotify", new[] { "spotify.com", "scdn.co", "spotifycdn.com" }),
            ("Netflix", new[] { "netflix.com", "nflxvideo.net", "nflximg.net", "nflxext.com", "nflxso.net" }),
            ("Twitch", new[] { "twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net" })
        };

        private readonly ObservableCollection<RuleItem> rules = new ObservableCollection<RuleItem>();
        private readonly ObservableCollection<ProfileItem> profileItems = new ObservableCollection<ProfileItem>();
        private List<Candidate> candidates = new List<Candidate>();
        private bool loadingProfile;

        private void InitTunnels()
        {
            RulesList.ItemsSource = rules;
            ProfilesList.ItemsSource = profileItems;
            BuildPresets();

            // Перетаскивание: программы и ярлыки, ссылки на сайты, ссылки-подписки — куда угодно в окно.
            DragEnter += OnDragOverWindow;
            DragOver += OnDragOverWindow;
            DragLeave += (_, _) => HighlightDrop(false);
            Drop += OnDropWindow;
        }

        /// <summary>Показать активный профиль: режим, переключатели, правила, полосу профилей.</summary>
        private void LoadProfileIntoUi()
        {
            loadingProfile = true;
            RoutingProfile p = settings.Active;

            ModeAll.IsChecked = p.DefaultViaVpn;
            ModeSelected.IsChecked = !p.DefaultViaVpn;
            RussiaCheck.IsChecked = p.RussiaDirect;
            AdsCheck.IsChecked = p.BlockAds;
            ProfileName.Text = p.Name;
            ProfileDelete.IsEnabled = settings.Profiles.Count > 1;
            ProfileChipText.Text = p.Name;
            SetDefaultAction();

            rules.Clear();

            foreach (RouteRule rule in p.Rules)
            {
                rules.Add(new RuleItem(rule));
            }

            profileItems.Clear();

            foreach (RoutingProfile profile in settings.Profiles)
            {
                profileItems.Add(new ProfileItem(profile) { IsActive = profile.Id == p.Id });
            }

            ApplyRuleFilter();
            LoadRuleIconsAsync();
            UpdatePageHint();
            loadingProfile = false;
        }

        /// <summary>Новое правило по умолчанию делает «обратное» режиму: в «Всё через VPN» — напрямую, и наоборот.</summary>
        private void SetDefaultAction()
        {
            ActVpn.IsChecked = !settings.DefaultViaVpn;
            ActDirect.IsChecked = settings.DefaultViaVpn;
        }

        private async void LoadRuleIconsAsync()
        {
            foreach (RuleItem item in rules.Where(r => r.Rule.Kind == RuleKind.App && r.Icon == null).ToList())
            {
                string path = item.Rule.Path;
                item.Icon = await Task.Run(() => IconFor(path));
            }
        }

        /// <summary>Правила поменялись: сохранить и (с задержкой) перезапустить движок.</summary>
        private void ScheduleApply()
        {
            if (loading || loadingProfile)
            {
                return;
            }

            settings.Rules = rules.Select(r => r.Rule).ToList();
            settings.Save();
            UpdateRuleEmpties();
            UpdatePageHint();
            applyDebounce.Stop();
            applyDebounce.Start();
        }

        // ================================================================== режим и переключатели

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (loading || loadingProfile)
            {
                return;
            }

            settings.DefaultViaVpn = ModeAll.IsChecked == true;
            SetDefaultAction();
            ScheduleApply();
        }

        private void OnTunnelOption(object sender, RoutedEventArgs e)
        {
            settings.RussiaDirect = RussiaCheck.IsChecked == true;
            settings.BlockAds = AdsCheck.IsChecked == true;
            ScheduleApply();
        }

        // ================================================================== список правил

        private void OnRuleFilter(object sender, RoutedEventArgs e) => ApplyRuleFilter();

        private void ApplyRuleFilter()
        {
            // Вызывается и во время InitializeComponent (у «Все» стоит IsChecked) — тогда ещё не все элементы созданы.
            if (RulesList == null || RulesEmpty == null || FilterOther == null || CollectionViewSource.GetDefaultView(rules) is not ICollectionView view)
            {
                return;
            }

            Predicate<object> filter = null;

            if (FilterApps.IsChecked == true)
            {
                filter = o => o is RuleItem r && r.Rule.Kind == RuleKind.App;
            }
            else if (FilterSites.IsChecked == true)
            {
                filter = o => o is RuleItem r && r.Rule.Kind == RuleKind.Site;
            }
            else if (FilterOther.IsChecked == true)
            {
                filter = o => o is RuleItem r && (r.Rule.Kind == RuleKind.Ip || r.Rule.Kind == RuleKind.RuleSet);
            }

            view.Filter = filter;
            UpdateRuleEmpties();
        }

        private void UpdateRuleEmpties()
        {
            int shown = CollectionViewSource.GetDefaultView(rules)?.Cast<object>().Count() ?? rules.Count;
            RulesEmpty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            RulesCount.Text = rules.Count == 0 ? "" : rules.Count(r => r.Rule.Block) is int b && b > 0
                ? rules.Count + " правил · " + b + " блок."
                : rules.Count + " " + Plural(rules.Count, "правило", "правила", "правил");
        }

        private void OnRuleToggle(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RuleItem item)
            {
                // VPN → напрямую → блок → VPN
                if (item.Rule.Block)
                {
                    item.SetAction(viaVpn: true, block: false);
                }
                else if (item.Rule.ViaVpn)
                {
                    item.SetAction(viaVpn: false, block: false);
                }
                else
                {
                    item.SetAction(viaVpn: false, block: true);
                }

                ScheduleApply();
            }
        }

        private void OnRuleDelete(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not RuleItem item)
            {
                return;
            }

            int index = rules.IndexOf(item);
            string profileId = settings.ActiveProfileId;
            rules.Remove(item);
            ScheduleApply();

            ShowToast("Удалено: " + item.Title, ToastKind.Info, "Вернуть", () =>
            {
                if (settings.ActiveProfileId != profileId || rules.Contains(item))
                {
                    return;
                }

                rules.Insert(Math.Min(index, rules.Count), item);
                ScheduleApply();
            });
        }

        // ================================================================== добавление

        /// <summary>Действие для новых правил — как выбрано в сегменте под строкой ввода.</summary>
        private (bool ViaVpn, bool Block) NewRuleAction() =>
            ActBlock.IsChecked == true ? (false, true) : (ActVpn.IsChecked == true, false);

        /// <summary>Добавить правило (или поменять действие у уже существующего такого же).</summary>
        private RuleItem AddRule(RouteRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Value))
            {
                return null;
            }

            RuleItem existing = rules.FirstOrDefault(r => r.Rule.Kind == rule.Kind && r.Rule.Value.Equals(rule.Value, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.SetAction(rule.ViaVpn, rule.Block);

                if (rule.Path.Length > 0 && existing.Rule.Path.Length == 0)
                {
                    existing.Rule.Path = rule.Path;
                }

                return existing;
            }

            var item = new RuleItem(rule);
            rules.Insert(0, item);
            return item;
        }

        private void OnQuickAdd(object sender, RoutedEventArgs e) => AddFromQuickInput();

        private void OnQuickKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AddFromQuickInput();
            }
        }

        private void AddFromQuickInput()
        {
            string[] parts = QuickInput.Text.Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                QuickInput.Focus();
                return;
            }

            var bad = new List<string>();
            var added = new List<RuleItem>();
            var (viaVpn, block) = NewRuleAction();

            foreach (string part in parts)
            {
                RouteRule rule = LinkImport.GuessRule(part);

                if (rule == null)
                {
                    bad.Add(part);
                    continue;
                }

                rule.ViaVpn = viaVpn;
                rule.Block = block;
                RuleItem item = AddRule(rule);

                if (item != null)
                {
                    added.Add(item);
                }
            }

            QuickInput.Text = bad.Count > 0 ? string.Join(" ", bad) : "";

            if (added.Count > 0)
            {
                FinishAdding(added);
            }

            if (bad.Count > 0)
            {
                ShowToast("Не понял: " + Shorten(string.Join(", ", bad), 80) + ". Пример: youtube.com, game.exe, 1.2.3.4, geosite:twitch", ToastKind.Warning);
            }
        }

        /// <summary>Значки программ, скачивание наборов, применение и короткий итог.</summary>
        private async void FinishAdding(List<RuleItem> added)
        {
            ScheduleApply();
            LoadRuleIconsAsync();
            string action = added[0].Rule.Block ? "заблокировано" : added[0].Rule.ViaVpn ? "через VPN" : "напрямую";
            ShowToast((added.Count == 1 ? added[0].Title : "Добавлено: " + added.Count) + " → " + action, ToastKind.Success);

            if (added.Any(a => a.Rule.Kind == RuleKind.RuleSet))
            {
                await EnsureRuleSetsAsync(quiet: false);
            }
        }

        /// <summary>Скачать наборы geosite/geoip, на которые ссылается активный профиль.</summary>
        private async Task EnsureRuleSetsAsync(bool quiet)
        {
            List<string> needed = RuleSets.Needed(settings.Active).ToList();

            if (needed.Count == 0)
            {
                return;
            }

            List<string> failed = await Task.Run(() => RuleSets.EnsureAsync(needed));

            foreach (RuleItem item in rules.Where(r => r.Rule.Kind == RuleKind.RuleSet))
            {
                item.RaiseSubtitle();
            }

            if (failed.Count > 0 && !quiet)
            {
                ShowToast("Не скачался набор " + string.Join(", ", failed) + " — проверь название (например, geosite:youtube)", ToastKind.Warning);
            }
            else if (failed.Count == 0 && !quiet)
            {
                // Набор появился на диске — пусть движок его подхватит.
                applyDebounce.Stop();
                applyDebounce.Start();
            }
        }

        private void BuildPresets()
        {
            foreach (var preset in PresetSites)
            {
                var button = new Button { Content = "+ " + preset.Name, Style = (Style)FindResource("ChipButton"), Tag = preset.Entries };
                button.Click += (s, _) =>
                {
                    bool block = ActBlock.IsChecked == true;
                    var added = new List<RuleItem>();

                    foreach (string entry in (string[])((Button)s).Tag)
                    {
                        RouteRule rule = LinkImport.GuessRule(entry);

                        if (rule != null)
                        {
                            rule.ViaVpn = !block;
                            rule.Block = block;
                            added.Add(AddRule(rule));
                        }
                    }

                    ScheduleApply();
                    ShowToast(preset.Name + (block ? " → заблокирован" : " → через VPN"), ToastKind.Success);
                };
                Presets.Children.Add(button);
            }
        }

        // ================================================================== перетаскивание

        private void OnDragOverWindow(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            HighlightDrop(ok && page == Page.Tunnels);
        }

        private void HighlightDrop(bool on)
        {
            DropZone.BorderBrush = on ? (Brush)FindResource("Accent2") : (Brush)FindResource("Stroke");
        }

        private async void OnDropWindow(object sender, DragEventArgs e)
        {
            HighlightDrop(false);

            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var added = new List<RuleItem>();
                var (viaVpn, block) = NewRuleAction();

                foreach (string file in files)
                {
                    string exe = ResolveExe(file);

                    if (exe == null)
                    {
                        continue;
                    }

                    RuleItem item = AddRule(new RouteRule { Kind = RuleKind.App, Value = Path.GetFileName(exe), Path = exe, ViaVpn = viaVpn, Block = block });

                    if (item != null)
                    {
                        added.Add(item);
                    }
                }

                if (added.Count > 0)
                {
                    OpenPage(Page.Tunnels);
                    FinishAdding(added);
                }
                else
                {
                    ShowToast("Перетащи программу (.exe) или ярлык на неё", ToastKind.Warning);
                }

                return;
            }

            string text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string ?? "";
            text = text.Trim();

            if (text.Length == 0)
            {
                return;
            }

            // Ссылка-подписка или сервер — в импорт; обычная ссылка на сайт — в правила.
            if (LinkImport.LooksImportable(text) && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase) || page != Page.Tunnels && LinkImport.LooksImportable(text))
            {
                await ImportTextAsync(text);
                return;
            }

            RouteRule rule = LinkImport.GuessRule(text.Split('\n')[0]);

            if (rule == null || rule.Kind == RuleKind.App)
            {
                ShowToast("Сюда можно перетащить программу, ярлык или ссылку на сайт", ToastKind.Warning);
                return;
            }

            var action = NewRuleAction();
            rule.ViaVpn = action.ViaVpn;
            rule.Block = action.Block;
            OpenPage(Page.Tunnels);
            FinishAdding(new List<RuleItem> { AddRule(rule) });
        }

        /// <summary>exe как есть, ярлык .lnk — на что он указывает. Остальное — null.</summary>
        private static string ResolveExe(string file)
        {
            try
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();

                if (ext == ".exe")
                {
                    return file;
                }

                if (ext == ".lnk")
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");

                    if (shellType == null)
                    {
                        return null;
                    }

                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic link = shell.CreateShortcut(file);
                    string target = (string)link.TargetPath;
                    return target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Ярлык " + file + ": " + ex.Message);
            }

            return null;
        }

        // ================================================================== профили

        private void OnProfilePick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ProfileItem item)
            {
                SwitchProfile(item.Profile);
            }
        }

        private void SwitchProfile(RoutingProfile profile)
        {
            if (profile == null || profile.Id == settings.ActiveProfileId)
            {
                return;
            }

            settings.ActiveProfileId = profile.Id;
            settings.Save();
            LoadProfileIntoUi();
            ShowToast("Маршрутизация: «" + profile.Name + "»", ToastKind.Success);
            _ = EnsureRuleSetsAsync(quiet: true);
            applyDebounce.Stop();
            applyDebounce.Start();
        }

        private void OnProfileAdd(object sender, RoutedEventArgs e)
        {
            RoutingProfile copy = settings.Active.Clone(UniqueProfileName("Профиль " + (settings.Profiles.Count + 1)));
            settings.Profiles.Add(copy);
            settings.ActiveProfileId = copy.Id;
            settings.Save();
            LoadProfileIntoUi();
            ProfileName.Focus();
            ProfileName.SelectAll();
            ShowToast("Новый профиль — копия прошлого. Назови его и поменяй правила", ToastKind.Info);
        }

        private void OnProfileDelete(object sender, RoutedEventArgs e)
        {
            if (settings.Profiles.Count <= 1)
            {
                return;
            }

            RoutingProfile removed = settings.Active;
            int index = settings.Profiles.IndexOf(removed);
            settings.Profiles.Remove(removed);
            settings.ActiveProfileId = settings.Profiles[Math.Max(0, index - 1)].Id;
            settings.Save();
            LoadProfileIntoUi();
            applyDebounce.Stop();
            applyDebounce.Start();

            ShowToast("Профиль «" + removed.Name + "» удалён", ToastKind.Info, "Вернуть", () =>
            {
                if (settings.Profiles.Contains(removed))
                {
                    return;
                }

                settings.Profiles.Insert(Math.Min(index, settings.Profiles.Count), removed);
                SwitchProfile(removed);
            });
        }

        private void OnProfileNameKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnProfileRename(sender, null);
                Keyboard.ClearFocus();
            }
        }

        private void OnProfileRename(object sender, RoutedEventArgs e)
        {
            string name = ProfileName.Text.Trim();
            RoutingProfile p = settings.Active;

            if (name.Length == 0 || name == p.Name)
            {
                ProfileName.Text = p.Name;
                return;
            }

            p.Name = UniqueProfileName(name, p);
            settings.Save();
            ProfileName.Text = p.Name;
            ProfileChipText.Text = p.Name;

            foreach (ProfileItem item in profileItems)
            {
                item.RaiseName();
            }

            UpdatePageHint();
        }

        private string UniqueProfileName(string name, RoutingProfile except = null)
        {
            string result = name;

            for (int n = 2; settings.Profiles.Any(p => p != except && p.Name.Equals(result, StringComparison.OrdinalIgnoreCase)); n++)
            {
                result = name + " " + n;
            }

            return result;
        }

        /// <summary>Профиль из ссылки happ://routing/… или JSON — добавить и предложить включить.</summary>
        private void ImportRouting(RoutingProfile profile, int skipped)
        {
            profile.Name = UniqueProfileName(profile.Name);
            settings.Profiles.Add(profile);
            settings.Save();
            LoadProfileIntoUi();
            OpenPage(Page.Tunnels);

            string text = "Профиль «" + profile.Name + "» добавлен: правил " + profile.Rules.Count +
                          (skipped > 0 ? " (пропущено " + skipped + " — regexp и т. п.)" : "");
            ShowToast(text, ToastKind.Success, "Включить", () => SwitchProfile(profile));
        }

        /// <summary>Чип профиля под кнопкой: меню со всеми профилями.</summary>
        private void OnProfileChip(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = ProfileChip, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };

            foreach (RoutingProfile p in settings.Profiles)
            {
                RoutingProfile target = p;
                var item = new MenuItem { Header = p.Name, IsChecked = p.Id == settings.ActiveProfileId };
                item.Click += (_, _) => SwitchProfile(target);
                menu.Items.Add(item);
            }

            var edit = new MenuItem { Header = "Настроить туннели…" };
            edit.Click += (_, _) => OpenPage(Page.Tunnels);
            menu.Items.Add(edit);
            menu.IsOpen = true;
        }

        // ================================================================== выбор программ

        private async void OnPickerOpen(object sender, RoutedEventArgs e)
        {
            PickerOverlay.Visibility = Visibility.Visible;
            Fade(PickerOverlay, 1, 150, from: 0);
            PickerSearch.Text = "";
            PickerHint.Text = "Загружаю список…";
            PickerList.ItemsSource = null;
            candidates = await Task.Run(ListProcesses);
            var (viaVpn, block) = NewRuleAction();
            PickerHint.Text = block ? "будут заблокированы" : viaVpn ? "пойдут через VPN" : "пойдут напрямую";
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

            var (viaVpn, block) = NewRuleAction();
            var added = new List<RuleItem>();

            foreach (Candidate c in chosen)
            {
                RuleItem item = AddRule(new RouteRule { Kind = RuleKind.App, Value = c.Name, Path = c.Path ?? "", ViaVpn = viaVpn, Block = block });

                if (item != null)
                {
                    item.Icon = c.Icon;
                    added.Add(item);
                }
            }

            PickerOverlay.Visibility = Visibility.Collapsed;
            FinishAdding(added);
        }

        private void OnPickerBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выбери программу" };

            if (dialog.ShowDialog(this) == true)
            {
                var (viaVpn, block) = NewRuleAction();
                RuleItem item = AddRule(new RouteRule { Kind = RuleKind.App, Value = Path.GetFileName(dialog.FileName), Path = dialog.FileName, ViaVpn = viaVpn, Block = block });

                if (item != null)
                {
                    item.Icon = IconFor(dialog.FileName);
                    PickerOverlay.Visibility = Visibility.Collapsed;
                    FinishAdding(new List<RuleItem> { item });
                }
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

                    string exe = Path.GetFileName(path);
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
    }

    // ====================================================================== модели

    public sealed class RuleItem : Notify
    {
        private static readonly Geometry AppGlyph = MakeGlyph("M4,5 H20 V19 H4 Z M4,9 H20 M7,7 H7.5");
        private static readonly Geometry SiteGlyph = MakeGlyph("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M3,12 H21 M12,3 C8,7 8,17 12,21 M12,3 C16,7 16,17 12,21");
        private static readonly Geometry IpGlyph = MakeGlyph("M9,4 L7,20 M17,4 L15,20 M4,9 H20 M3,15 H19");
        private static readonly Geometry SetGlyph = MakeGlyph("M12,3 L21,8 L12,13 L3,8 Z M3,12 L12,17 L21,12 M3,16 L12,21 L21,16");

        private ImageSource icon;

        public RuleItem(RouteRule rule)
        {
            Rule = rule;
        }

        public RouteRule Rule { get; }

        public string Title => Rule.Kind switch
        {
            RuleKind.RuleSet when Rule.Value.StartsWith("geosite-") => "geosite: " + Rule.Value.Substring(8),
            RuleKind.RuleSet when Rule.Value.StartsWith("geoip-") => "geoip: " + Rule.Value.Substring(6),
            _ => Rule.Value
        };

        public string Subtitle => Rule.Kind switch
        {
            RuleKind.App => string.IsNullOrEmpty(Rule.Path) ? "программа" : Rule.Path,
            RuleKind.Site => "сайт и все его поддомены",
            RuleKind.Ip => Rule.Value.EndsWith("/32") || Rule.Value.EndsWith("/128") ? "IP-адрес" : "подсеть",
            _ => RuleSets.Exists(Rule.Value) ? "готовый набор правил" : "набор правил · ещё не скачан"
        };

        public ImageSource Icon
        {
            get => icon;
            set
            {
                Set(ref icon, value);
                Raise(nameof(ImageVisibility));
                Raise(nameof(GlyphVisibility));
            }
        }

        public Visibility ImageVisibility => icon != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GlyphVisibility => icon == null ? Visibility.Visible : Visibility.Collapsed;

        public Geometry Glyph => Rule.Kind switch
        {
            RuleKind.App => AppGlyph,
            RuleKind.Site => SiteGlyph,
            RuleKind.Ip => IpGlyph,
            _ => SetGlyph
        };

        public Brush GlyphBrush => (Brush)Application.Current.FindResource(Rule.Kind switch
        {
            RuleKind.Site => "Accent2",
            RuleKind.Ip => "Warn",
            RuleKind.RuleSet => "Violet",
            _ => "SubText"
        });

        /// <summary>vpn / direct / block — для цвета и подписи чипа.</summary>
        public string ActionKey => Rule.Block ? "block" : Rule.ViaVpn ? "vpn" : "direct";

        public void SetAction(bool viaVpn, bool block)
        {
            Rule.ViaVpn = viaVpn;
            Rule.Block = block;
            Raise(nameof(ActionKey));
        }

        public void RaiseSubtitle() => Raise(nameof(Subtitle));

        private static Geometry MakeGlyph(string data)
        {
            Geometry g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }
    }

    public sealed class ProfileItem : Notify
    {
        private bool isActive;

        public ProfileItem(RoutingProfile profile)
        {
            Profile = profile;
        }

        public RoutingProfile Profile { get; }
        public string Name => Profile.Name;
        public bool IsActive { get => isActive; set => Set(ref isActive, value); }

        public void RaiseName() => Raise(nameof(Name));
    }
}
