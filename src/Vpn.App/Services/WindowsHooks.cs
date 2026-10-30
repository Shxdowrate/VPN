using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NeonVpn.Core;

namespace NeonVpn.Services
{
    /// <summary>Системный прокси Windows (WinINet): включение, выключение и восстановление прежних настроек.</summary>
    public sealed class WindowsHooks : ISystemHooks
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string Bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

        private int? savedEnable;
        private string savedServer;
        private string savedOverride;

        public void SetSystemProxy(string host, int port)
        {
            try
            {
                using RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, true);

                if (k == null)
                {
                    return;
                }

                // Запоминаем, что было, только если прокси ещё не наш.
                string server = k.GetValue("ProxyServer") as string;

                if (server != $"{host}:{port}")
                {
                    savedEnable = k.GetValue("ProxyEnable") as int?;
                    savedServer = server;
                    savedOverride = k.GetValue("ProxyOverride") as string;
                }

                k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                k.SetValue("ProxyServer", $"{host}:{port}");
                k.SetValue("ProxyOverride", Bypass);
                Refresh();
                Log.Write($"Системный прокси: {host}:{port}");
            }
            catch (Exception e)
            {
                Log.Write("Не включил системный прокси: " + e.Message);
            }
        }

        public void ClearSystemProxy()
        {
            try
            {
                using RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, true);

                if (k == null)
                {
                    return;
                }

                k.SetValue("ProxyEnable", savedEnable ?? 0, RegistryValueKind.DWord);

                if (savedServer != null) k.SetValue("ProxyServer", savedServer);
                else k.DeleteValue("ProxyServer", false);

                if (savedOverride != null) k.SetValue("ProxyOverride", savedOverride);

                Refresh();
                Log.Write("Системный прокси выключен");
            }
            catch (Exception e)
            {
                Log.Write("Не выключил системный прокси: " + e.Message);
            }
        }

        /// <summary>Если программа упала с включённым прокси — при старте убираем его, иначе интернет «пропадёт».</summary>
        public static void CleanupLeftover(int port)
        {
            try
            {
                using RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, true);

                if (k != null && (k.GetValue("ProxyServer") as string) == "127.0.0.1:" + port && (k.GetValue("ProxyEnable") as int?) == 1)
                {
                    k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    Refresh();
                    Log.Write("Убрал системный прокси, оставшийся от прошлого запуска");
                }
            }
            catch
            {
                // не страшно
            }
        }

        private static void Refresh()
        {
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // INTERNET_OPTION_SETTINGS_CHANGED
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // INTERNET_OPTION_REFRESH
        }

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);
    }

    /// <summary>
    /// Автозапуск с Windows через Планировщик заданий: программа просит права администратора,
    /// а задание «с наивысшими правами» запускает её без окна UAC при входе.
    /// </summary>
    public static class Autostart
    {
        private const string Task = "NeonVPN";

        public static void Set(bool enabled)
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "VPN.exe");
                string args = enabled
                    ? $"/Create /F /TN \"{Task}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:10 /TR \"\\\"{exe}\\\" --autostart\""
                    : $"/Delete /F /TN \"{Task}\"";

                using Process p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                p?.WaitForExit(8000);

                // Старый способ (ключ Run) убираем — он требовал бы UAC при каждом входе.
                using RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                run?.DeleteValue("ShxdowrateVPN", false);
                run?.DeleteValue("NeonVPN", false);
            }
            catch (Exception e)
            {
                Log.Write("Автозапуск: " + e.Message);
            }
        }
    }
}
