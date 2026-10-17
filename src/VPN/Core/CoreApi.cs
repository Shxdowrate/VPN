using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VPN.Core
{
    /// <summary>
    /// Связь с работающим движком через его Clash API: скорость, пинг серверов, переключение сервера на лету.
    /// </summary>
    public sealed class CoreApi : IDisposable
    {
        private readonly HttpClient http;

        public CoreApi(string secret)
        {
            http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri("http://" + ConfigBuilder.ApiAddress),
                Timeout = Timeout.InfiniteTimeSpan
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        /// <summary>Поток скорости: движок раз в секунду присылает {"up":..,"down":..} (байт/с).</summary>
        public async Task StreamTrafficAsync(Action<long, long> onSample, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, "/traffic");
                    using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    using Stream stream = await response.Content.ReadAsStreamAsync(token);
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    while (!token.IsCancellationRequested)
                    {
                        string line = await reader.ReadLineAsync(token);

                        if (line == null)
                        {
                            break;
                        }

                        if (line.Length == 0)
                        {
                            continue;
                        }

                        JsonNode node = JsonNode.Parse(line);
                        onSample(node?["up"]?.GetValue<long>() ?? 0, node?["down"]?.GetValue<long>() ?? 0);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // движок ещё поднимается — пробуем снова
                }

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

        /// <summary>Пинг через сервер (настоящий: запрос идёт через VPN). null — не ответил.</summary>
        public async Task<int?> DelayAsync(string tag, int timeoutMs = 4000)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs + 1500);
                string url = "/proxies/" + Uri.EscapeDataString(tag) + "/delay?timeout=" + timeoutMs + "&url=" + Uri.EscapeDataString(ConfigBuilder.TestUrl);
                using HttpResponseMessage response = await http.GetAsync(url, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                JsonNode node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                int delay = node?["delay"]?.GetValue<int>() ?? 0;
                return delay > 0 ? delay : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Переключить сервер без перезапуска.</summary>
        public async Task<bool> SelectAsync(string tag)
        {
            try
            {
                var body = new StringContent(new JsonObject { ["name"] = tag }.ToJsonString(), Encoding.UTF8, "application/json");
                using var cts = new CancellationTokenSource(3000);
                using HttpResponseMessage response = await http.PutAsync("/proxies/proxy", body, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Какой сервер сейчас реально используется (для режима «Авто»).</summary>
        public async Task<string> CurrentAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(3000);
                string json = await http.GetStringAsync("/proxies/proxy", cts.Token);
                string now = JsonNode.Parse(json)?["now"]?.GetValue<string>() ?? "";

                if (now == "auto")
                {
                    json = await http.GetStringAsync("/proxies/auto", cts.Token);
                    now = JsonNode.Parse(json)?["now"]?.GetValue<string>() ?? "";
                }

                return now;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Всего скачано/отправлено за сессию.</summary>
        public async Task<(long up, long down)> TotalsAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(3000);
                string json = await http.GetStringAsync("/connections", cts.Token);
                JsonNode node = JsonNode.Parse(json);
                return (node?["uploadTotal"]?.GetValue<long>() ?? 0, node?["downloadTotal"]?.GetValue<long>() ?? 0);
            }
            catch
            {
                return (0, 0);
            }
        }

        public void Dispose() => http.Dispose();

        /// <summary>Пинг до сервера напрямую (TCP-рукопожатие), пока VPN выключен.</summary>
        public static async Task<int?> TcpPingAsync(string host, int port, int timeoutMs = 2500)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(timeoutMs);
                var sw = Stopwatch.StartNew();
                await client.ConnectAsync(host, port, cts.Token);
                return (int)Math.Max(1, sw.ElapsedMilliseconds);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Автозапуск с правами администратора — через планировщик заданий (обычный автозапуск не умеет в UAC).</summary>
    public static class Autostart
    {
        private const string TaskName = "ShxdowrateVPN";

        public static void Set(bool enabled)
        {
            string exe = Environment.ProcessPath ?? "";
            string args = enabled
                ? "/Create /F /TN \"" + TaskName + "\" /SC ONLOGON /RL HIGHEST /TR \"\\\"" + exe + "\\\" --minimized\""
                : "/Delete /F /TN \"" + TaskName + "\"";

            try
            {
                var info = new ProcessStartInfo("schtasks.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using Process p = Process.Start(info);
                p.WaitForExit(5000);
                Log.Write(enabled ? "Автозапуск включён" : "Автозапуск выключен");
            }
            catch (Exception e)
            {
                Log.Write("Автозапуск: " + e.Message);
            }
        }
    }

    public static class Format
    {
        public static string Speed(long bytesPerSecond)
        {
            double bits = bytesPerSecond * 8.0;

            if (bits < 1000)
            {
                return "0 Кбит/с";
            }

            if (bits < 1_000_000)
            {
                return (bits / 1000).ToString("0") + " Кбит/с";
            }

            return (bits / 1_000_000).ToString(bits < 10_000_000 ? "0.0" : "0") + " Мбит/с";
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " Б";
            }

            string[] units = { "КБ", "МБ", "ГБ", "ТБ" };
            double v = bytes;
            int i = -1;

            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024;
                i++;
            }

            return v.ToString(v < 10 ? "0.0" : "0") + " " + units[i];
        }

        public static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? ((int)t.TotalHours).ToString("0") + ":" + t.ToString(@"mm\:ss") : t.ToString(@"mm\:ss");
    }
}
