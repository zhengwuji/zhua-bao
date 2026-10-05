using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Zhuabao.Core
{
    public static class ProcessLocator
    {
        /// <summary>根据本地端口找进程名（TCP+UDP），找不到返回空。</summary>
        public static string FindByLocalPort(int port)
        {
            try
            {
                // Win32: GetExtendedTcpTable / GetExtendedUdpTable
                foreach (var row in GetTcpRows())
                    if (row.LocalPort == port) return NameOfPid(row.Pid);

                foreach (var row in GetUdpRows())
                    if (row.LocalPort == port) return NameOfPid(row.Pid);
            }
            catch (Exception ex)
            {
                Log.D("进程", "端口归属查询失败 (port=" + port + "): " + ex.Message);
            }
            return string.Empty;
        }

        private static string NameOfPid(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName;
            }
            catch { return ""; }
        }

        public struct ConnRow { public int LocalPort; public int Pid; }

        /// <summary>dwLocalPort 是网络字节序（大端）存于 DWORD 低 16 位，需按字节交换。</summary>
        private static int SwapPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

        private static IEnumerable<ConnRow> GetTcpRows()
        {
            const int buffSize = 1 << 16;
            var table = new byte[buffSize];
            int size = buffSize;
            // AF_INET, TCP_TABLE_OWNER_PID_ALL = 4
            if (GetExtendedTcpTable(table, ref size, false, 2, 4, 0) != 0) yield break;
            int count = BitConverter.ToInt32(table, 0);
            int offset = 4;
            int rowSize = 24; // dwState+dwLocalAddr+dwLocalPort+dwRemoteAddr+dwRemotePort+dwOwningPid
            for (int i = 0; i < count; i++)
            {
                int baseOff = offset + i * rowSize;
                int localPortRaw = BitConverter.ToInt32(table, baseOff + 8);
                int pid = BitConverter.ToInt32(table, baseOff + 20);
                yield return new ConnRow { LocalPort = SwapPort(localPortRaw), Pid = pid };
            }
        }

        private static IEnumerable<ConnRow> GetUdpRows()
        {
            const int buffSize = 1 << 16;
            var table = new byte[buffSize];
            int size = buffSize;
            // AF_INET, UDP_TABLE_OWNER_PID = 1
            if (GetExtendedUdpTable(table, ref size, false, 2, 1, 0) != 0) yield break;
            int count = BitConverter.ToInt32(table, 0);
            int offset = 4;
            int rowSize = 16;
            for (int i = 0; i < count; i++)
            {
                int baseOff = offset + i * rowSize;
                int localPortRaw = BitConverter.ToInt32(table, baseOff + 4);
                int pid = BitConverter.ToInt32(table, baseOff + 12);
                yield return new ConnRow { LocalPort = SwapPort(localPortRaw), Pid = pid };
            }
        }

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(byte[] pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(byte[] pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);
    }
}
