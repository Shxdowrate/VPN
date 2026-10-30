using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonVpn.Core
{
    public sealed class Settings
    {
        public CoreKind Core { get; set; } = CoreKind.SingBox;

        /// <summary>Виртуальный адаптер: ловит весь трафик системы (игры, мессенджеры, всё).</summary>
        public bool UseTun { get; set; } = true;

        /// <summary>Системный прокси Windows: браузеры и программы, которые его понимают. Можно вместе с TUN.</summary>
        public bool UseSystemProxy { get; set; }

        public int ProxyPort { get; set; } = 10808;

        /// <summary>Пускать к локальному прокси другие устройства сети (телефон, приставку).</summary>
        public bool AllowLan { get; set; }

        /// <summary>Xray: резать TLS ClientHello на куски — помогает, когда мобильный оператор режет VPN по DPI.</summary>
        public bool XrayFragment { get; set; }

        public List<SubscriptionInfo> Subscriptions { get; set; } = new List<SubscriptionInfo>();
        public List<ServerGroup> Groups { get; set; } = new List<ServerGroup>();
        public Target Target { get; set; } = new Target();
        public TunnelSettings Tunnel { get; set; } = new TunnelSettings();

        /// <summary>Как часто «Авто» перепроверяет серверы (минуты).</summary>
        public int AutoCheckMinutes { get; set; } = 5;

        /// <summary>Насколько новый сервер должен быть быстрее, чтобы «Авто» переключилось (мс) — чтобы не дёргаться.</summary>
        public int AutoTolerance { get; set; } = 60;

        public string TestUrl { get; set; } = "https://www.gstatic.com/generate_204";
        public string RemoteDns { get; set; } = "1.1.1.1";
        public string LocalDns { get; set; } = "77.88.8.8";

        public bool Autostart { get; set; }
        public bool ConnectOnLaunch { get; set; }
        public bool StartMinimized { get; set; }

        /// <summary>Акцентный цвет «включено» (#RRGGBB).</summary>
        public string Accent { get; set; } = "#3DDC84";

        public double MainLeft { get; set; } = double.NaN;
        public double MainTop { get; set; } = double.NaN;
        public double MainWidth { get; set; } = 1040;
        public double MainHeight { get; set; } = 680;
        public double MiniLeft { get; set; } = double.NaN;
        public double MiniTop { get; set; } = double.NaN;
        public bool StartInMini { get; set; }

        /// <summary>Последние пинги серверов (ключ → мс, -1 — нет ответа): чтобы список не был пустым после запуска.</summary>
        public Dictionary<string, int> LastPing { get; set; } = new Dictionary<string, int>();

        // ------------------------------------------------------------------ выборки

        public IEnumerable<ServerNode> AllActive() => Subscriptions.SelectMany(s => s.Active);

        public ServerNode FindServer(string key) =>
            string.IsNullOrEmpty(key) ? null : Subscriptions.SelectMany(s => s.Servers).FirstOrDefault(s => s.Key == key);

        /// <summary>Серверы области (только включённые).</summary>
        public List<ServerNode> InScope(ScopeKind scope, string id)
        {
            switch (scope)
            {
                case ScopeKind.Subscription:
                    return Subscriptions.FirstOrDefault(s => s.Id == id)?.Active.ToList() ?? new List<ServerNode>();
                case ScopeKind.Group:
                {
                    ServerGroup g = Groups.FirstOrDefault(x => x.Id == id);

                    if (g == null)
                    {
                        return new List<ServerNode>();
                    }

                    HashSet<string> members = new HashSet<string>(g.Members);
                    return AllActive().Where(s => members.Contains(s.Key)).ToList();
                }
                default:
                    return AllActive().ToList();
            }
        }

        public string ScopeName(ScopeKind scope, string id) => scope switch
        {
            ScopeKind.Subscription => Subscriptions.FirstOrDefault(s => s.Id == id)?.Name ?? "подписка",
            ScopeKind.Group => Groups.FirstOrDefault(g => g.Id == id)?.Name ?? "группа",
            _ => "все серверы"
        };

        /// <summary>Подписка для серверов, добавленных ссылкой вручную.</summary>
        public SubscriptionInfo Manual()
        {
            SubscriptionInfo m = Subscriptions.FirstOrDefault(s => s.Id == "manual");

            if (m == null)
            {
                m = new SubscriptionInfo { Id = "manual", Name = "Мои серверы", UpdateHours = 0 };
                Subscriptions.Insert(0, m);
            }

            return m;
        }

        // ------------------------------------------------------------------ файл

        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static Settings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    Settings s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json);

                    if (s != null)
                    {
                        s.Target ??= new Target();
                        s.Tunnel ??= new TunnelSettings();
                        return s;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("Настройки не прочитались, начинаю с чистых: " + e.Message);

                try
                {
                    File.Copy(path, path + ".broken", true);
                }
                catch
                {
                    // не страшно
                }
            }

            return new Settings();
        }

        private readonly object saveLock = new object();

        public void Save(string path)
        {
            lock (saveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
                File.Move(tmp, path, true);
            }
        }
    }
}
