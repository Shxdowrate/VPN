using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPN.Core
{
    public enum RuleKind
    {
        App,
        Site
    }

    /// <summary>Правило туннелирования: программа или сайт → через VPN или напрямую.</summary>
    public sealed class RouteRule
    {
        public RuleKind Kind { get; set; }

        /// <summary>Для программы — имя exe (Discord.exe), для сайта — домен (youtube.com).</summary>
        public string Value { get; set; } = "";

        /// <summary>Полный путь к exe (только для красоты в списке).</summary>
        public string Path { get; set; } = "";

        public bool ViaVpn { get; set; } = true;
    }

    public sealed class AppSettings
    {
        public string SubscriptionUrl { get; set; } = "";

        /// <summary>Тег выбранного сервера или "auto" — самый быстрый.</summary>
        public string SelectedServer { get; set; } = "auto";

        /// <summary>Что делать с трафиком, который не попал ни в одно правило.</summary>
        public bool DefaultViaVpn { get; set; } = true;

        public bool RussiaDirect { get; set; } = true;
        public bool BlockAds { get; set; } = false;
        public List<RouteRule> Rules { get; set; } = new List<RouteRule>();

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

        /// <summary>Избранные серверы — по имени: теги меняются при обновлении подписки, а имена обычно нет.</summary>
        public List<string> Favorites { get; set; } = new List<string>();

        public bool SortByPing { get; set; }

        /// <summary>Положение и размер «смартфона» (без левой панели). NaN — по центру экрана.</summary>
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }

        public DateTime LastSubscriptionUpdate { get; set; }
        public string SubscriptionTitle { get; set; } = "";
        public long TrafficUsed { get; set; }
        public long TrafficTotal { get; set; }
        public long ExpireUnix { get; set; }
        public List<ServerProfile> Servers { get; set; } = new List<ServerProfile>();

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
                    AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Options);

                    if (loaded != null)
                    {
                        loaded.Rules ??= new List<RouteRule>();
                        loaded.Servers ??= new List<ServerProfile>();
                        loaded.Favorites ??= new List<string>();
                        return loaded;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("Не удалось прочитать настройки: " + e.Message);
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Data);
                string tmp = Paths.Settings + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
                File.Move(tmp, Paths.Settings, true);
            }
            catch (Exception e)
            {
                Log.Write("Не удалось сохранить настройки: " + e.Message);
            }
        }
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
    }

    public static class Paths
    {
        public static readonly string App = AppContext.BaseDirectory;
        public static readonly string Core = System.IO.Path.Combine(App, "core", "sing-box.exe");
        public static string RuleSets = System.IO.Path.Combine(App, "rules");
        public static readonly string Data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShxdowrateVPN");
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
