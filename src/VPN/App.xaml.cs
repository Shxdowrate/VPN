using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using VPN.Core;

namespace VPN
{
    public partial class App : Application
    {
        private Mutex single;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            single = new Mutex(true, "Global\\ShxdowrateVPN.Single", out bool first);

            if (!first)
            {
                MessageBox.Show("VPN уже запущен — он в трее рядом с часами.", "VPN", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnUiError;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("Критическая ошибка: " + args.ExceptionObject);

            Log.TrimFile();
            Log.Write("Запуск VPN");
            CoreRunner.KillLeftovers();

            bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            var window = new MainWindow(minimized);
            MainWindow = window;

            if (!minimized)
            {
                window.Show();
            }
        }

        private void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("Ошибка: " + e.Exception);
            MessageBox.Show("Что-то пошло не так:\n" + e.Exception.Message + "\n\nПодробности — в журнале (меню слева).", "VPN",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            single?.Dispose();
            base.OnExit(e);
        }
    }
}
