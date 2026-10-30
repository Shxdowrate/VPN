using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using NeonVpn.Core;
using NeonVpn.Services;

namespace NeonVpn.Views
{
    public partial class SettingsView : UserControl
    {
        private bool loading;

        private static Settings S => App.Vpn.Settings;

        public SettingsView()
        {
            InitializeComponent();

            foreach (int m in new[] { 2, 5, 10, 30 })
            {
                RadioButton r = new RadioButton { Content = m + " МИН", Tag = m, GroupName = "interval", Style = (Style)FindResource("RadioChip") };
                r.Click += (_, _) => { S.AutoCheckMinutes = m; Save(false); };
                Intervals.Children.Add(r);
            }

            foreach (int ms in new[] { 30, 60, 120, 250 })
            {
                RadioButton r = new RadioButton { Content = ms + " МС", Tag = ms, GroupName = "tol", Style = (Style)FindResource("RadioChip") };
                r.Click += (_, _) => { S.AutoTolerance = ms; Save(false); };
                Tolerances.Children.Add(r);
            }

            foreach (var (name, a, b) in Theme.Presets)
            {
                ColorPanel.Children.Add(Swatch(name, a, b));
            }

            VersionText.Text = "Neon VPN " + typeof(App).Assembly.GetName().Version?.ToString(3) + " · данные: " + Paths.Data;
        }

        /// <summary>Образец цвета: два светящихся кружка и имя.</summary>
        private ToggleButton Swatch(string name, string a, string b)
        {
            Color ca = Palette.Parse(a, Colors.Lime);
            Color cb = Palette.Parse(b, Colors.Cyan);
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Dot(ca));
            content.Children.Add(Dot(cb));
            content.Children.Add(new TextBlock { Text = name.ToUpperInvariant(), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

            ToggleButton t = new ToggleButton { Content = content, Tag = a + "|" + b, Style = (Style)FindResource("Chip") };
            t.Click += (_, _) =>
            {
                S.Accent = a;
                S.Accent2 = b;
                ApplyLook();
            };
            return t;
        }

        private static Ellipse Dot(Color c) => new Ellipse
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(0, 0, 3, 0),
            Fill = new SolidColorBrush(c),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 }
        };

        public void Reload()
        {
            loading = true;
            CoreSing.IsChecked = S.Core == CoreKind.SingBox;
            CoreXray.IsChecked = S.Core == CoreKind.Xray;
            Fragment.IsChecked = S.XrayFragment;
            Tun.IsChecked = S.UseTun;
            SysProxy.IsChecked = S.UseSystemProxy;
            Port.Text = S.ProxyPort.ToString();
            Lan.IsChecked = S.AllowLan;
            TestUrl.Text = S.TestUrl;
            RemoteDns.Text = S.RemoteDns;
            LocalDns.Text = S.LocalDns;
            AutostartBox.IsChecked = S.Autostart;
            AutoConnect.IsChecked = S.ConnectOnLaunch;
            StartHidden.IsChecked = S.StartMinimized;
            Anim.IsChecked = S.Animations;

            foreach (RadioButton r in Intervals.Children.OfType<RadioButton>()) r.IsChecked = (int)r.Tag == S.AutoCheckMinutes;
            foreach (RadioButton r in Tolerances.Children.OfType<RadioButton>()) r.IsChecked = (int)r.Tag == S.AutoTolerance;
            foreach (ToggleButton t in ColorPanel.Children.OfType<ToggleButton>()) t.IsChecked = (string)t.Tag == S.Accent + "|" + S.Accent2;

            ShapeBlade.IsChecked = S.FrameShape == "Blade";
            ShapeShard.IsChecked = S.FrameShape == "Shard";
            ShapeCapsule.IsChecked = S.FrameShape == "Capsule";

            foreach (RadioButton r in new[] { Glow1, Glow2, Glow3, Glow4 })
            {
                r.IsChecked = Math.Abs(double.Parse((string)r.Tag, CultureInfo.InvariantCulture) - S.Glow) < 0.05;
            }

            bool hasXray = File.Exists(Paths.Xray);
            bool hasSing = File.Exists(Paths.SingBox);
            CoreHint.Text = S.Core == CoreKind.Xray
                ? "С серверами говорит Xray (лучше на мобильном интернете, есть xhttp). Hysteria2 и TUIC всё равно идут через sing-box."
                : "С серверами говорит sing-box. Серверы с xhttp сами уходят в Xray.";
            CoreFiles.Text = (hasSing ? "sing-box ✓" : "sing-box ✗ нет core\\sing-box.exe") + "   ·   " + (hasXray ? "Xray ✓" : "Xray ✗ нет core\\xray.exe");
            loading = false;
        }

        private void OnCore(object sender, RoutedEventArgs e)
        {
            CoreKind core = CoreXray.IsChecked == true ? CoreKind.Xray : CoreKind.SingBox;

            if (core == CoreKind.Xray && !File.Exists(Paths.Xray))
            {
                App.Notify("Нет core\\xray.exe рядом с программой.", true);
                Reload();
                return;
            }

            S.Core = core;
            Save(true);
            Reload();
        }

        private void OnChanged(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }

            bool reconnect = false;

            if (S.XrayFragment != (Fragment.IsChecked == true)) { S.XrayFragment = Fragment.IsChecked == true; reconnect = true; }

            bool tun = Tun.IsChecked == true, proxy = SysProxy.IsChecked == true;

            if (!tun && !proxy)
            {
                App.Notify("Нужен хотя бы один режим: TUN или системный прокси.", false);
                Tun.IsChecked = S.UseTun;
                SysProxy.IsChecked = S.UseSystemProxy;
            }
            else if (tun != S.UseTun || proxy != S.UseSystemProxy)
            {
                S.UseTun = tun;
                S.UseSystemProxy = proxy;
                reconnect = true;
            }

            if (int.TryParse(Port.Text.Trim(), out int port) && port is > 1024 and < 65535 && port != S.ProxyPort)
            {
                S.ProxyPort = port;
                reconnect = true;
            }

            if (S.AllowLan != (Lan.IsChecked == true)) { S.AllowLan = Lan.IsChecked == true; reconnect = true; }

            string url = TestUrl.Text.Trim();

            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                S.TestUrl = url;
            }
            else if (url.Length > 0)
            {
                TestUrl.Text = S.TestUrl;
                App.Notify("Адрес проверки должен начинаться с https:// — ядро не пингует по http.", true);
            }

            if (RemoteDns.Text.Trim().Length > 0 && RemoteDns.Text.Trim() != S.RemoteDns) { S.RemoteDns = RemoteDns.Text.Trim(); reconnect = true; }
            if (LocalDns.Text.Trim().Length > 0 && LocalDns.Text.Trim() != S.LocalDns) { S.LocalDns = LocalDns.Text.Trim(); reconnect = true; }

            if (S.Autostart != (AutostartBox.IsChecked == true))
            {
                S.Autostart = AutostartBox.IsChecked == true;
                Autostart.Set(S.Autostart);
            }

            S.ConnectOnLaunch = AutoConnect.IsChecked == true;
            S.StartMinimized = StartHidden.IsChecked == true;
            Save(reconnect);
        }

        private void OnLook(object sender, RoutedEventArgs e)
        {
            if (loading)
            {
                return;
            }

            if (sender is RadioButton r && r.GroupName == "shape") S.FrameShape = (string)r.Tag;
            if (sender is RadioButton g && g.GroupName == "glow") S.Glow = double.Parse((string)g.Tag, CultureInfo.InvariantCulture);
            S.Animations = Anim.IsChecked == true;
            ApplyLook();
        }

        private void ApplyLook()
        {
            Theme.Apply(S);
            App.Vm.Refresh();
            App.MainWin?.ApplyLook();
            App.Mini?.ApplyLook();
            App.Tray?.Update();
            App.Vpn.Save();
            Reload();
        }

        private async void Save(bool reconnect)
        {
            App.Vpn.Save();
            App.Vm.Refresh();
            App.MainWin?.OnStateChanged(App.Vpn.State, App.Vpn.State);

            if (reconnect && App.Vpn.IsOn)
            {
                await App.Vpn.ReconnectAsync();
                App.Notify("Настройки применены.", false);
            }
        }

        private void OnLog(object sender, RoutedEventArgs e) => Open(Paths.LogFile);

        private void OnData(object sender, RoutedEventArgs e) => Open(Paths.Data);

        private static void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Notify("Не открылось: " + ex.Message, true);
            }
        }

        private async void OnExit(object sender, RoutedEventArgs e) => await App.ExitAsync();
    }
}
