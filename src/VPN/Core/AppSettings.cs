using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPN.Core
{
    public enum RuleKind
    {
        /// <summary>Программа: имя exe (Discord.exe).</summary>
        App,

        /// <summary>Сайт: домен вместе с поддоменами (youtube.com).</summary>
        Site,

        /// <summary>IP-адрес или подсеть (1.2.3.4, 10.0.0.0/8).</summary>
        Ip,

        /// <summary>Готовый набор правил sing-box: geosite-youtube, geoip-ru…</summary>
        RuleSet
    }

    /// <summary>Правило туннелирования: что (программа, сайт, IP, набор) → через VPN, напрямую или заблокировать.</summary>
    public sealed class RouteRule
    {
        public RuleKind Kind { get; set; }

        /// <summary>Программа — имя exe, сайт — домен, IP — адрес/подсеть, набор — тег (geosite-youtube).</summary>
        public string Value { get; set; } = "";

        /// <summary>Полный путь к exe (только для значка и подсказки).</summary>
        public string Path { get; set; } = "";

        public bool ViaVpn { get; set; } = true;

        /// <summary>Заблокировать вовсе (важнее ViaVpn).</summary>
        public bool Block { get; set; }

        public RouteRule Clone() => new RouteRule { Kind = Kind, Value = Value, Path = Path, ViaVpn = ViaVpn, Block = Block };
    }

    /// <summary>
    /// Профиль маршрутизации — сохранённый набор правил туннелей. Переключаются одним кликом
    /// (например, «Дом»: всё через VPN; «Игры»: только Discord и браузер).
    /// </summary>
    public sealed class RoutingProfile
    {
        public string Id { get; set; } = Ids.New();
        public string Name { get; set; } = "Основной";

        /// <summary>Что делать с трафиком, который не попал ни в одно правило.</summary>
        public bool DefaultViaVpn { get; set; } = true;

        public bool RussiaDirect { get; set; } = true;
        public bool BlockAds { get; set; }
        public List<RouteRule> Rules { get; set; } = new List<RouteRule>();

        public RoutingProfile Clone(string name) => new RoutingProfile
        {
            Name = name,
            DefaultViaVpn = DefaultViaVpn,
            RussiaDirect = RussiaDirect,
            BlockAds = BlockAds,
            Rules = Rules.Select(r => r.Clone()).ToList()
        };
    }

    /// <summary>Подписка (или группа «Мои серверы» для ссылок, добавленных вручную).</summary>
    public sealed class SubscriptionEntry
    {
        public const string ManualId = "my";

        public string Id { get; set; } = Ids.New();

        /// <summary>Пусто — группа ручных серверов.</summary>
        public string Url { get; set; } = "";

        public string Title { get; set; } = "";
        public long Used { get; set; }
        public long Total { get; set; }
        public long Expire { get; set; }
        public DateTime LastUpdate { get; set; }
        public List<ServerProfile> Servers { get; set; } = new List<ServerProfile>();

        [JsonIgnore]
        public bool IsManual => Id == ManualId;

        /// <summary>Имя для списка: заголовок из панели, иначе адрес сайта подписки.</summary>
        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title))
                {
                    return Title.Trim();
                }

                if (IsManual)
                {
                    return "Мои серверы";
                }

                try
                {
                    return new Uri(Url).Host;
                }
                catch
                {
                    return "Подписка";
                }
            }
        }

        /// <summary>Теги серверов — «id-номер»: при обновлении одной подписки теги других не сдвигаются.</summary>
        public void Retag()
        {
            int n = 1;

            foreach (ServerProfile s in Servers)
            {
                s.Tag = Id + "-" + n++;
                s.Group = Id;
            }
        }
    }

    public sealed class AppSettings
    {
        public List<SubscriptionEntry> Subscriptions { get; set; } = new List<SubscriptionEntry>();

        /// <summary>Тег выбранного сервера или "auto" — самый быстрый.</summary>
        public string SelectedServer { get; set; } = "auto";

        public List<RoutingProfile> Profiles { get; set; } = new List<RoutingProfile>();
        public string ActiveProfileId { get; set; } = "";

        public bool Autostart { get; set; }
        public bool StartMinimized { get; set; }
        public bool AutoConnect { get; set; }
        public int SubscriptionUpdateHours { get; set; } = 12;

        /// <summary>Переподключаться, если движок упал или связь пропала.</summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>Системные уведомления Windows, когда окно скрыто или неактивно.</summary>
        public bool Notifications { get; set; } = true;

        /// <summary>Ctrl+Alt+V — включить/выключить VPN из любого места.</summary>
        public bool Hotkey { get; set; } = true;

        public bool Topmost { get; set; }

        /// <summary>Открывать ссылки happ:// (кнопки «Добавить в Happ» на сайтах панелей) этой программой.</summary>
        public bool HandleHappLinks { get; set; } = true;

        /// <summary>Предлагать добавить ссылку, найденную в буфере обмена.</summary>
        public bool WatchClipboard { get; set; } = true;

        /// <summary>Избранные серверы — по имени: теги меняются при обновлении подписки, а имена обычно нет.</summary>
        public List<string> Favorites { get; set; } = new List<string>();

        public bool SortByPing { get; set; }

        /// <summary>Положение и размер окна. NaN — по центру экрана.</summary>
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }

        /// <summary>Версия формата настроек: 2 — несколько подписок и профили.</summary>
        public int Version { get; set; } = 2;

        // ------------------------------------------------------------------ вычисляемое

        /// <summary>Все серверы всех подписок подряд — то, что идёт в конфиг. Собирается методом Flatten.</summary>
        [JsonIgnore]
        public List<ServerProfile> Servers { get; set; } = new List<ServerProfile>();

        /// <summary>Активный профиль маршрутизации (есть всегда).</summary>
        [JsonIgnore]
        public RoutingProfile Active
        {
            get
            {
                if (Profiles.Count == 0)
                {
                    Profiles.Add(new RoutingProfile());
                }

                RoutingProfile p = Profiles.FirstOrDefault(x => x.Id == ActiveProfileId);

                if (p == null)
                {
                    p = Profiles[0];
                    ActiveProfileId = p.Id;
                }

                return p;
            }
        }

        [JsonIgnore]
        public bool DefaultViaVpn { get => Active.DefaultViaVpn; set => Active.DefaultViaVpn = value; }

        [JsonIgnore]
        public bool RussiaDirect { get => Active.RussiaDirect; set => Active.RussiaDirect = value; }

        [JsonIgnore]
        public bool BlockAds { get => Active.BlockAds; set => Active.BlockAds = value; }

        [JsonIgnore]
        public List<RouteRule> Rules { get => Active.Rules; set => Active.Rules = value ?? new List<RouteRule>(); }

        /// <summary>Собрать общий список серверов из подписок (после любого изменения подписок).</summary>
        public void Flatten()
        {
            Servers = Subscriptions.SelectMany(s => s.Servers).ToList();
        }

        public SubscriptionEntry Manual()
        {
            SubscriptionEntry manual = Subscriptions.FirstOrDefault(s => s.IsManual);

            if (manual == null)
            {
                manual = new SubscriptionEntry { Id = SubscriptionEntry.ManualId };
                Subscriptions.Add(manual);
            }

            return manual;
        }

        // ------------------------------------------------------------------ чтение и запись

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // NaN в положении окна («ещё не сохраняли») — без этого сериализатор падает.
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter() }
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(Paths.Settings))
                {
                    return FromJson(File.ReadAllText(Paths.Settings));
                }
            }
            catch (Exception e)
            {
                Log.Write("Не удалось прочитать настройки: " + e.Message);
            }

            AppSettings fresh = new AppSettings();
            fresh.Normalize();
            return fresh;
        }

        /// <summary>Разбор настроек, включая перенос из старого формата (одна подписка, одни правила).</summary>
        public static AppSettings FromJson(string json)
        {
            AppSettings s = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            s.Subscriptions ??= new List<SubscriptionEntry>();
            s.Profiles ??= new List<RoutingProfile>();
            s.Favorites ??= new List<string>();

            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                Migrate(s, doc.RootElement);
            }

            s.Normalize();
            return s;
        }

        private static void Migrate(AppSettings s, JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty(nameof(Version), out _))
            {
                return;
            }

            // Старые серверы назывались s1, s2… — запомним, какой был выбран, чтобы не сбросить выбор.
            string oldSelected = s.SelectedServer;

            if (s.Subscriptions.Count == 0 && root.TryGetProperty("SubscriptionUrl", out JsonElement url) && url.GetString() is string u && u.Trim().Length > 0)
            {
                var entry = new SubscriptionEntry
                {
                    Url = u.Trim(),
                    Title = Str(root, "SubscriptionTitle"),
                    Used = Long(root, "TrafficUsed"),
                    Total = Long(root, "TrafficTotal"),
                    Expire = Long(root, "ExpireUnix"),
                    LastUpdate = root.TryGetProperty("LastSubscriptionUpdate", out JsonElement d) && d.TryGetDateTime(out DateTime dt) ? dt : DateTime.MinValue
                };

                if (root.TryGetProperty("Servers", out JsonElement servers) && servers.ValueKind == JsonValueKind.Array)
                {
                    entry.Servers = JsonSerializer.Deserialize<List<ServerProfile>>(servers.GetRawText(), Options) ?? new List<ServerProfile>();
                }

                Dictionary<string, string> names = entry.Servers.ToDictionary(x => x.Tag, x => x.Name);
                entry.Retag();
                s.Subscriptions.Add(entry);

                if (oldSelected != "auto" && names.TryGetValue(oldSelected, out string name))
                {
                    s.SelectedServer = entry.Servers.FirstOrDefault(x => x.Name == name)?.Tag ?? "auto";
                }
            }

            if (s.Profiles.Count == 0)
            {
                var profile = new RoutingProfile
                {
                    Name = "Основной",
                    DefaultViaVpn = Bool(root, "DefaultViaVpn", true),
                    RussiaDirect = Bool(root, "RussiaDirect", true),
                    BlockAds = Bool(root, "BlockAds", false)
                };

                if (root.TryGetProperty("Rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    profile.Rules = JsonSerializer.Deserialize<List<RouteRule>>(rules.GetRawText(), Options) ?? new List<RouteRule>();
                }

                s.Profiles.Add(profile);
                s.ActiveProfileId = profile.Id;
            }

            // Раньше в WindowWidth хранилась ширина «смартфона» — для нового окна она мала.
            s.WindowWidth = 0;
            s.WindowHeight = 0;
            Log.Write("Настройки перенесены в новый формат");
        }

        /// <summary>Привести всё в порядок: теги, общий список, активный профиль.</summary>
        private void Normalize()
        {
            foreach (SubscriptionEntry sub in Subscriptions)
            {
                sub.Servers ??= new List<ServerProfile>();

                foreach (ServerProfile server in sub.Servers)
                {
                    server.Group = sub.Id;
                }
            }

            foreach (RoutingProfile p in Profiles)
            {
                p.Rules ??= new List<RouteRule>();
            }

            _ = Active;
            Version = 2;
            Flatten();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Data);
                string tmp = Paths.Settings + ".tmp";
                File.WriteAllText(tmp, ToJson());
                File.Move(tmp, Paths.Settings, true);
            }
            catch (Exception e)
            {
                Log.Write("Не удалось сохранить настройки: " + e.Message);
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, Options);

        private static string Str(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

        private static long Long(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) ? v : 0;

        private static bool Bool(JsonElement root, string name, bool fallback) =>
            root.TryGetProperty(name, out JsonElement e) && (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) ? e.GetBoolean() : fallback;
    }

    /// <summary>Сервер из подписки. Outbound — готовый JSON для sing-box (без тега).</summary>
    public sealed class ServerProfile
    {
        public string Tag { get; set; } = "";
        public string Name { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Outbound { get; set; } = "{}";

        /// <summary>Id подписки, к которой относится сервер (не сохраняется — восстанавливается при загрузке).</summary>
        [JsonIgnore]
        public string Group { get; set; } = "";
    }

    public static class Ids
    {
        /// <summary>Короткий id из 4 символов: годится в тег sing-box и не путается с «my».</summary>
        public static string New() => Guid.NewGuid().ToString("N").Substring(0, 4);
    }

    public static class Paths
    {
        public static readonly string App = AppContext.BaseDirectory;
        public static readonly string Core = System.IO.Path.Combine(App, "core", "sing-box.exe");

        /// <summary>Наборы правил, которые идут вместе с программой.</summary>
        public static string RuleSets = System.IO.Path.Combine(App, "rules");

        public static readonly string Data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShxdowrateVPN");

        /// <summary>Наборы правил, скачанные по требованию (geosite-youtube и т. п.).</summary>
        public static string UserRuleSets = System.IO.Path.Combine(Data, "rules");

        public static readonly string Settings = System.IO.Path.Combine(Data, "settings.json");
        public static readonly string Config = System.IO.Path.Combine(Data, "config.json");
        public static readonly string Cache = System.IO.Path.Combine(Data, "cache.db");
        public static readonly string LogFile = System.IO.Path.Combine(Data, "vpn.log");
    }

    /// <summary>Журнал: в память (для окна журнала) и в файл.</summary>
    public static class Log
    {
        private static readonly object Lock = new object();
        private static readonly LinkedList<string> Lines = new LinkedList<string>();
        public static event Action<string> Added;

        public static void Write(string line)
        {
            string text = DateTime.Now.ToString("HH:mm:ss") + "  " + line;

            lock (Lock)
            {
                Lines.AddLast(text);

                while (Lines.Count > 600)
                {
                    Lines.RemoveFirst();
                }

                try
                {
                    Directory.CreateDirectory(Paths.Data);
                    File.AppendAllText(Paths.LogFile, text + Environment.NewLine);
                }
                catch
                {
                    // журнал не должен ронять программу
                }
            }

            Added?.Invoke(text);
        }

        public static string All()
        {
            lock (Lock)
            {
                return string.Join(Environment.NewLine, Lines);
            }
        }

        public static void TrimFile()
        {
            try
            {
                if (File.Exists(Paths.LogFile) && new FileInfo(Paths.LogFile).Length > 2_000_000)
                {
                    File.Delete(Paths.LogFile);
                }
            }
            catch
            {
            }
        }
    }
}
