using System.ComponentModel;
using System.Windows;
using NeonVpn.Core;

namespace NeonVpn.Views
{
    /// <summary>Мини-окно: кнопка, сервер, скорость/пинг/потери отдельно для VPN и прямого трафика.</summary>
    public partial class MiniWindow : Window
    {
        public MiniWindow()
        {
            InitializeComponent();
            DataContext = App.Vm;
            Settings s = App.Vpn.Settings;
            WindowKit.Place(this, s.MiniLeft, s.MiniTop);
            WindowKit.EnableDrag(this, Root);

            // Двойной клик по корпусу — большое окно.
            Root.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2 && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
                {
                    App.ShowMain();
                }
            };

            ApplyLook();
        }

        public void ShowAndActivate() => WindowKit.ShowAndActivate(this);

        public void ApplyLook()
        {
            WindowKit.Style(Frame, App.Vpn.Settings, App.Vm.StateColor, App.Vpn.State == VpnState.On);
            Frame.StartRunning(App.Vpn.Settings.Animations);
        }

        public void OnStateChanged(VpnState before, VpnState now)
        {
            ApplyLook();

            if (now == VpnState.On && before != VpnState.On)
            {
                Frame.Pulse();
            }
        }

        private async void OnPower(object sender, RoutedEventArgs e) => await App.Vpn.ToggleAsync();

        private void OnExpand(object sender, RoutedEventArgs e) => App.ShowMain();

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnClose(object sender, RoutedEventArgs e)
        {
            SavePlacement();
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            SavePlacement();
            Hide();
        }

        public void SavePlacement()
        {
            if (!IsLoaded || WindowState != WindowState.Normal)
            {
                return;
            }

            App.Vpn.Settings.MiniLeft = Left;
            App.Vpn.Settings.MiniTop = Top;
            App.Vpn.Save();
        }
    }
}
