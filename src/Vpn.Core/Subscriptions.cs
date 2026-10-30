using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NeonVpn.Core
{
    /// <summary>Загрузка и обновление подписок. Выбор пользователя (какие серверы выключены) переживает обновления.</summary>
    public static class Subscriptions
    {
        public sealed class Fetched
        {
            public List<ServerNode> Servers = new List<ServerNode>();
            public string Title = "";
            public long Used, Total, Expire;
            public int Skipped;
            public int UpdateHours;

            /// <summary>Настройки, которые провайдер передаёт клиенту (заголовки и строки «#ключ: значение»): фрагментация, шум, DNS…</summary>
            public Dictionary<string, string> Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string ClientName = "";
        }

        /// <summary>Как представляться панели. Многие провайдеры отдают рабочие настройки только «своему» клиенту.</summary>
        public static readonly (string name, string userAgent)[] Clients =
        {
            ("Happ", "Happ/3.3.6"),
            ("v2rayN", "v2rayN/7.10"),
            ("sing-box", "sing-box/1.14.3")
        };

        /// <summary>Заголовки, которые понимаем и храним (см. документацию Happ «Управление приложением»).</summary>
        private static readonly string[] KnownHints =
        {
            "fragmentation-enable", "fragmentation-packets", "fragmentation-length", "fragmentation-interval",
            "noises-enable", "noises-packet-type", "noises-packet", "noises-delay", "noises-rand", "noises-rand-range",
            "mux-enable", "mux-tcp-connections", "mux-xudp-connections",
            "server-address-resolve-enable", "server-address-resolve-dns-domain", "server-address-resolve-dns-ip",
            "ping-type", "check-url-via-proxy", "routing-enable", "new-url", "new-domain", "fallback-url", "announce"
        };

        /// <param name="client">Имя из Clients; пусто — по очереди: Happ, затем v2rayN, если ответ не разобрался.</param>
        /// <param name="proxy">Адрес локального прокси, если подписку надо качать через VPN (когда напрямую не открывается).</param>
        public static async Task<Fetched> FetchAsync(string url, string proxy = null, CancellationToken token = default, string client = "")
        {
            List<(string name, string userAgent)> order = Clients.Where(c => client.Length == 0 || c.name == client).ToList();
            Exception last = null;

            foreach (var c in order.Count > 0 ? order : Clients.ToList())
            {
                try
                {
                    Fetched f = await FetchAs(url, proxy, c.userAgent, token);
                    f.ClientName = c.name;
                    return f;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    last = e;
                    Log.Write($"Подписка как {c.name}: {e.Message}");
                }
            }

            throw last ?? new Exception("подписка не загрузилась");
        }

        private static async Task<Fetched> FetchAs(string url, string proxy, string userAgent, CancellationToken token)
        {
            HttpClientHandler handler = new HttpClientHandler { UseProxy = proxy != null, AutomaticDecompression = System.Net.DecompressionMethods.All };

            if (proxy != null)
            {
                handler.Proxy = new System.Net.WebProxy(proxy);
            }

            using HttpClient http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            // Как Happ: идентификатор устройства и система. Панели с лимитом устройств (Remnawave HWID) без x-hwid отвечают 404.
            request.Headers.TryAddWithoutValidation("x-hwid", Device.Hwid);
            request.Headers.TryAddWithoutValidation("x-device-os", Device.Os);
            request.Headers.TryAddWithoutValidation("x-ver-os", Device.OsVersion);
            request.Headers.TryAddWithoutValidation("x-device-model", Device.Model);
            request.Headers.TryAddWithoutValidation("Accept-Language", "ru");

            using HttpResponseMessage response = await http.SendAsync(request, token);
            string body = await response.Content.ReadAsStringAsync(token);
            Dump(url, userAgent, response, body);
            response.EnsureSuccessStatusCode();

            Fetched result = new Fetched();
            ReadHeaders(response, result);
            ReadBodyHints(body, result);
            result.Servers = LinkParser.ParseMany(body, out result.Skipped);

            if (result.Servers.Count == 0)
            {
                throw new Exception(body.Contains("happ://crypt")
                    ? "подписка зашифрована под Happ"
                    : "в подписке нет серверов, которые я понимаю");
            }

            return result;
        }

        /// <summary>Последний ответ панели — в файл, чтобы было видно, что именно она прислала.</summary>
        private static void Dump(string url, string userAgent, HttpResponseMessage response, string body)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Paths.Run);
                string name = "sub-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)), 0, 4).ToLowerInvariant()
                              + "-" + userAgent.Split('/')[0] + ".txt";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("HTTP " + (int)response.StatusCode + "  UA: " + userAgent + "  " + DateTime.Now);

                foreach (var h in response.Headers.Concat(response.Content.Headers))
                {
                    sb.AppendLine(h.Key + ": " + string.Join(", ", h.Value));
                }

                sb.AppendLine();
                sb.Append(body.Length > 200_000 ? body.Substring(0, 200_000) : body);
                System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Run, name), sb.ToString());
            }
            catch
            {
                // не страшно
            }
        }

        private static void ReadBodyHints(string body, Fetched result)
        {
            string text = body.Contains("://") ? body : LinkParser.TryBase64(body.Trim()) ?? body;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();

                if (!line.StartsWith("#") || line.Contains("://"))
                {
                    continue;
                }

                int colon = line.IndexOf(':');

                if (colon > 1)
                {
                    string key = line.Substring(1, colon - 1).Trim();

                    if (KnownHints.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Hints[key] = line.Substring(colon + 1).Trim();
                    }
                }
            }
        }

        private static void ReadHeaders(HttpResponseMessage response, Fetched result)
        {
            string H(string name) => response.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() ?? "" :
                response.Content.Headers.TryGetValues(name, out var c) ? c.FirstOrDefault() ?? "" : "";

            string title = H("profile-title");

            if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                title = LinkParser.TryBase64(title.Substring(7)) ?? title;
            }

            result.Title = title.Trim();

            if (int.TryParse(H("profile-update-interval"), out int hours))
            {
                result.UpdateHours = hours;
            }

            // upload=123; download=456; total=789; expire=1700000000
            long up = 0, down = 0;

            foreach (string part in H("subscription-userinfo").Split(';'))
            {
                string[] kv = part.Trim().Split('=');

                if (kv.Length != 2 || !long.TryParse(kv[1].Trim(), out long value))
                {
                    continue;
                }

                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "upload": up = value; break;
                    case "download": down = value; break;
                    case "total": result.Total = value; break;
                    case "expire": result.Expire = value; break;
                }
            }

            result.Used = up + down;

            foreach (string key in KnownHints)
            {
                string v = H(key);

                if (v.Length > 0)
                {
                    result.Hints[key] = v;
                }
            }
        }

        /// <summary>
        /// Кладёт свежие серверы в подписку. Ключи стабильны, поэтому выключенные остаются выключенными,
        /// а группы продолжают ссылаться на те же серверы. Новые серверы выключаются, если так настроено.
        /// </summary>
        public static (int added, int removed) Apply(SubscriptionInfo sub, Fetched fetched)
        {
            HashSet<string> before = new HashSet<string>(sub.Servers.Select(s => s.Key));
            List<ServerNode> fresh = new List<ServerNode>();
            HashSet<string> seen = new HashSet<string>();

            foreach (ServerNode node in fetched.Servers)
            {
                node.SourceId = sub.Id;
                node.ComputeKey();

                // Одинаковые серверы под разными именами — различаем по порядку.
                string key = node.Key;
                int n = 2;

                while (!seen.Add(node.Key))
                {
                    node.Key = key + "-" + n++;
                }

                fresh.Add(node);
            }

            int added = fresh.Count(s => !before.Contains(s.Key));
            int removed = before.Count(k => fresh.All(s => s.Key != k));

            if (sub.NewServersOff && before.Count > 0)
            {
                foreach (ServerNode s in fresh.Where(s => !before.Contains(s.Key)))
                {
                    sub.Excluded.Add(s.Key);
                }
            }

            // Ключи исчезнувших серверов не храним — иначе список выключенных растёт вечно.
            sub.Excluded.IntersectWith(fresh.Select(s => s.Key));

            sub.Servers = fresh;
            sub.Updated = DateTime.Now;
            sub.Used = fetched.Used;
            sub.Total = fetched.Total;
            sub.Expire = fetched.Expire;
            sub.Error = "";
            sub.Hints = fetched.Hints;
            sub.Client = fetched.ClientName;

            if (string.IsNullOrWhiteSpace(sub.Name))
            {
                sub.Name = fetched.Title.Length > 0 ? fetched.Title : HostOf(sub.Url);
            }

            if (fetched.UpdateHours > 0 && sub.UpdateHours == 12)
            {
                sub.UpdateHours = fetched.UpdateHours;
            }

            return (added, removed);
        }

        /// <summary>Добавляет серверы из вставленных ссылок в «Мои серверы».</summary>
        public static int AddManual(Settings settings, string text)
        {
            SubscriptionInfo manual = settings.Manual();
            int count = 0;

            foreach (ServerNode node in LinkParser.ParseMany(text, out _))
            {
                node.SourceId = manual.Id;
                node.ComputeKey();

                if (manual.Servers.All(s => s.Key != node.Key))
                {
                    manual.Servers.Add(node);
                    count++;
                }
            }

            return count;
        }

        /// <summary>Убирает из групп серверы, которых больше нет ни в одной подписке.</summary>
        public static void PruneGroups(Settings settings)
        {
            HashSet<string> all = new HashSet<string>(settings.Subscriptions.SelectMany(s => s.Servers).Select(s => s.Key));

            foreach (ServerGroup g in settings.Groups)
            {
                g.Members.RemoveAll(k => !all.Contains(k));
            }
        }

        public static string HostOf(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                return "Подписка";
            }
        }
    }
}
