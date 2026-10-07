namespace Aegis.Agent.Models;

using Aegis.Agent.Services;

public sealed record TelemetryFrame(
    DateTimeOffset Timestamp,
    SystemSnapshot System,
    IReadOnlyList<ProcessSnapshot> Processes,
    IReadOnlyList<SensorReading> Sensors);

public sealed record TelemetryHistoryPoint(
    DateTimeOffset Timestamp,
    double? CpuLoadPercent,
    double? CpuTemperatureC,
    double? MemoryLoadPercent,
    double? GpuLoadPercent,
    double? GpuTemperatureC,
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond);

public sealed record AgentStatus(
    string Status,
    string Service,
    string Version,
    string Machine,
    DateTimeOffset Timestamp,
    DateTimeOffset? LastSampleAt,
    int SampleIntervalMs,
    int HistoryPoints,
    string DataDirectory);
