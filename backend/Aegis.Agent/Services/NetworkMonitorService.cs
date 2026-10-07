using System.Net.NetworkInformation;

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
                            rxPerSecond = Math.Max(0, stats.BytesReceived - previous.BytesReceived) / seconds;
                            txPerSecond = Math.Max(0, stats.BytesSent - previous.BytesSent) / seconds;
                        }
                    }

                    _previous[nic.Id] = new NetworkSample(stats.BytesReceived, stats.BytesSent, now);

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
                    connection.State.ToString()))
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

    private sealed record NetworkSample(long BytesReceived, long BytesSent, DateTimeOffset Timestamp);
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
    string State);

public sealed record UdpListenerSnapshot(
    string LocalAddress,
    int LocalPort);
