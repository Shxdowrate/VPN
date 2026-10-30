using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NeonVpn.Core;

namespace NeonVpn.Views
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };

        public ServersView Servers => ServersPage;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = App.Vm;

            Settings s = App.Vpn.Settings;
            // Не больше рабочей области экрана (маленький монитор, крупный масштаб).
            Rect work = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, work.Width);
            MinHeight = Math.Min(MinHeight, work.Height);
            Width = Math.Min(work.Width, Math.Max(MinWidth, s.MainWidth));
            Height = Math.Min(work.Height, Math.Max(MinHeight, s.MainHeight));
            WindowKit.Place(this, s.MainLeft, s.MainTop);
            WindowKit.EnableDrag(this, Root);
            ApplyLook();

            toastTimer.Tick += (_, _) =>
            {
                toastTimer.Stop();
                ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
            };

            Home.GoToServers += () => NavServers.IsChecked = true;
        }

        public void ShowAndActivate() => WindowKit.ShowAndActivate(this);

        /// <summary>Открыть вкладку по имени (home, servers, tunnels, settings) — для запуска с --page=…</summary>
        public void GoTo(string page)
        {
            switch (page)
            {
                case "servers": NavServers.IsChecked = true; break;
                case "tunnels": NavTunnels.IsChecked = true; break;
                case "settings": NavSettings.IsChecked = true; break;
                default: NavHome.IsChecked = true; break;
            }
        }

        /// <summary>Оформление меняется только через ресурсы темы — здесь нечего перекрашивать.</summary>
        public void ApplyLook()
        {
        }

        public void OnStateChanged(VpnState before, VpnState now)
        {
            Home.OnStateChanged();
        }

        public void Toast(string text, bool error)
        {
            ToastText.Text = text;
            ToastIcon.Foreground = new SolidColorBrush(error ? Palette.Bad : Palette.Dim);
            ToastIcon.Text = error ? "\uE7BA" : "\uE946";
            ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            toastTimer.Stop();
            toastTimer.Interval = TimeSpan.FromSeconds(error ? 7 : 4.5);
            toastTimer.Start();
        }

        private void OnNav(object sender, RoutedEventArgs e)
        {
            if (Home == null)
            {
                return;
            }

            Home.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            ServersPage.Visibility = NavServers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Tunnels.Visibility = NavTunnels.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (NavServers.IsChecked == true) ServersPage.Reload();
            if (NavTunnels.IsChecked == true) Tunnels.Reload();
            if (NavSettings.IsChecked == true) SettingsPage.Reload();

            FrameworkElement shown = NavHome.IsChecked == true ? Home : NavServers.IsChecked == true ? ServersPage : NavTunnels.IsChecked == true ? Tunnels : SettingsPage;
            shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(150)));
        }

        private void OnMini(object sender, RoutedEventArgs e) => App.ShowMini();

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e)
        {
            SavePlacement();
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Alt+F4 и «закрыть» из панели задач — прячем в трей, выходим только из меню трея.
            e.Cancel = true;
            SavePlacement();
            Hide();
        }

        public void SavePlacement()
        {
            if (WindowState != WindowState.Normal || !IsLoaded)
            {
                return;
            }

            Settings s = App.Vpn.Settings;
            s.MainLeft = Left;
            s.MainTop = Top;
            s.MainWidth = ActualWidth;
            s.MainHeight = ActualHeight;
            App.Vpn.Save();
        }
    }
}
