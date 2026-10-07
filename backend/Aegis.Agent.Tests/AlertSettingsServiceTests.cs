using System.Text.Json;
using Aegis.Agent.Configuration;
using Aegis.Agent.Infrastructure;
using Aegis.Agent.Services;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Tests;

public sealed class AlertSettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aegis-alert-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_clamps_values_and_persists_them_in_sqlite()
    {
        var service = CreateService();

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

        var loaded = await CreateService().GetAsync();

        Assert.Equal(saved, loaded);
        Assert.True(File.Exists(Path.Combine(_directory, "aegis.db")));
    }

    [Fact]
    public async Task Legacy_alert_settings_json_is_migrated()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = Path.Combine(_directory, "alert-settings.json");
        var legacy = AlertSettings.Default with
        {
            CpuLoad = 88,
            SustainSeconds = 22,
            AutoAether = false
        };

        await File.WriteAllTextAsync(
            legacyPath,
            JsonSerializer.Serialize(
                legacy,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var loaded = await CreateService().GetAsync();

        Assert.Equal(88, loaded.CpuLoad);
        Assert.Equal(22, loaded.SustainSeconds);
        Assert.False(loaded.AutoAether);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + ".migrated"));
    }

    private AlertSettingsService CreateService()
    {
        Directory.CreateDirectory(_directory);
        var database = new StateDatabase(
            Options.Create(new AgentOptions { DataDirectory = _directory }));
        return new AlertSettingsService(database);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
