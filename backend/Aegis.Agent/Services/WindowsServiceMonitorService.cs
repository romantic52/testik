using System.ComponentModel;
using System.ServiceProcess;

namespace Aegis.Agent.Services;

public sealed class WindowsServiceMonitorService
{
    public IReadOnlyList<WindowsServiceSnapshot> GetServices(int limit = 500)
    {
        limit = Math.Clamp(limit, 1, 2_000);

        try
        {
            return ServiceController.GetServices()
                .OrderBy(service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(service =>
                {
                    using (service)
                    {
                        return new WindowsServiceSnapshot(
                            service.ServiceName,
                            service.DisplayName,
                            Safe(() => service.Status.ToString()),
                            Safe(() => service.ServiceType.ToString()),
                            Safe(() => (bool?)service.CanStop),
                            Safe(() => (bool?)service.CanPauseAndContinue));
                    }
                })
                .ToList();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<WindowsServiceSnapshot>();
        }
        catch (Win32Exception)
        {
            return Array.Empty<WindowsServiceSnapshot>();
        }
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}

public sealed record WindowsServiceSnapshot(
    string Name,
    string DisplayName,
    string? Status,
    string? ServiceType,
    bool? CanStop,
    bool? CanPauseAndContinue);
