using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NeonVpn.Core;

namespace NeonVpn.Views
{
    public partial class HomeView : UserControl
    {
        public event Action GoToServers;

        public HomeView()
        {
            InitializeComponent();
            DataContext = App.Vm;
            Loaded += (_, _) => OnStateChanged();
        }

        public void OnStateChanged()
        {
            Settings s = App.Vpn.Settings;
            TunChip.IsChecked = s.UseTun;
            ProxyChip.IsChecked = s.UseSystemProxy;
            CoreSing.IsChecked = s.Core == CoreKind.SingBox;
            CoreXray.IsChecked = s.Core == CoreKind.Xray;
            TargetIcon.Text = s.Target.Auto ? "" : s.Target.PickedAsBest ? "" : "";
        }

        private async void OnPower(object sender, RoutedEventArgs e) => await App.Vpn.ToggleAsync();

        private async void OnBest(object sender, RoutedEventArgs e) => await App.Vpn.ConnectBestAsync(ScopeKind.All, "");

        private void OnServerCard(object sender, RoutedEventArgs e) => GoToServers?.Invoke();

        private void OnAuto(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = ScopeMenu.Build(App.Vpn.Settings, (scope, id) => _ = App.Vpn.SetAutoAsync(scope, id), "Авто среди:");
            menu.PlacementTarget = AutoButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private async void OnMode(object sender, RoutedEventArgs e)
        {
            Settings s = App.Vpn.Settings;
            bool tun = TunChip.IsChecked == true;
            bool proxy = ProxyChip.IsChecked == true;

            if (!tun && !proxy)
            {
                // Хотя бы один вход нужен — снимаем галочку обратно.
                ((ToggleButton)sender).IsChecked = true;
                App.Notify("Нужен хотя бы один режим: TUN или системный прокси.", false);
                return;
            }

            s.UseTun = tun;
            s.UseSystemProxy = proxy;
            App.Vpn.Save();
            App.Vm.Refresh();
            await App.Vpn.ReconnectAsync();
        }

        private async void OnCore(object sender, RoutedEventArgs e)
        {
            Settings s = App.Vpn.Settings;
            CoreKind core = CoreXray.IsChecked == true ? CoreKind.Xray : CoreKind.SingBox;

            if (core == s.Core)
            {
                return;
            }

            if (core == CoreKind.Xray && !System.IO.File.Exists(Paths.Xray))
            {
                App.Notify("Нет файла core\\xray.exe рядом с программой — положи его туда или скачай свежую сборку.", true);
                OnStateChanged();
                return;
            }

            s.Core = core;
            App.Vpn.Save();
            App.Vm.Refresh();
            await App.Vpn.ReconnectAsync();
        }
    }

    /// <summary>Меню выбора области: все серверы, подписки, группы.</summary>
    public static class ScopeMenu
    {
        public static ContextMenu Build(Settings s, Action<ScopeKind, string> pick, string title)
        {
            ContextMenu menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = title, IsEnabled = false });

            void Add(string header, ScopeKind kind, string id, int count)
            {
                MenuItem i = new MenuItem
                {
                    Header = header + "  ·  " + count,
                    IsChecked = s.Target.Auto && s.Target.Scope == kind && (kind == ScopeKind.All || s.Target.ScopeId == id),
                    IsEnabled = count > 0
                };
                i.Click += (_, _) => pick(kind, id);
                menu.Items.Add(i);
            }

            Add("Все серверы", ScopeKind.All, "", s.AllActive().Count());

            if (s.Subscriptions.Count > 0)
            {
                menu.Items.Add(new Separator());

                foreach (SubscriptionInfo sub in s.Subscriptions)
                {
                    Add("Подписка: " + sub.Name, ScopeKind.Subscription, sub.Id, sub.Active.Count());
                }
            }

            if (s.Groups.Count > 0)
            {
                menu.Items.Add(new Separator());

                foreach (ServerGroup g in s.Groups)
                {
                    Add("Группа: " + g.Name, ScopeKind.Group, g.Id, s.InScope(ScopeKind.Group, g.Id).Count);
                }
            }

            return menu;
        }
    }
}
