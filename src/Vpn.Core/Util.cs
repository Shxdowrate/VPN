using System;
using System.IO;
using System.Linq;
using System.Text;

namespace NeonVpn.Core
{
    /// <summary>Где что лежит. Окно задаёт папки при старте (Paths.Init), тесты — свои.</summary>
    public static class Paths
    {
        public static string App { get; private set; } = AppContext.BaseDirectory;
        public static string Data { get; private set; } = Path.Combine(AppContext.BaseDirectory, "data");

        public static string CoreDir => Path.Combine(App, "core");
        public static string RuleSets => Path.Combine(App, "rules");
        public static string Exe(string name) => Path.Combine(CoreDir, OperatingSystem.IsWindows() ? name + ".exe" : name);
        public static string SingBox => Exe("sing-box");
        public static string Xray => Exe("xray");

        public static string Settings => Path.Combine(Data, "settings.json");
        public static string LogFile => Path.Combine(Data, "vpn.log");
        public static string Run => Path.Combine(Data, "run");

        public static void Init(string app, string data)
        {
            App = app;
            Data = data;
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Run);
        }
    }

    public static class Log
    {
        private static readonly object Lock = new object();
        public static event Action<string> Line;

        public static void Write(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;

            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(Paths.Data);
                    File.AppendAllText(Paths.LogFile, line + Environment.NewLine);
                }
                catch
                {
                    // журнал не должен ронять программу
                }
            }

            Line?.Invoke(line);
        }

        public static string Tail(int lines = 300)
        {
            lock (Lock)
            {
                try
                {
                    return File.Exists(Paths.LogFile) ? string.Join(Environment.NewLine, File.ReadLines(Paths.LogFile).TakeLast(lines)) : "";
                }
                catch
                {
                    return "";
                }
            }
        }

        public static void Trim(long maxBytes = 2_000_000)
        {
            lock (Lock)
            {
                try
                {
                    FileInfo f = new FileInfo(Paths.LogFile);

                    if (f.Exists && f.Length > maxBytes)
                    {
                        File.WriteAllLines(Paths.LogFile, File.ReadLines(Paths.LogFile).TakeLast(2000).ToArray());
                    }
                }
                catch
                {
                    // не страшно
                }
            }
        }
    }

    public static class Names
    {
        /// <summary>Код страны из флага-эмодзи (🇩🇪 → DE) или из названия вида «DE-1», «[NL] …».</summary>
        public static string CountryCode(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }

            var runes = name.EnumerateRunes().ToList();

            for (int i = 0; i + 1 < runes.Count; i++)
            {
                int a = runes[i].Value;
                int b = runes[i + 1].Value;

                if (a >= 0x1F1E6 && a <= 0x1F1FF && b >= 0x1F1E6 && b <= 0x1F1FF)
                {
                    return new string(new[] { (char)('A' + a - 0x1F1E6), (char)('A' + b - 0x1F1E6) });
                }
            }

            return "";
        }

        /// <summary>Название без флагов (Windows всё равно рисует их буквами — флаг рисуем сами).</summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }

            StringBuilder sb = new StringBuilder();

            foreach (Rune r in name.EnumerateRunes())
            {
                if ((r.Value >= 0x1F1E6 && r.Value <= 0x1F1FF) || r.Value == 0xFE0F)
                {
                    continue;
                }

                sb.Append(r.ToString());
            }

            return sb.ToString().Trim();
        }
    }

    public static class Format
    {
        public static string Speed(long bytesPerSecond)
        {
            double bits = bytesPerSecond * 8.0;

            if (bits < 1000) return $"{bits:0} бит/с";
            if (bits < 1_000_000) return $"{bits / 1000:0.#} Кбит/с";
            if (bits < 1_000_000_000) return $"{bits / 1_000_000:0.0} Мбит/с";
            return $"{bits / 1_000_000_000:0.00} Гбит/с";
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.#} МБ";
            return $"{bytes / 1024.0 / 1024 / 1024:0.##} ГБ";
        }

        public static string Duration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

        public static string Ping(int? ms) => ms == null ? "—" : ms < 0 ? "нет ответа" : ms + " мс";
    }
}
