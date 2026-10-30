using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NeonVpn.Core;
using NeonVpn.Services;
using NeonVpn.Views;

namespace NeonVpn
{
    public partial class App : Application
    {
        public static VpnService Vpn { get; private set; }
        public static MainVm Vm { get; private set; }
        public static Tray Tray { get; private set; }
        public static MainWindow Main { get; private set; }
        public static MiniWindow Mini { get; private set; }

        private static Mutex single;
        private static EventWaitHandle showSignal;
        private DispatcherTimer statsTimer;
        private DispatcherTimer subsTimer;
        private bool exiting;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Одна копия: вторая просто показывает окно первой.
            single = new Mutex(true, "NeonVPN-single-instance", out bool first);
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "NeonVPN-show");

            if (!first)
            {
                showSignal.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, ex) =>
            {
                Log.Write("Ошибка окна: " + ex.Exception);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("Сбой: " + ex.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, ex) =>
            {
                Log.Write("Фоновая ошибка: " + ex.Exception?.GetBaseException().Message);
                ex.SetObserved();
            };

            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonVPN");
            Paths.Init(AppContext.BaseDirectory, data);
            Log.Trim();
            Log.Write("—— запуск " + typeof(App).Assembly.GetName().Version);

            Settings settings = File.Exists(Paths.Settings) ? Settings.Load(Paths.Settings) : Migration.FromOldVersion() ?? new Settings();
            CoreRunner.KillLeftovers();
            WindowsHooks.CleanupLeftover(settings.ProxyPort);
            Theme.Apply(settings);

            string exe = Path.GetFileName(Environment.ProcessPath ?? "VPN.exe");
            Vpn = new VpnService(settings, new WindowsHooks(), new[] { exe, "VPN.exe" }.Distinct().ToArray());
            Vpn.Save();
            Vm = new MainVm(Vpn);

            Vpn.StateChanged += () => Ui(OnStateChanged);
            Vpn.PingsUpdated += () => Ui(() => Main?.Servers.OnPings());
            Vpn.Notice += m => Ui(() => Notify(m, true));
            Vpn.AutoSwitched += (from, to, why) => Ui(() => Notify($"Авто: {from?.DisplayName} → {to?.DisplayName} ({why})", false));

            Tray = new Tray(Vpn);
            Tray.OpenMain += () => Ui(ShowMain);
            Tray.OpenMini += () => Ui(ShowMini);
            Tray.Exit += () => Ui(() => _ = ExitAsync());

            Main = new MainWindow();
            Mini = new MiniWindow();
            Hotkey.Register(Main, () => Ui(() => _ = Vpn.ToggleAsync()));

            // Сигнал от второй копии — показать окно.
            Task.Run(() =>
            {
                while (showSignal.WaitOne())
                {
                    Ui(ShowMain);
                }
            });

            bool autostart = e.Args.Contains("--autostart");

            if (!(autostart && settings.StartMinimized))
            {
                if (settings.StartInMini) ShowMini();
                else ShowMain();
            }

            statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Vm.RefreshStats(), Dispatcher);
            statsTimer.Start();

            subsTimer = new DispatcherTimer(TimeSpan.FromMinutes(20), DispatcherPriority.Background, async (_, _) => await UpdateSubs(), Dispatcher);
            subsTimer.Start();

            if (settings.ConnectOnLaunch && settings.AllActive().Any())
            {
                await Vpn.ConnectAsync();
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
            await UpdateSubs();
        }

        private static async Task UpdateSubs()
        {
            try
            {
                await Vpn.UpdateDueAsync();
                Main?.Servers.Reload();
                Vm.Refresh();
            }
            catch (Exception ex)
            {
                Log.Write("Обновление подписок: " + ex.Message);
            }
        }

        private void OnStateChanged()
        {
            VpnState before = lastState;
            lastState = Vpn.State;
            Vm.Refresh();
            Tray.Update();
            Main?.OnStateChanged(before, Vpn.State);
            Mini?.OnStateChanged(before, Vpn.State);
            Main?.Servers.OnCurrentChanged();

            bool windowsHidden = (Main == null || !Main.IsVisible) && (Mini == null || !Mini.IsVisible);

            if (windowsHidden && before != Vpn.State)
            {
                if (Vpn.State == VpnState.On && before != VpnState.Switching) Tray.Notify("Подключено", Vpn.Current?.DisplayName);
                else if (Vpn.State == VpnState.Off && before is VpnState.On) Tray.Notify("Отключено", "");
            }
        }

        private VpnState lastState = VpnState.Off;

        public static void Notify(string text, bool error)
        {
            if (Main != null && Main.IsVisible && Main.WindowState != WindowState.Minimized)
            {
                Main.Toast(text, error);
            }
            else
            {
                Tray?.Notify(error ? "Внимание" : "Neon VPN", text, error);
            }
        }

        public static void Ui(Action a)
        {
            Application app = Current;

            if (app == null)
            {
                return;
            }

            if (app.Dispatcher.CheckAccess()) a();
            else app.Dispatcher.BeginInvoke(a);
        }

        public static void ShowMain()
        {
            if (Mini != null && Mini.IsVisible)
            {
                Mini.SavePlacement();
                Mini.Hide();
            }

            Main.ShowAndActivate();
            Vpn.Settings.StartInMini = false;
        }

        public static void ShowMini()
        {
            if (Main != null && Main.IsVisible)
            {
                Main.SavePlacement();
                Main.Hide();
            }

            Mini.ShowAndActivate();
            Vpn.Settings.StartInMini = true;
        }

        public static async Task ExitAsync()
        {
            App app = (App)Current;

            if (app.exiting)
            {
                return;
            }

            app.exiting = true;
            Main?.SavePlacement();
            Mini?.SavePlacement();
            Vpn.Save();
            Tray?.Dispose();
            Main?.Hide();
            Mini?.Hide();

            try
            {
                await Task.WhenAny(Vpn.ShutdownAsync(), Task.Delay(6000));
            }
            catch (Exception ex)
            {
                Log.Write("Выход: " + ex.Message);
            }

            Log.Write("—— выход");
            app.Shutdown();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            // Выход из Windows — убираем системный прокси, иначе он останется до следующего входа.
            Vpn?.ShutdownAsync().Wait(4000);
            Vpn?.Save();
            base.OnSessionEnding(e);
        }
    }
}
