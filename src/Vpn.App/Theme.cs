using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NeonVpn.Core;

namespace NeonVpn
{
    /// <summary>Перекрашивает неон под выбранные цвета — на лету, без перезапуска.</summary>
    public static class Theme
    {
        public static readonly (string name, string a, string b)[] Presets =
        {
            ("RTX", "#76FF03", "#00E5FF"),
            ("Кибер", "#00E5FF", "#FF2BD6"),
            ("Синтвейв", "#FF2BD6", "#7C4DFF"),
            ("Плазма", "#7C4DFF", "#00E5FF"),
            ("Лава", "#FF3D00", "#FFC400"),
            ("Лёд", "#B3F5FF", "#2979FF"),
            ("Токсик", "#C6FF00", "#00FFA3")
        };

        public static void Apply(Settings s)
        {
            Color a = Palette.Parse(s.Accent, Color.FromRgb(0x76, 0xFF, 0x03));
            Color b = Palette.Parse(s.Accent2, Color.FromRgb(0x00, 0xE5, 0xFF));
            double k = Math.Max(0.3, Math.Min(1.6, s.Glow));
            ResourceDictionary r = Application.Current.Resources;

            Palette.Accent = a;
            Palette.Accent2 = b;
            r["AccentColor"] = a;
            r["Accent2Color"] = b;
            r["Accent"] = Freeze(new SolidColorBrush(a));
            r["Accent2"] = Freeze(new SolidColorBrush(b));
            r["AccentSoft"] = Freeze(new SolidColorBrush(Color.FromArgb(0x22, a.R, a.G, a.B)));
            r["Accent2Soft"] = Freeze(new SolidColorBrush(Color.FromArgb(0x22, b.R, b.G, b.B)));
            r["GlowAccent"] = Freeze(new DropShadowEffect { Color = a, BlurRadius = 18 * k, ShadowDepth = 0, Opacity = Math.Min(1, 0.9 * k) });
            r["GlowAccent2"] = Freeze(new DropShadowEffect { Color = b, BlurRadius = 18 * k, ShadowDepth = 0, Opacity = Math.Min(1, 0.9 * k) });
            r["GlowSoft"] = Freeze(new DropShadowEffect { Color = a, BlurRadius = 10 * k, ShadowDepth = 0, Opacity = Math.Min(1, 0.55 * k) });

            // Тёмный текст на кнопке-заливке, если акцент светлый, иначе белый.
            double lum = (0.299 * a.R + 0.587 * a.G + 0.114 * a.B) / 255;
            r["OnAccent"] = Freeze(new SolidColorBrush(lum > 0.55 ? Color.FromRgb(0x04, 0x06, 0x0A) : Colors.White));
        }

        private static T Freeze<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }
    }

    /// <summary>Переносит подписку и правила из прошлой версии программы (ShxdowrateVPN).</summary>
    public static class Migration
    {
        public static Settings FromOldVersion()
        {
            try
            {
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShxdowrateVPN", "settings.json");

                if (!File.Exists(old))
                {
                    return null;
                }

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(old));
                JsonElement root = doc.RootElement;
                Settings s = new Settings();

                string S(string name) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
                bool B(string name, bool fallback) => root.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;

                string url = S("SubscriptionUrl");

                if (url.Length > 0)
                {
                    s.Subscriptions.Add(new SubscriptionInfo { Url = url, Name = S("SubscriptionTitle") });
                }

                s.Tunnel.Default = B("DefaultViaVpn", true) ? RouteAction.Vpn : RouteAction.Direct;
                s.Tunnel.RussiaDirect = B("RussiaDirect", true);
                s.Tunnel.BlockAds = B("BlockAds", false);
                s.Autostart = B("Autostart", false);
                s.ConnectOnLaunch = B("AutoConnect", false);
                s.StartMinimized = B("StartMinimized", false);

                if (root.TryGetProperty("Rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement r in rules.EnumerateArray())
                    {
                        string kind = r.TryGetProperty("Kind", out JsonElement k) ? k.ToString() : "";
                        string value = r.TryGetProperty("Value", out JsonElement v) ? v.GetString() ?? "" : "";
                        bool viaVpn = r.TryGetProperty("ViaVpn", out JsonElement vv) && vv.ValueKind == JsonValueKind.True;

                        if (value.Length > 0)
                        {
                            s.Tunnel.Rules.Add(new RouteRule
                            {
                                Kind = kind is "App" or "0" ? RuleKind.App : RuleKind.Domain,
                                Value = value,
                                Action = viaVpn ? RouteAction.Vpn : RouteAction.Direct
                            });
                        }
                    }
                }

                Log.Write($"Перенёс настройки прошлой версии: подписок {s.Subscriptions.Count}, правил {s.Tunnel.Rules.Count}");
                return s;
            }
            catch (Exception e)
            {
                Log.Write("Не перенёс старые настройки: " + e.Message);
                return null;
            }
        }
    }
}
