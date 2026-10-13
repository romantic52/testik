using System.Diagnostics;
using System.Net;

namespace Aegis.Agent.Services;

/// <summary>
/// Listening TCP ports and bound UDP ports with their owning process, and how far
/// they are reachable: only this PC (loopback) or the network (any/LAN address).
/// </summary>
public sealed class PortsService
{
    public IReadOnlyList<OpenPort> GetOpenPorts()
    {
        var names = new Dictionary<int, (string? Name, string? Path)>();
        (string? Name, string? Path) Owner(int pid)
        {
            if (names.TryGetValue(pid, out var cached)) return cached;
            string? name = pid == 4 ? "System" : null, path = null;
            if (pid > 4)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    name = p.ProcessName;
                    try { path = p.MainModule?.FileName; } catch { }
                }
                catch { }
            }
            return names[pid] = (name, path);
        }

        var result = new List<OpenPort>();
        foreach (var row in TcpTableReader.Read().Where(r => r.State == "Listen"))
        {
            var (name, path) = Owner(row.Pid);
            result.Add(new OpenPort("TCP", row.LocalAddress, row.LocalPort, Exposure(row.LocalAddress), row.Pid, name, path,
                ConnectionClassifier.Service(row.LocalPort)));
        }
        foreach (var row in TcpTableReader.ReadUdp())
        {
            var (name, path) = Owner(row.Pid);
            result.Add(new OpenPort("UDP", row.LocalAddress, row.LocalPort, Exposure(row.LocalAddress), row.Pid, name, path,
                ConnectionClassifier.Service(row.LocalPort)));
        }
        return result
            .GroupBy(p => (p.Protocol, p.Address, p.Port, p.ProcessId))
            .Select(g => g.First())
            .OrderBy(p => p.Exposure == "local")
            .ThenBy(p => p.Protocol)
            .ThenBy(p => p.Port)
            .ToList();
    }

    /// <summary>"network" = reachable from other machines (subject to the firewall), "local" = this PC only.</summary>
    public static string Exposure(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return "network";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip) ? "local" : "network";
    }
}

public sealed record OpenPort(string Protocol, string Address, int Port, string Exposure, int ProcessId,
    string? ProcessName, string? ProcessPath, string? Service);
