using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using VPN.Core;

namespace VPN
{
    public partial class App : Application
    {
        private const string PipeName = "ShxdowrateVPN.Pipe";

        private Mutex single;
        private CancellationTokenSource pipeCts;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Ссылка, с которой нас открыли (happ://…, shxvpn://…, vless://…), — первый аргумент, не начинающийся с «--».
            string link = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a.Contains("://"));

            single = new Mutex(true, "Global\\ShxdowrateVPN.Single", out bool first);

            if (!first)
            {
                // Программа уже запущена: передаём ей ссылку (или просьбу показать окно) и выходим.
                if (!SendToRunning(link ?? "show"))
                {
                    MessageBox.Show("VPN уже запущен — он в трее рядом с часами.", "VPN", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnUiError;
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("Критическая ошибка: " + args.ExceptionObject);

            Log.TrimFile();
            Log.Write("Запуск VPN");
            CoreRunner.KillLeftovers();

            // По ссылке открываем окно, даже если в настройках «запускать свёрнутым».
            bool minimized = link == null && e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            var window = new MainWindow(minimized, link);
            MainWindow = window;

            if (!minimized)
            {
                window.Show();
            }

            pipeCts = new CancellationTokenSource();
            _ = Task.Run(() => ListenAsync(window, pipeCts.Token));
        }

        /// <summary>Отправить строку уже запущенной программе. false — не получилось.</summary>
        private static bool SendToRunning(string message)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(message.Replace("\r", "").Replace("\n", " "));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Ждём сообщений от повторных запусков: «показать окно» или ссылку для импорта.</summary>
        private async Task ListenAsync(MainWindow window, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    string line = await reader.ReadLineAsync();

                    if (!string.IsNullOrWhiteSpace(line) && line.Length < 100_000)
                    {
                        await Dispatcher.BeginInvoke(new Action(() => window.OnExternalMessage(line.Trim())));
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Log.Write("Канал связи: " + e.Message);

                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("Ошибка: " + e.Exception);
            MessageBox.Show("Что-то пошло не так:\n" + e.Exception.Message + "\n\nПодробности — в журнале («Настройки» → «Журнал»).", "VPN",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            pipeCts?.Cancel();
            single?.Dispose();
            base.OnExit(e);
        }
    }
}
