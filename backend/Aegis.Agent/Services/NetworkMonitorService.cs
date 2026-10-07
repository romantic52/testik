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
