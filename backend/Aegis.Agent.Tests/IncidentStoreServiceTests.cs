using Aegis.Agent.Configuration;
using Aegis.Agent.Services;
using Microsoft.Extensions.Options;

namespace Aegis.Agent.Tests;

public sealed class IncidentStoreServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "aegis-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Upsert_persists_incident_and_writes_audit_record()
    {
        var store = CreateStore();
        var incident = new IncidentRecord(
            "INC-TEST-1",
            "Test incident",
            "unit-test",
            "Высокий",
            "Новый",
            "Persistence check",
            new[] { "12:00 — created" },
            false,
            false,
            null,
            DateTimeOffset.UtcNow);

        await store.UpsertAsync(incident);

        var incidents = await store.ListAsync();
        var audit = await store.AuditAsync();

        var saved = Assert.Single(incidents);
        Assert.Equal("INC-TEST-1", saved.Id);
        Assert.Equal("Test incident", saved.Title);

        Assert.Contains(audit, item =>
            item.Type == "incident.created" && item.Subject == "INC-TEST-1");
    }

    [Fact]
    public async Task Upsert_replaces_existing_incident_instead_of_duplicating_it()
    {
        var store = CreateStore();
        var created = DateTimeOffset.UtcNow;

        await store.UpsertAsync(new IncidentRecord(
            "INC-TEST-2", "Initial", "unit-test", "Средний", "Новый",
            "", Array.Empty<string>(), false, false, null, created));

        await store.UpsertAsync(new IncidentRecord(
            "INC-TEST-2", "Updated", "unit-test", "Высокий", "Закрыт",
            "", Array.Empty<string>(), false, true, null, created));

        var incidents = await store.ListAsync();
        var saved = Assert.Single(incidents);

        Assert.Equal("Updated", saved.Title);
        Assert.Equal("Закрыт", saved.Status);
        Assert.True(saved.AetherSent);
    }

    [Fact]
    public async Task Delete_removes_incident_and_records_audit()
    {
        var store = CreateStore();
        await store.UpsertAsync(new IncidentRecord(
            "INC-TEST-3", "Delete me", "unit-test", "Низкий", "Новый",
            "", Array.Empty<string>(), false, false, null, DateTimeOffset.UtcNow));

        var removed = await store.DeleteAsync("INC-TEST-3");

        Assert.True(removed);
        Assert.Empty(await store.ListAsync());
        Assert.Contains(await store.AuditAsync(), x =>
            x.Type == "incident.deleted" && x.Subject == "INC-TEST-3");
    }

    [Fact]
    public async Task Upsert_rejects_invalid_incident_id()
    {
        var store = CreateStore();

        var incident = new IncidentRecord(
            "bad id with spaces",
            "Invalid",
            "unit-test",
            "Средний",
            "Новый",
            "",
            Array.Empty<string>(),
            false,
            false,
            null,
            DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(incident));
    }

    [Fact]
    public async Task Corrupt_incident_file_is_preserved_instead_of_overwritten()
    {
        Directory.CreateDirectory(_directory);
        var original = Path.Combine(_directory, "incidents.json");
        await File.WriteAllTextAsync(original, "{ this is not json");

        var store = CreateStore();
        var incidents = await store.ListAsync();

        Assert.Empty(incidents);
        Assert.False(File.Exists(original));
        Assert.Single(Directory.GetFiles(_directory, "incidents.corrupt-*.json"));
    }

    [Fact]
    public async Task Audit_rotates_when_size_limit_is_reached()
    {
        Directory.CreateDirectory(_directory);
        var auditPath = Path.Combine(_directory, "audit.jsonl");
        await File.WriteAllTextAsync(auditPath, new string('x', 1024 * 1024));

        var store = new IncidentStoreService(Options.Create(new AgentOptions
        {
            DataDirectory = _directory,
            AuditMaxMegabytes = 1,
            AuditRetentionFiles = 2
        }));

        await store.AppendAuditAsync(new AuditRecord(
            DateTimeOffset.UtcNow,
            "rotation.test",
            "audit",
            "valid record after rotation",
            "unit-test"));

        Assert.True(File.Exists(Path.Combine(_directory, "audit.1.jsonl")));
        var audit = await store.AuditAsync(10);
        Assert.Contains(audit, item => item.Type == "rotation.test");
    }

    private IncidentStoreService CreateStore() =>
        new(Options.Create(new AgentOptions { DataDirectory = _directory }));

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
