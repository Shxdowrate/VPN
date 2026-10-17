using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VPN.Core
{
    /// <summary>Запуск и остановка движка sing-box.</summary>
    public sealed class CoreRunner
    {
        private Process process;
        private bool stopping;

        public string ApiSecret { get; private set; } = "";

        /// <summary>Движок завершился сам (ошибка, вылет). Аргумент — последние строки вывода.</summary>
        public event Action<string> Crashed;

        public bool Running => process != null && !process.HasExited;

        /// <summary>Убить «осиротевший» движок от прошлого запуска, если программа тогда вылетела.</summary>
        public static void KillLeftovers()
        {
            foreach (Process p in Process.GetProcessesByName("sing-box"))
            {
                try
                {
                    string path = p.MainModule?.FileName ?? "";

                    if (path.Equals(Paths.Core, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(true);
                        p.WaitForExit(3000);
                    }
                }
                catch
                {
                    // чужой процесс без доступа — не наш
                }
            }
        }

        /// <summary>Собрать конфиг, проверить его и запустить движок. Возвращает текст ошибки или null.</summary>
        public async Task<string> StartAsync(AppSettings settings)
        {
            await StopAsync();

            if (!File.Exists(Paths.Core))
            {
                return "Не найден движок: " + Paths.Core;
            }

            if (settings.Servers.Count == 0)
            {
                return "Нет серверов. Добавь подписку в меню слева.";
            }

            ApiSecret = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Paths.Data);
            await File.WriteAllTextAsync(Paths.Config, ConfigBuilder.Build(settings, ApiSecret));

            string check = await RunOnceAsync("check -c \"" + Paths.Config + "\"");

            if (check != null)
            {
                return "Ошибка конфигурации:\n" + check;
            }

            var tail = new StringBuilder();
            stopping = false;

            var info = new ProcessStartInfo(Paths.Core, "run -c \"" + Paths.Config + "\" -D \"" + Paths.Data + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Paths.Data
            };

            Process p = new Process { StartInfo = info, EnableRaisingEvents = true };

            void OnLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                line = StripAnsi(line);
                Log.Write("[core] " + line);

                lock (tail)
                {
                    tail.AppendLine(line);

                    if (tail.Length > 4000)
                    {
                        tail.Remove(0, tail.Length - 4000);
                    }
                }
            }

            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.Exited += (_, _) =>
            {
                if (!stopping)
                {
                    string text;

                    lock (tail)
                    {
                        text = tail.ToString();
                    }

                    Log.Write("Движок неожиданно остановился (код " + SafeExitCode(p) + ")");
                    Crashed?.Invoke(LastLines(text, 6));
                }
            };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            process = p;
            Log.Write("Движок запущен");

            // Дадим секунду: если упадёт сразу (занят порт, нет прав) — сообщим.
            await Task.Delay(1200);

            if (p.HasExited)
            {
                string text;

                lock (tail)
                {
                    text = tail.ToString();
                }

                process = null;
                return "Движок не запустился:\n" + LastLines(text, 6);
            }

            return null;
        }

        public async Task StopAsync()
        {
            Process p = process;
            process = null;

            if (p == null)
            {
                return;
            }

            stopping = true;

            try
            {
                if (!p.HasExited)
                {
                    p.Kill(true);
                    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception e)
            {
                Log.Write("Остановка движка: " + e.Message);
            }
            finally
            {
                p.Dispose();
                Log.Write("Движок остановлен");
            }
        }

        /// <summary>Разовый запуск (например, sing-box check). Возвращает текст ошибки или null.</summary>
        private static async Task<string> RunOnceAsync(string args)
        {
            var info = new ProcessStartInfo(Paths.Core, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using Process p = Process.Start(info);
            // Читаем оба потока одновременно, иначе процесс может застрять на переполненном буфере.
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            string output = await stdout + await stderr;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : LastLines(StripAnsi(output), 6);
        }

        private static string SafeExitCode(Process p)
        {
            try
            {
                return p.ExitCode.ToString();
            }
            catch
            {
                return "?";
            }
        }

        private static string LastLines(string text, int count) =>
            string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(count));

        private static string StripAnsi(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"\x1B\[[0-9;]*[A-Za-z]", "");
    }
}
