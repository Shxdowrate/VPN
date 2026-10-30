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
        }

        /// <param name="proxy">Адрес локального прокси, если подписку надо качать через VPN (когда напрямую не открывается).</param>
        public static async Task<Fetched> FetchAsync(string url, string proxy = null, CancellationToken token = default)
        {
            HttpClientHandler handler = new HttpClientHandler { UseProxy = proxy != null, AutomaticDecompression = System.Net.DecompressionMethods.All };

            if (proxy != null)
            {
                handler.Proxy = new System.Net.WebProxy(proxy);
            }

            using HttpClient http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            // Панели (Marzban, Remnawave, 3x-ui) по User-Agent решают, в каком формате отдать. Этот получает список ссылок.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("v2rayN/7.10");

            using HttpResponseMessage response = await http.GetAsync(url.Trim(), token);
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(token);

            Fetched result = new Fetched();
            ReadHeaders(response, result);
            result.Servers = LinkParser.ParseMany(body, out result.Skipped);

            if (result.Servers.Count == 0)
            {
                throw new Exception(body.TrimStart().StartsWith("happ://") || body.Contains("happ://crypt")
                    ? "подписка зашифрована под Happ — попроси у провайдера обычную ссылку"
                    : "в подписке нет серверов, которые я понимаю");
            }

            return result;
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
