using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace Danilovl.WinCleaner
{
    class PageFileUsage
    {
        public string Name;
        public int AllocatedMb;
        public int CurrentMb;
        public int PeakMb;
    }

    static class Wmi
    {
        static ManagementScope Scope()
        {
            var scope = new ManagementScope(@"\\.\root\cimv2",
                new ConnectionOptions { EnablePrivileges = true, Impersonation = ImpersonationLevel.Impersonate });
            scope.Connect();
            return scope;
        }

        static List<ManagementObject> Query(string wql)
        {
            var list = new List<ManagementObject>();
            using (var s = new ManagementObjectSearcher(Scope(), new ObjectQuery(wql)))
                foreach (ManagementObject mo in s.Get()) list.Add(mo);
            return list;
        }

        // ------------------------------------------------ точки восстановления (теневые копии)

        public static long ShadowStorageUsed(out int copies)
        {
            copies = 0;
            long used = 0;
            try
            {
                foreach (var mo in Query("SELECT UsedSpace FROM Win32_ShadowStorage"))
                    used += Convert.ToInt64(mo["UsedSpace"]);
                copies = Query("SELECT ID FROM Win32_ShadowCopy").Count;
            }
            catch { }
            return used;
        }

        /// <summary>Удаляет все теневые копии, кроме самой новой на каждом томе.</summary>
        public static int DeleteOldShadowCopies(Action<string> log)
        {
            int deleted = 0;
            var all = Query("SELECT * FROM Win32_ShadowCopy");
            foreach (var group in all.GroupBy(mo => Convert.ToString(mo["VolumeName"])))
            {
                var ordered = group.OrderBy(mo => SafeDate(mo["InstallDate"])).ToList();
                for (int i = 0; i < ordered.Count - 1; i++)
                {
                    var mo = ordered[i];
                    try
                    {
                        string when = SafeDate(mo["InstallDate"]).ToString("yyyy-MM-dd HH:mm");
                        mo.Delete();
                        deleted++;
                        if (log != null) log(L.T("    удалена точка восстановления от ") + when);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log(L.T("    не удалось удалить точку: ") + ex.Message);
                    }
                }
            }
            return deleted;
        }

        static DateTime SafeDate(object cim)
        {
            try { return ManagementDateTimeConverter.ToDateTime(Convert.ToString(cim)); }
            catch { return DateTime.MinValue; }
        }

        // ------------------------------------------------ память и подкачка

        public static long TotalRamBytes()
        {
            try
            {
                foreach (var mo in Query("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                    return Convert.ToInt64(mo["TotalPhysicalMemory"]);
            }
            catch { }
            return 0;
        }

        public static List<PageFileUsage> PageFiles()
        {
            var list = new List<PageFileUsage>();
            try
            {
                foreach (var mo in Query("SELECT * FROM Win32_PageFileUsage"))
                    list.Add(new PageFileUsage
                    {
                        Name = Convert.ToString(mo["Name"]),
                        AllocatedMb = Convert.ToInt32(mo["AllocatedBaseSize"]),
                        CurrentMb = Convert.ToInt32(mo["CurrentUsage"]),
                        PeakMb = Convert.ToInt32(mo["PeakUsage"])
                    });
            }
            catch { }
            return list;
        }
    }
}
