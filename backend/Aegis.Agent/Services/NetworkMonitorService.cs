using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Aegis.Agent.Services;

public sealed class NetworkMonitorService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, NetworkSample> _previous = new();

    public NetworkSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var adapters = new List<NetworkAdapterSnapshot>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                try
                {
                    var stats = nic.GetIPv4Statistics();
                    var rxPerSecond = 0d;
                    var txPerSecond = 0d;

                    if (_previous.TryGetValue(nic.Id, out var previous))
                    {
                        var seconds = (now - previous.Timestamp).TotalSeconds;
                        if (seconds > 0)
                        {
                            rxPerSecond = Math.Max(
                                0,
                                stats.BytesReceived - previous.BytesReceived) / seconds;
                            txPerSecond = Math.Max(
                                0,
                                stats.BytesSent - previous.BytesSent) / seconds;
                        }
                    }

                    _previous[nic.Id] = new NetworkSample(
                        stats.BytesReceived,
                        stats.BytesSent,
                        now);

                    adapters.Add(new NetworkAdapterSnapshot(
                        nic.Name,
                        nic.Description,
                        nic.NetworkInterfaceType.ToString(),
                        nic.Speed,
                        stats.BytesReceived,
                        stats.BytesSent,
                        Math.Round(rxPerSecond, 0),
                        Math.Round(txPerSecond, 0)));
                }
                catch
                {
                    // Some virtual adapters do not expose IPv4 statistics.
                }
            }

            return new NetworkSnapshot(
                now,
                adapters,
                Math.Round(adapters.Sum(a => a.ReceiveBytesPerSecond), 0),
                Math.Round(adapters.Sum(a => a.SendBytesPerSecond), 0));
        }
    }

    public IReadOnlyList<TcpConnectionSnapshot> GetTcpConnections(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);

        try
        {
            var owned = GetOwnedTcp4Connections();
            if (owned.Count > 0)
            {
                return owned
                    .OrderByDescending(connection =>
                        connection.State.Equals(
                            TcpState.Established.ToString(),
                            StringComparison.OrdinalIgnoreCase))
                    .ThenBy(connection => connection.State)
                    .ThenBy(connection => connection.ProcessName)
                    .ThenBy(connection => connection.RemoteAddress)
                    .Take(limit)
                    .ToList();
            }
        }
        catch
        {
            // Fall through to the portable snapshot when owner PID lookup fails.
        }

        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .OrderByDescending(connection => connection.State == TcpState.Established)
                .ThenBy(connection => connection.State)
                .ThenBy(connection => connection.RemoteEndPoint.Address.ToString())
                .Take(limit)
                .Select(connection => new TcpConnectionSnapshot(
                    connection.LocalEndPoint.Address.ToString(),
                    connection.LocalEndPoint.Port,
                    connection.RemoteEndPoint.Address.ToString(),
                    connection.RemoteEndPoint.Port,
                    connection.State.ToString(),
                    null,
                    null))
                .ToList();
        }
        catch
        {
            return Array.Empty<TcpConnectionSnapshot>();
        }
    }

    public IReadOnlyList<UdpListenerSnapshot> GetUdpListeners(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);

        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveUdpListeners()
                .OrderBy(endpoint => endpoint.Address.ToString())
                .ThenBy(endpoint => endpoint.Port)
                .Take(limit)
                .Select(endpoint => new UdpListenerSnapshot(
                    endpoint.Address.ToString(),
                    endpoint.Port))
                .ToList();
        }
        catch
        {
            return Array.Empty<UdpListenerSnapshot>();
        }
    }

    private static IReadOnlyList<TcpConnectionSnapshot> GetOwnedTcp4Connections()
    {
        const int afInet = 2;
        var size = 0;

        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            true,
            afInet,
            TcpTableClass.OwnerPidAll,
            0);

        if (result != ErrorInsufficientBuffer || size <= sizeof(int))
            return Array.Empty<TcpConnectionSnapshot>();

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref size,
                true,
                afInet,
                TcpTableClass.OwnerPidAll,
                0);

            if (result != ErrorSuccess)
                return Array.Empty<TcpConnectionSnapshot>();

            var count = Marshal.ReadInt32(buffer);
            var rowPointer = IntPtr.Add(buffer, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var connections = new List<TcpConnectionSnapshot>(count);

            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(
                    IntPtr.Add(rowPointer, index * rowSize));

                var pid = unchecked((int)row.OwningPid);
                connections.Add(new TcpConnectionSnapshot(
                    new IPAddress(row.LocalAddress).ToString(),
                    DecodePort(row.LocalPort),
                    new IPAddress(row.RemoteAddress).ToString(),
                    DecodePort(row.RemotePort),
                    ((TcpState)row.State).ToString(),
                    pid,
                    GetProcessName(pid)));
            }

            return connections;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int DecodePort(uint raw)
    {
        var bytes = BitConverter.GetBytes(raw);
        return (bytes[0] << 8) + bytes[1];
    }

    private static string? GetProcessName(int pid)
    {
        if (pid <= 0)
            return null;

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int outBufferLength,
        bool order,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        BasicListener,
        BasicConnections,
        BasicAll,
        OwnerPidListener,
        OwnerPidConnections,
        OwnerPidAll
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    private sealed record NetworkSample(
        long BytesReceived,
        long BytesSent,
        DateTimeOffset Timestamp);
}

public sealed record NetworkAdapterSnapshot(
    string Name,
    string Description,
    string Type,
    long LinkSpeedBitsPerSecond,
    long BytesReceived,
    long BytesSent,
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond);

public sealed record NetworkSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<NetworkAdapterSnapshot> Adapters,
    double TotalReceiveBytesPerSecond,
    double TotalSendBytesPerSecond);

public sealed record TcpConnectionSnapshot(
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    int? ProcessId,
    string? ProcessName);

public sealed record UdpListenerSnapshot(
    string LocalAddress,
    int LocalPort);
