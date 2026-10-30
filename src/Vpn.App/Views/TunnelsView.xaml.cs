using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using NeonVpn.Core;

namespace NeonVpn.Views
{
    public partial class TunnelsView : UserControl
    {
        private readonly ObservableCollection<RuleVm> rules = new ObservableCollection<RuleVm>();
        private readonly DispatcherTimer apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };

        private static TunnelSettings T => App.Vpn.Settings.Tunnel;

        /// <summary>Частые случаи — одним кликом.</summary>
        private static readonly (string label, RuleKind kind, string value, RouteAction action)[] Quick =
        {
            ("YouTube → VPN", RuleKind.Domain, "youtube.com", RouteAction.Vpn),
            ("Discord → VPN", RuleKind.App, "Discord.exe", RouteAction.Vpn),
            ("Instagram → VPN", RuleKind.Domain, "instagram.com", RouteAction.Vpn),
            ("ChatGPT → VPN", RuleKind.Domain, "chatgpt.com", RouteAction.Vpn),
            ("Telegram → VPN", RuleKind.App, "Telegram.exe", RouteAction.Vpn),
            ("Steam → напрямую", RuleKind.App, "steam.exe", RouteAction.Direct),
            ("Торренты → напрямую", RuleKind.App, "qbittorrent.exe", RouteAction.Direct),
            ("Госуслуги → напрямую", RuleKind.Domain, "gosuslugi.ru", RouteAction.Direct)
        };

        public TunnelsView()
        {
            InitializeComponent();
            Rules.ItemsSource = rules;
            apply.Tick += async (_, _) =>
            {
                apply.Stop();
                App.Vpn.Save();

                if (App.Vpn.IsOn)
                {
                    await App.Vpn.ReconnectAsync();
                    App.Notify("Туннели применены.", false);
                }
            };

            foreach (var q in Quick)
            {
                Button b = new Button { Content = q.label, Style = (Style)FindResource("NeonButton"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4, 10, 4), FontSize = 11.5 };

                b.Click += (_, _) => AddRule(q.kind, q.value, q.action);
                QuickPanel.Children.Add(b);
            }

            OnKind(null, null);
        }

        public void Reload()
        {
            DefVpn.IsChecked = T.Default != RouteAction.Direct;
            DefDirect.IsChecked = T.Default == RouteAction.Direct;
            RuDirect.IsChecked = T.RussiaDirect;
            LanDirect.IsChecked = T.LanDirect;
            Ads.IsChecked = T.BlockAds;
            DefHint.Text = T.Default == RouteAction.Direct
                ? "Через VPN пойдёт только то, что ты указал справа."
                : "Через VPN идёт всё, кроме того, что указано «напрямую».";

            rules.Clear();

            foreach (RouteRule r in T.Rules)
            {
                rules.Add(new RuleVm { Rule = r });
            }

            Count.Text = rules.Count > 0 ? rules.Count.ToString() : "";
            EmptyRules.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Changed()
        {
            Reload();
            apply.Stop();
            apply.Start();
        }

        private void OnDefault(object sender, RoutedEventArgs e)
        {
            T.Default = DefDirect.IsChecked == true ? RouteAction.Direct : RouteAction.Vpn;
            Changed();
        }

        private void OnPreset(object sender, RoutedEventArgs e)
        {
            T.RussiaDirect = RuDirect.IsChecked == true;
            T.LanDirect = LanDirect.IsChecked == true;
            T.BlockAds = Ads.IsChecked == true;
            Changed();
        }

        private RuleKind Kind => KindDomain.IsChecked == true ? RuleKind.Domain : KindIp.IsChecked == true ? RuleKind.Ip : RuleKind.App;

        private RouteAction Action => ActDirect.IsChecked == true ? RouteAction.Direct : ActBlock.IsChecked == true ? RouteAction.Block : RouteAction.Vpn;

        private void OnKind(object sender, RoutedEventArgs e)
        {
            if (Input == null)
            {
                return;
            }

            Input.Tag = Kind switch
            {
                RuleKind.App => "chrome.exe или полный путь к программе",
                RuleKind.Domain => "youtube.com — вместе со всеми поддоменами",
                _ => "1.2.3.4 или 10.0.0.0/8"
            };

            Visibility apps = Kind == RuleKind.App ? Visibility.Visible : Visibility.Collapsed;
            BrowseButton.Visibility = apps;
            RunningButton.Visibility = apps;
        }

        private void OnInputKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnAdd(null, null);
            }
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            // Можно вставить несколько через запятую или пробел.
            foreach (string v in Input.Text.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                AddRule(Kind, v.Trim(), Action, quiet: true);
            }

            Input.Text = "";
            Changed();
        }

        private void AddRule(RuleKind kind, string value, RouteAction action, bool quiet = false)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (kind == RuleKind.Domain)
            {
                value = ConfigBuilder.NormalizeDomain(value);
            }
            else if (kind == RuleKind.App && !value.Contains('\\') && !value.Contains('/') && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value += ".exe";
            }

            RouteRule existing = T.Rules.FirstOrDefault(r => r.Kind == kind && string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Action = action;
            }
            else
            {
                T.Rules.Add(new RouteRule { Kind = kind, Value = value, Action = action });
            }

            if (!quiet)
            {
                Changed();
            }
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выбери программу" };

            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                // Имя файла, а не путь: правило переживёт обновление программы в другую папку.
                AddRule(RuleKind.App, Path.GetFileName(dlg.FileName), Action);
            }
        }

        private void OnRunning(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = new ContextMenu { PlacementTarget = RunningButton, Placement = PlacementMode.Bottom };

            var procs = Process.GetProcesses()
                .Select(p =>
                {
                    try
                    {
                        return (name: p.ProcessName, title: p.MainWindowHandle != IntPtr.Zero ? p.MainWindowTitle : "");
                    }
                    catch
                    {
                        return (name: "", title: "");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                })
                .Where(p => p.name.Length > 0 && p.title.Length > 0)
                .GroupBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var p in procs)
            {
                string exe = p.name + ".exe";

                if (exe.Equals("VPN.exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                MenuItem i = new MenuItem { Header = exe + (p.title.Length > 0 ? "   —   " + (p.title.Length > 40 ? p.title.Substring(0, 40) + "…" : p.title) : "") };
                i.Click += (_, _) => AddRule(RuleKind.App, exe, Action);
                menu.Items.Add(i);
            }

            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "Нет программ с окнами", IsEnabled = false });
            }

            menu.IsOpen = true;
        }

        private void OnCycle(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RuleVm vm)
            {
                vm.Rule.Action = vm.Rule.Action switch
                {
                    RouteAction.Vpn => RouteAction.Direct,
                    RouteAction.Direct => RouteAction.Block,
                    _ => RouteAction.Vpn
                };
                vm.Changed();
                apply.Stop();
                apply.Start();
            }
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is RuleVm vm)
            {
                T.Rules.Remove(vm.Rule);
                Changed();
            }
        }
    }
}
