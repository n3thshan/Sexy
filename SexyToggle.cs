using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sexy
{
    /// <summary>
    /// Small WinINET helper. It does the same per-connection proxy update
    /// that v2rayN uses, then mirrors the values into the current user's
    /// Internet Settings registry key and notifies WinINET.
    /// </summary>
    public static class WinInetProxy
    {
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private const int INTERNET_OPTION_PER_CONNECTION_OPTION = 75;
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        private const int PROXY_TYPE_DIRECT = 0x00000001;
        private const int PROXY_TYPE_PROXY = 0x00000002;

        private enum PerConnOption
        {
            INTERNET_PER_CONN_FLAGS = 1,
            INTERNET_PER_CONN_PROXY_SERVER = 2,
            INTERNET_PER_CONN_PROXY_BYPASS = 3
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct InternetPerConnOptionList
        {
            public int dwSize;
            public IntPtr szConnection;
            public int dwOptionCount;
            public int dwOptionError;
            public IntPtr options;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct InternetConnectionOption
        {
            public int m_Option;
            public InternetConnectionOptionValue m_Value;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InternetConnectionOptionValue
        {
            [FieldOffset(0)]
            public int m_Int;

            [FieldOffset(0)]
            public IntPtr m_StringPtr;

            [FieldOffset(0)]
            public System.Runtime.InteropServices.ComTypes.FILETIME m_FileTime;
        }

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InternetSetOption(
            IntPtr hInternet,
            int dwOption,
            IntPtr lpBuffer,
            int dwBufferLength);

        public static void SetProxy(string proxyServer, string bypass)
        {
            if (string.IsNullOrWhiteSpace(proxyServer))
            {
                throw new ArgumentException("A proxy server is required.", "proxyServer");
            }

            Apply(proxyServer, bypass ?? string.Empty, true);
            WriteRegistry(true, proxyServer, bypass ?? string.Empty);
            Refresh();
        }

        public static void ClearProxy()
        {
            Apply(null, null, false);
            WriteRegistry(false, string.Empty, string.Empty);
            Refresh();
        }

        private static void Apply(string proxyServer, string bypass, bool enable)
        {
            int optionCount = enable ? 3 : 1;
            InternetConnectionOption[] options = new InternetConnectionOption[optionCount];

            IntPtr serverPtr = IntPtr.Zero;
            IntPtr bypassPtr = IntPtr.Zero;
            IntPtr optionsPtr = IntPtr.Zero;
            IntPtr listPtr = IntPtr.Zero;

            try
            {
                options[0].m_Option = (int)PerConnOption.INTERNET_PER_CONN_FLAGS;
                options[0].m_Value.m_Int = PROXY_TYPE_DIRECT | (enable ? PROXY_TYPE_PROXY : 0);

                if (enable)
                {
                    serverPtr = Marshal.StringToHGlobalAuto(proxyServer);
                    options[1].m_Option = (int)PerConnOption.INTERNET_PER_CONN_PROXY_SERVER;
                    options[1].m_Value.m_StringPtr = serverPtr;

                    bypassPtr = Marshal.StringToHGlobalAuto(bypass);
                    options[2].m_Option = (int)PerConnOption.INTERNET_PER_CONN_PROXY_BYPASS;
                    options[2].m_Value.m_StringPtr = bypassPtr;
                }

                int optionSize = Marshal.SizeOf(typeof(InternetConnectionOption));
                optionsPtr = Marshal.AllocCoTaskMem(optionSize * options.Length);

                for (int i = 0; i < options.Length; i++)
                {
                    IntPtr target = new IntPtr(optionsPtr.ToInt64() + (i * optionSize));
                    Marshal.StructureToPtr(options[i], target, false);
                }

                InternetPerConnOptionList list = new InternetPerConnOptionList();
                list.dwSize = Marshal.SizeOf(typeof(InternetPerConnOptionList));
                list.szConnection = IntPtr.Zero;
                list.dwOptionCount = options.Length;
                list.dwOptionError = 0;
                list.options = optionsPtr;

                listPtr = Marshal.AllocCoTaskMem(list.dwSize);
                Marshal.StructureToPtr(list, listPtr, false);

                if (!InternetSetOption(
                        IntPtr.Zero,
                        INTERNET_OPTION_PER_CONNECTION_OPTION,
                        listPtr,
                        list.dwSize))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(
                        error,
                        "InternetSetOption(INTERNET_OPTION_PER_CONNECTION_OPTION) failed.");
                }
            }
            finally
            {
                if (listPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(listPtr);
                }

                if (optionsPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(optionsPtr);
                }

                if (serverPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(serverPtr);
                }

                if (bypassPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(bypassPtr);
                }
            }
        }

        private static void WriteRegistry(bool enable, string proxyServer, string bypass)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegPath, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException(
                        "Could not open HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings.");
                }

                key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", enable ? proxyServer : string.Empty, RegistryValueKind.String);
                key.SetValue("ProxyOverride", enable ? bypass : string.Empty, RegistryValueKind.String);
                key.SetValue("AutoConfigURL", string.Empty, RegistryValueKind.String);
            }
        }

        private static void Refresh()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
