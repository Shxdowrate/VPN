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

        }

        public void ShowAndActivate() => WindowKit.ShowAndActivate(this);

        public void ApplyLook()
        {
        }

        public void OnStateChanged(VpnState before, VpnState now)
        {
        }

        private async void OnPower(object sender, RoutedEventArgs e) => await App.Vpn.ToggleAsync();

        private void OnExpand(object sender, RoutedEventArgs e) => App.ShowMain();

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
