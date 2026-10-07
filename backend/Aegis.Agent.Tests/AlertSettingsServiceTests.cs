using Aegis.Agent.Configuration;
using Aegis.Agent.Services;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Tests;

public sealed class AlertSettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aegis-alert-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_clamps_values_and_persists_them()
    {
        var incidentStore = new IncidentStoreService(
            Options.Create(new AgentOptions { DataDirectory = _directory }));
        var service = new AlertSettingsService(incidentStore);

        var saved = await service.SaveAsync(new AlertSettings(
            true,
            true,
            500,
            10,
            999,
            5,
            1,
            500,
            1,
            999));

        Assert.Equal(100, saved.CpuLoad);
        Assert.Equal(50, saved.CpuTemp);
        Assert.Equal(120, saved.GpuTemp);
        Assert.Equal(50, saved.Ram);
        Assert.Equal(30, saved.ProcessCpu);
        Assert.Equal(100, saved.Disk);
        Assert.Equal(5, saved.SustainSeconds);
        Assert.Equal(120, saved.CooldownMinutes);

        var secondService = new AlertSettingsService(incidentStore);
        var loaded = await secondService.GetAsync();

        Assert.Equal(saved, loaded);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
