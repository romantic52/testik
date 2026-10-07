using System.Text.Json;

namespace Aegis.Agent.Services;

public sealed class AlertSettingsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private AlertSettings? _cached;

    public AlertSettingsService(IncidentStoreService incidents)
    {
        _path = Path.Combine(incidents.DataDirectory, "alert-settings.json");
    }

    public async Task<AlertSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null)
                return _cached;

            if (!File.Exists(_path))
                return _cached = AlertSettings.Default;

            try
            {
                await using var stream = File.OpenRead(_path);
                var value = await JsonSerializer.DeserializeAsync<AlertSettings>(stream, _json, cancellationToken);
                return _cached = Normalize(value ?? AlertSettings.Default);
            }
            catch (JsonException)
            {
                return _cached = AlertSettings.Default;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AlertSettings> SaveAsync(AlertSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, normalized, _json, cancellationToken);

            File.Move(temp, _path, true);
            _cached = normalized;
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AlertSettings Normalize(AlertSettings value) => value with
    {
        CpuLoad = Math.Clamp(value.CpuLoad, 50, 100),
        CpuTemp = Math.Clamp(value.CpuTemp, 50, 120),
        GpuTemp = Math.Clamp(value.GpuTemp, 50, 120),
        Ram = Math.Clamp(value.Ram, 50, 100),
        ProcessCpu = Math.Clamp(value.ProcessCpu, 30, 100),
        Disk = Math.Clamp(value.Disk, 70, 100),
        SustainSeconds = Math.Clamp(value.SustainSeconds, 5, 120),
        CooldownMinutes = Math.Clamp(value.CooldownMinutes, 1, 120)
    };
}

public sealed record AlertSettings(
    bool Enabled,
    bool AutoAether,
    double CpuLoad,
    double CpuTemp,
    double GpuTemp,
    double Ram,
    double ProcessCpu,
    double Disk,
    int SustainSeconds,
    int CooldownMinutes)
{
    public static AlertSettings Default { get; } = new(
        true, true, 95, 90, 90, 95, 80, 95, 15, 10);
}
