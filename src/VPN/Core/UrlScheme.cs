using System;
using Microsoft.Win32;

namespace VPN.Core
{
    /// <summary>
    /// Регистрация ссылок вида happ://… и shxvpn://… — чтобы кнопка «Добавить в приложение» на сайте панели
    /// открывала эту программу. Пишем в HKCU, права администратора для этого не нужны.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static class UrlScheme
    {
        public const string Own = "shxvpn";
        public const string Happ = "happ";

        /// <summary>
        /// Зарегистрировать схему. Без force чужой обработчик (например, установленный Happ) не трогаем.
        /// Возвращает true, если ссылки теперь открываются нами.
        /// </summary>
        public static bool Register(string scheme, bool force)
        {
            string exe = Environment.ProcessPath ?? "";

            if (exe.Length == 0)
            {
                return false;
            }

            try
            {
                string key = @"Software\Classes\" + scheme;
                string command = "\"" + exe + "\" \"%1\"";

                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(key + @"\shell\open\command"))
                {
                    string current = existing?.GetValue("") as string ?? "";

                    if (current.Equals(command, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (current.Length > 0 && !force && !current.Contains(System.IO.Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                using RegistryKey root = Registry.CurrentUser.CreateSubKey(key);
                root.SetValue("", "URL:" + scheme);
                root.SetValue("URL Protocol", "");

                using (RegistryKey icon = root.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue("", "\"" + exe + "\",0");
                }

                using (RegistryKey cmd = root.CreateSubKey(@"shell\open\command"))
                {
                    cmd.SetValue("", command);
                }

                Log.Write("Ссылки " + scheme + ":// открываются этой программой");
                return true;
            }
            catch (Exception e)
            {
                Log.Write("Регистрация " + scheme + "://: " + e.Message);
                return false;
            }
        }

        /// <summary>Убрать регистрацию, только если она наша.</summary>
        public static void Unregister(string scheme)
        {
            try
            {
                string key = @"Software\Classes\" + scheme;
                string exe = Environment.ProcessPath ?? "";

                using (RegistryKey existing = Registry.CurrentUser.OpenSubKey(key + @"\shell\open\command"))
                {
                    string current = existing?.GetValue("") as string ?? "";

                    if (current.Length == 0 || exe.Length == 0 || !current.Contains(exe, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                Registry.CurrentUser.DeleteSubKeyTree(key, false);
            }
            catch (Exception e)
            {
                Log.Write("Снятие " + scheme + "://: " + e.Message);
            }
        }
    }
}
