using System.Text.Json;
using Aegis.Agent.Configuration;
using Aegis.Agent.Infrastructure;
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
        var incident = TestIncident("INC-TEST-1", "Test incident");

        await store.UpsertAsync(incident);

        var incidents = await store.ListAsync();
        var audit = await store.AuditAsync();

        var saved = Assert.Single(incidents);
        Assert.Equal("INC-TEST-1", saved.Id);
        Assert.Equal("Test incident", saved.Title);
        Assert.Contains(audit, item =>
            item.Type == "incident.created" && item.Subject == "INC-TEST-1");
        Assert.True(File.Exists(Path.Combine(_directory, "aegis.db")));
        Assert.True(store.Revision > 0);
    }

    [Fact]
    public async Task Upsert_replaces_existing_incident_in_single_row()
    {
        var store = CreateStore();
        var created = DateTimeOffset.UtcNow;

        await store.UpsertAsync(new IncidentRecord(
            "INC-TEST-2", "Initial", "unit-test", "Средний", "Новый",
            "", Array.Empty<string>(), false, false, null, created));

        await store.UpsertAsync(new IncidentRecord(
            "INC-TEST-2", "Updated", "unit-test", "Высокий", "Закрыт",
            "", Array.Empty<string>(), false, true, null, created));

        var saved = Assert.Single(await store.ListAsync());

        Assert.Equal("Updated", saved.Title);
        Assert.Equal("Закрыт", saved.Status);
        Assert.True(saved.AetherSent);
        Assert.Contains(await store.AuditAsync(), x =>
            x.Type == "incident.updated" && x.Subject == "INC-TEST-2");
    }

    [Fact]
    public async Task Delete_removes_incident_and_records_audit()
    {
        var store = CreateStore();
        await store.UpsertAsync(TestIncident("INC-TEST-3", "Delete me"));

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
        var invalid = TestIncident("bad id with spaces", "Invalid");

        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync(invalid));
    }

    [Fact]
    public async Task State_survives_new_database_and_service_instances()
    {
        var first = CreateStore();
        await first.UpsertAsync(TestIncident("INC-PERSIST", "Persistent"));
        var revision = first.Revision;

        var second = CreateStore();
        var saved = Assert.Single(await second.ListAsync());

        Assert.Equal("INC-PERSIST", saved.Id);
        Assert.Equal(revision, second.Revision);
        Assert.Contains(await second.AuditAsync(), x => x.Subject == "INC-PERSIST");
    }

    [Fact]
    public async Task Legacy_incident_json_is_migrated_once()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = Path.Combine(_directory, "incidents.json");
        var legacy = new[] { TestIncident("INC-LEGACY", "Imported legacy incident") };
        await File.WriteAllTextAsync(
            legacyPath,
            JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var store = CreateStore();
        var saved = Assert.Single(await store.ListAsync());

        Assert.Equal("INC-LEGACY", saved.Id);
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + ".migrated"));
    }

    [Fact]
    public async Task Corrupt_legacy_incident_json_is_preserved()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = Path.Combine(_directory, "incidents.json");
        await File.WriteAllTextAsync(legacyPath, "{ this is not json");

        var store = CreateStore();

        Assert.Empty(await store.ListAsync());
        Assert.False(File.Exists(legacyPath));
        Assert.Single(Directory.GetFiles(_directory, "incidents.json.corrupt-*"));
    }

    [Fact]
    public async Task Legacy_audit_jsonl_is_migrated()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = Path.Combine(_directory, "audit.jsonl");
        var record = new AuditRecord(
            DateTimeOffset.UtcNow,
            "legacy.audit",
            "subject",
            "detail",
            "legacy");

        await File.WriteAllTextAsync(
            legacyPath,
            JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            + Environment.NewLine);

        var store = CreateStore();
        var audit = await store.AuditAsync();

        Assert.Contains(audit, x => x.Type == "legacy.audit");
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + ".migrated"));
    }

    private IncidentStoreService CreateStore()
    {
        Directory.CreateDirectory(_directory);
        var database = new StateDatabase(
            Options.Create(new AgentOptions { DataDirectory = _directory }));
        return new IncidentStoreService(database);
    }

    private static IncidentRecord TestIncident(string id, string title) =>
        new(
            id,
            title,
            "unit-test",
            "Высокий",
            "Новый",
            "Persistence check",
            new[] { "12:00 — created" },
            false,
            false,
            null,
            DateTimeOffset.UtcNow);

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
