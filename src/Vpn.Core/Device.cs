using System;
using System.Security.Cryptography;
using System.Text;

namespace NeonVpn.Core
{
    /// <summary>Данные устройства для панелей подписок (как у Happ): постоянный HWID, система, модель.</summary>
    public static class Device
    {
        private static string hwid;

        /// <summary>32 hex-символа, постоянные для этого компьютера (MachineGuid Windows или имя машины).</summary>
        public static string Hwid
        {
            get
            {
                if (hwid != null)
                {
                    return hwid;
                }

                string seed = Environment.MachineName;

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\\Microsoft\\Cryptography");
                        seed = key?.GetValue("MachineGuid") as string ?? seed;
                    }
                    catch
                    {
                        // нет доступа — хватит имени машины
                    }
                }

                hwid = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("neonvpn:" + seed)), 0, 16).ToLowerInvariant();
                return hwid;
            }
        }

        public static string Os => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

        public static string OsVersion => Environment.OSVersion.Version.Major + "." + Environment.OSVersion.Version.Minor + "." + Environment.OSVersion.Version.Build;

        public static string Model => Environment.MachineName;
    }
}
