using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NeonVpn.Core
{
    /// <summary>Clash API sing-box: пинг серверов, переключение, соединения со счётчиками.</summary>
    public sealed class ClashApi : IDisposable
    {
        private readonly HttpClient http;

        public ClashApi(int port, string secret)
        {
            http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + port + "/"),
                Timeout = TimeSpan.FromSeconds(20)
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        public async Task<bool> AliveAsync(CancellationToken token = default)
        {
            try
            {
                using HttpResponseMessage r = await http.GetAsync("version", token);
                return r.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Задержка через сервер (мс): HTTP-запрос к testUrl по новому соединению. null — нет ответа.</summary>
        public async Task<int?> DelayAsync(string tag, string testUrl, int timeoutMs = 5000, CancellationToken token = default)
        {
            try
            {
                string url = $"proxies/{Uri.EscapeDataString(tag)}/delay?timeout={timeoutMs}&url={Uri.EscapeDataString(testUrl)}";
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(timeoutMs + 3000);
                using HttpResponseMessage r = await http.GetAsync(url, cts.Token);

                if (!r.IsSuccessStatusCode)
                {
                    return null;
                }

                using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(cts.Token));
                return doc.RootElement.TryGetProperty("delay", out JsonElement d) && d.GetInt32() > 0 ? d.GetInt32() : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> SelectAsync(string tag)
        {
            try
            {
                using StringContent body = new StringContent(JsonSerializer.Serialize(new { name = tag }), Encoding.UTF8, "application/json");
                using HttpResponseMessage r = await http.PutAsync("proxies/" + ConfigBuilder.Proxy, body);
                return r.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> CurrentAsync()
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync("proxies/" + ConfigBuilder.Proxy));
                return doc.RootElement.TryGetProperty("now", out JsonElement now) ? now.GetString() : null;
            }
            catch
            {
                return null;
            }
        }

        public sealed class Connection
        {
            public string Id;
            public long Up, Down;
            public bool Direct;
            public string Host, Process;
        }

        public sealed class Snapshot
        {
            public long UploadTotal, DownloadTotal;
            public List<Connection> Connections = new List<Connection>();
        }

        public async Task<Snapshot> ConnectionsAsync(CancellationToken token = default)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync("connections", token));
                JsonElement root = doc.RootElement;
                Snapshot s = new Snapshot
                {
                    UploadTotal = root.TryGetProperty("uploadTotal", out JsonElement ut) ? ut.GetInt64() : 0,
                    DownloadTotal = root.TryGetProperty("downloadTotal", out JsonElement dt) ? dt.GetInt64() : 0
                };

                if (root.TryGetProperty("connections", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement c in list.EnumerateArray())
                    {
                        bool direct = false;

                        if (c.TryGetProperty("chains", out JsonElement chains) && chains.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement ch in chains.EnumerateArray())
                            {
                                if (ch.GetString() == ConfigBuilder.Direct)
                                {
                                    direct = true;
                                }
                            }
                        }

                        JsonElement meta = c.TryGetProperty("metadata", out JsonElement m) ? m : default;

                        s.Connections.Add(new Connection
                        {
                            Id = c.GetProperty("id").GetString(),
                            Up = c.TryGetProperty("upload", out JsonElement u) ? u.GetInt64() : 0,
                            Down = c.TryGetProperty("download", out JsonElement d) ? d.GetInt64() : 0,
                            Direct = direct,
                            Host = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("host", out JsonElement h) ? h.GetString() : "",
                            Process = meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("processPath", out JsonElement p) ? p.GetString() : ""
                        });
                    }
                }

                return s;
            }
            catch
            {
                return null;
            }
        }

        public async Task CloseAllAsync()
        {
            try
            {
                using HttpResponseMessage r = await http.DeleteAsync("connections");
            }
            catch
            {
                // не страшно
            }
        }

        public void Dispose() => http.Dispose();
    }
}
