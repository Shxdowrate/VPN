using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using NeonVpn.Core;

namespace NeonVpn
{
    /// <summary>Единственный настраиваемый цвет — акцент «включено». Меняется на лету.</summary>
    public static class Theme
    {
        public static readonly (string name, string color)[] Presets =
        {
            ("Зелёный", "#3DDC84"),
            ("Синий", "#5B9DFF"),
            ("Фиолетовый", "#A78BFA"),
            ("Оранжевый", "#FF9F43"),
            ("Белый", "#ECEDEF")
        };

        public static void Apply(Settings s)
        {
            // Цвета прошлого (неонового) оформления сбрасываем на зелёный по умолчанию.
            if (Presets.All(p => !string.Equals(p.color, s.Accent, StringComparison.OrdinalIgnoreCase)))
            {
                s.Accent = Presets[0].color;
            }

            Color a = Palette.Parse(s.Accent, Color.FromRgb(0x3D, 0xDC, 0x84));
            ResourceDictionary r = Application.Current.Resources;

            Palette.Accent = a;
            r["AccentColor"] = a;
            r["Accent"] = Freeze(new SolidColorBrush(a));
            r["AccentSoft"] = Freeze(new SolidColorBrush(Color.FromArgb(0x16, a.R, a.G, a.B)));
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
