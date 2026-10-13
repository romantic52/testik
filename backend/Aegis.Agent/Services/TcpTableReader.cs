using System.Net;
using System.Runtime.InteropServices;

namespace Aegis.Agent.Services;

/// <summary>
/// Reads the Windows TCP tables (IPv4 + IPv6) with owning PIDs and, when the
/// process is elevated, per-connection byte counters (TCP Extended Statistics).
/// </summary>
public static class TcpTableReader
{
    public sealed record Row(
        string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort,
        string State, int Pid, bool IsIPv4, uint RawLocal, uint RawLocalPort, uint RawRemote, uint RawRemotePort, uint RawState);

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int OwnerPidAll = 5;

    public static IReadOnlyList<Row> Read()
    {
        var rows = new List<Row>(256);
        ReadV4(rows);
        ReadV6(rows);
        return rows;
    }

    private static void ReadV4(List<Row> rows)
    {
        var buffer = Fetch(AfInet);
        if (buffer == IntPtr.Zero) return;
        try
        {
            var count = Marshal.ReadInt32(buffer);
            var size = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var ptr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(ptr, i * size));
                rows.Add(new Row(
                    new IPAddress(r.LocalAddress).ToString(), Port(r.LocalPort),
                    new IPAddress(r.RemoteAddress).ToString(), Port(r.RemotePort),
                    StateName(r.State), unchecked((int)r.OwningPid), true,
                    r.LocalAddress, r.LocalPort, r.RemoteAddress, r.RemotePort, r.State));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ReadV6(List<Row> rows)
    {
        var buffer = Fetch(AfInet6);
        if (buffer == IntPtr.Zero) return;
        try
        {
            var count = Marshal.ReadInt32(buffer);
            var size = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            var ptr = IntPtr.Add(buffer, 4);
            for (var i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(IntPtr.Add(ptr, i * size));
                rows.Add(new Row(
                    new IPAddress(r.LocalAddress, r.LocalScopeId).ToString(), Port(r.LocalPort),
                    new IPAddress(r.RemoteAddress, r.RemoteScopeId).ToString(), Port(r.RemotePort),
                    StateName(r.State), unchecked((int)r.OwningPid), false, 0, 0, 0, 0, r.State));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr Fetch(int family)
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, OwnerPidAll, 0);
        for (var attempt = 0; attempt < 3 && result == ErrorInsufficientBuffer; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            result = GetExtendedTcpTable(buffer, ref size, true, family, OwnerPidAll, 0);
            if (result == ErrorSuccess) return buffer;
            Marshal.FreeHGlobal(buffer);
        }
        return IntPtr.Zero;
    }

    /// <summary>Turns on byte counting for an established IPv4 connection (needs elevation).</summary>
    public static bool EnableByteCounters(Row row)
    {
        if (!row.IsIPv4) return false;
        var tcp = ToMibRow(row);
        var rw = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(rw, 1);
            return SetPerTcpConnectionEStats(ref tcp, TcpConnectionEstatsData, rw, 0, 1, 0) == ErrorSuccess;
        }
        finally { Marshal.FreeHGlobal(rw); }
    }

    /// <summary>Bytes in/out since counting was enabled, or null when unavailable.</summary>
    public static (long In, long Out)? ReadByteCounters(Row row)
    {
        if (!row.IsIPv4) return null;
        var tcp = ToMibRow(row);
        var rodSize = Marshal.SizeOf<TcpEstatsDataRod>();
        var rod = Marshal.AllocHGlobal(rodSize);
        try
        {
            var result = GetPerTcpConnectionEStats(ref tcp, TcpConnectionEstatsData,
                IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, (uint)rodSize);
            if (result != ErrorSuccess) return null;
            var data = Marshal.PtrToStructure<TcpEstatsDataRod>(rod);
            return ((long)data.DataBytesIn, (long)data.DataBytesOut);
        }
        finally { Marshal.FreeHGlobal(rod); }
    }

    private static MibTcpRow ToMibRow(Row row) => new()
    {
        State = row.RawState, LocalAddress = row.RawLocal, LocalPort = row.RawLocalPort,
        RemoteAddress = row.RawRemote, RemotePort = row.RawRemotePort
    };

    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static string StateName(uint state) => state switch
    {
        1 => "Closed", 2 => "Listen", 3 => "SynSent", 4 => "SynReceived", 5 => "Established",
        6 => "FinWait1", 7 => "FinWait2", 8 => "CloseWait", 9 => "Closing", 10 => "LastAck",
        11 => "TimeWait", 12 => "DeleteTcb", _ => "Unknown"
    };

    private const int TcpConnectionEstatsData = 1;

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int type, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int type,
        IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddress;
        public uint LocalScopeId, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddress;
        public uint RemoteScopeId, RemotePort, State, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpEstatsDataRod
    {
        public ulong DataBytesOut, DataSegsOut, DataBytesIn, DataSegsIn, SegsOut, SegsIn;
        public uint SoftErrors, SoftErrorReason, SndUna, SndNxt, SndMax;
        public ulong ThruBytesAcked;
        public uint RcvNxt;
        public ulong ThruBytesReceived;
    }
}
