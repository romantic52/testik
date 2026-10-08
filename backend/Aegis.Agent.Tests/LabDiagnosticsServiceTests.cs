using Aegis.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aegis.Agent.Tests;

public sealed class LabDiagnosticsServiceTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(32, true)]
    [InlineData(33, false)]
    [InlineData(512, false)]
    public void Only_bounded_disk_file_sizes_are_allowed(int sizeMiB, bool valid)
    {
        Assert.Equal(valid, LabDiagnosticsService.ValidSize(sizeMiB));
    }

    [Fact]
    public async Task Disk_check_reads_back_verified_content_and_reports_speed()
    {
        var service = new LabDiagnosticsService(NullLogger<LabDiagnosticsService>.Instance);
        var result = await service.CheckDiskAsync(8, CancellationToken.None);

        Assert.Equal(8, result.SizeMiB);
        Assert.True(result.Verified);
        Assert.True(result.WriteMiBps > 0);
        Assert.True(result.ReadMiBps > 0);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_touching_disk()
    {
        var service = new LabDiagnosticsService(NullLogger<LabDiagnosticsService>.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CheckDiskAsync(300, CancellationToken.None));
    }
}
