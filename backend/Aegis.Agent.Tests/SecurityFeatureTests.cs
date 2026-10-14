using Aegis.Agent.Services;

namespace Aegis.Agent.Tests;

public sealed class ConnectionClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("192.168.1.20", "lan")]
    [InlineData("10.0.0.5", "lan")]
    [InlineData("172.20.3.4", "lan")]
    [InlineData("172.32.0.1", "internet")]
    [InlineData("169.254.10.1", "lan")]
    [InlineData("100.72.1.1", "provider")]
    [InlineData("8.8.8.8", "internet")]
    [InlineData("224.0.0.251", "multicast")]
    [InlineData("fe80::1", "lan")]
    [InlineData("fd12:3456::1", "lan")]
    [InlineData("2a00:1450:4001::200e", "internet")]
    [InlineData("::ffff:192.168.0.7", "lan")]
    [InlineData("0.0.0.0", "none")]
    public void Remote_address_scope(string address, string scope) =>
        Assert.Equal(scope, ConnectionClassifier.Scope(address));

    [Fact]
    public void Service_comes_from_the_well_known_side()
    {
        Assert.Equal("HTTPS", ConnectionClassifier.Service(51234, 443, "outbound"));
        Assert.Equal("RDP", ConnectionClassifier.Service(3389, 50111, "inbound"));
        Assert.Null(ConnectionClassifier.Service(50000, 50001, "outbound"));
    }

    [Fact]
    public void Direction_uses_listening_ports()
    {
        var listening = new HashSet<int> { 3389 };
        Assert.Equal("inbound", ConnectionClassifier.Direction("Established", 3389, listening));
        Assert.Equal("outbound", ConnectionClassifier.Direction("Established", 50123, listening));
        Assert.Equal("listen", ConnectionClassifier.Direction("Listen", 3389, listening));
    }
}

public sealed class AccessControlParsingTests
{
    [Theory]
    [InlineData("0xC000006A", "0xC000006D", "неверный пароль")]
    [InlineData("0xc0000064", "0xc000006d", "нет такой учётной записи")]
    [InlineData("0x0", "0xC0000234", "учётная запись заблокирована")]
    [InlineData("0x1234", "0x0", "код 0x1234")]
    [InlineData("-1073741718", "-1073741715", "неверный пароль")]   // as read from the user's Security log
    [InlineData("3221225572", "3221225581", "нет такой учётной записи")]
    [InlineData("0", "-1073741260", "учётная запись заблокирована")]
    public void Failure_reason_is_readable(string subStatus, string status, string expected) =>
        Assert.Equal(expected, AccessControlService.FailureReason(subStatus, status));

    [Theory]
    [InlineData(2, "за компьютером")]
    [InlineData(10, "удалённый рабочий стол")]
    [InlineData(99, "тип 99")]
    public void Logon_type_is_readable(int type, string expected) =>
        Assert.Equal(expected, AccessControlService.LogonType(type));
}

public sealed class CameraTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    [Fact]
    public async Task First_jpeg_is_extracted_from_mjpeg_stream()
    {
        var header = "--frame\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray();
        var second = new byte[] { 0xFF, 0xD8, 9, 9, 0xFF, 0xD9 };
        using var stream = new MemoryStream([.. header, .. Jpeg, .. "\r\n--frame\r\n"u8.ToArray(), .. second]);
        Assert.Equal(Jpeg, await CameraService.FirstJpegAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Incomplete_stream_is_rejected()
    {
        using var stream = new MemoryStream([0xFF, 0xD8, 1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => CameraService.FirstJpegAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://192.168.1.64/ISAPI/Streaming/channels/101/picture")]
    [InlineData("http://admin:secret@10.0.0.20/snapshot.jpg")]
    [InlineData("http://127.0.0.1:8899/frame.jpg")]
    [InlineData("http://camera.local/mjpeg")]
    public void Local_camera_urls_are_allowed(string url) =>
        Assert.NotNull(CameraService.ValidateUrl(url));

    [Theory]
    [InlineData("rtsp://192.168.1.64:554/stream")]
    [InlineData("http://8.8.8.8/snapshot.jpg")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("not a url")]
    public void Remote_or_non_http_urls_are_rejected(string url) =>
        Assert.Throws<ArgumentException>(() => CameraService.ValidateUrl(url));
}
