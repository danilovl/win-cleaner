using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Danilovl.WinCleaner
{
    static class Native
    {
        public const uint FILE_ATTRIBUTE_READONLY = 0x1;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // WIN32_FIND_DATAW читается вручную из неуправляемого буфера (см. FileWalker.ListDir):
        // это в разы быстрее, чем маршалинг структуры с двумя строками на каждый файл.
        public const int FIND_DATA_SIZE = 592;
        public const int FD_ATTRIBUTES = 0, FD_CREATE_LOW = 4, FD_CREATE_HIGH = 8, FD_WRITE_LOW = 20, FD_WRITE_HIGH = 24, FD_SIZE_HIGH = 28, FD_SIZE_LOW = 32, FD_NAME = 44;

        // FindExInfoBasic = 1, FIND_FIRST_EX_LARGE_FETCH = 2
        [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstFileEx(string lpFileName, int fInfoLevelId, IntPtr lpFindFileData,
            int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", EntryPoint = "FindNextFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextFile(IntPtr hFindFile, IntPtr lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FindClose(IntPtr hFindFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteFile(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool RemoveDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetFileAttributes(string lpFileName, uint dwFileAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFileAttributes(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

        public const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND = 7
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        // ------------------------------------------------ тип диска

        [StructLayout(LayoutKind.Sequential)]
        struct STORAGE_PROPERTY_QUERY
        {
            public int PropertyId;
            public int QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public int Version;
            public int Size;
            [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint ioControlCode, ref STORAGE_PROPERTY_QUERY inBuffer, int inSize,
            out DEVICE_SEEK_PENALTY_DESCRIPTOR outBuffer, int outSize, out int returned, IntPtr overlapped);

        /// <summary>
        /// Есть ли у диска задержка на перемещение головки: 2 — да (жёсткий диск), 1 — нет (SSD), 0 — узнать не удалось
        /// (сетевой, виртуальный, съёмный диск и т.п.).
        /// </summary>
        public static int SeekPenalty(char driveLetter)
        {
            try
            {
                using (var h = CreateFile(@"\\.\" + driveLetter + ":", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return 0;
                    var query = new STORAGE_PROPERTY_QUERY { PropertyId = 7, QueryType = 0 };     // StorageDeviceSeekPenaltyProperty, PropertyStandardQuery
                    DEVICE_SEEK_PENALTY_DESCRIPTOR result;
                    int returned;
                    if (!DeviceIoControl(h, 0x002D1400, ref query, Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY)),      // IOCTL_STORAGE_QUERY_PROPERTY
                            out result, Marshal.SizeOf(typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR)), out returned, IntPtr.Zero))
                        return 0;
                    return result.IncursSeekPenalty ? 2 : 1;
                }
            }
            catch { return 0; }
        }

        /// <summary>Путь с префиксом \\?\ — снимает ограничение 260 символов.</summary>
        public static string LP(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        /// <summary>Существует ли файл или папка. В отличие от File.Exists работает и для путей длиннее 260 символов.</summary>
        public static bool Exists(string path)
        {
            return GetFileAttributes(LP(path)) != INVALID_FILE_ATTRIBUTES;
        }
    }
}
