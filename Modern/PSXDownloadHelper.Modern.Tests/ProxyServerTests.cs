using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using PSXDownloadHelper.Modern;

namespace PSXDownloadHelper.Modern.Tests;

public sealed class ProxyServerTests
{
    [Fact]
    public async Task ForwardsAbsoluteHttpRequestToUpstream()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var upstreamTask = Task.Run(async () =>
        {
            using var accepted = await upstream.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            await ReadHeaderAsync(stream);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response);
        });

        var proxyPort = GetFreePort();
        var proxy = new ProxyServer();
        await proxy.StartAsync(IPAddress.Loopback, proxyPort);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(
                $"GET http://127.0.0.1:{upstreamPort}/probe?q=1 HTTP/1.1\r\nHost: 127.0.0.1:{upstreamPort}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request);
            var responseText = await ReadToEndAsync(stream);

            Assert.Contains("HTTP/1.1 200 OK", responseText, StringComparison.Ordinal);
            Assert.EndsWith("OK", responseText, StringComparison.Ordinal);
            await upstreamTask;
        }
        finally
        {
            await proxy.StopAsync();
        }
    }

    [Fact]
    public async Task ForwardsChunkedRequestBodyAndTrailers()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var upstreamTask = Task.Run(async () =>
        {
            using var accepted = await upstream.AcceptTcpClientAsync();
            var stream = accepted.GetStream();
            var header = Encoding.ASCII.GetString(await ReadHeaderAsync(stream));
            using var body = new MemoryStream();
            await ReadChunkedBodyAsync(stream, body);
            var receivedBody = Encoding.ASCII.GetString(body.ToArray());
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
            await stream.WriteAsync(response);
            return (header, receivedBody);
        });

        var proxyPort = GetFreePort();
        var proxy = new ProxyServer();
        await proxy.StartAsync(IPAddress.Loopback, proxyPort);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(
                $"POST http://127.0.0.1:{upstreamPort}/upload HTTP/1.1\r\nHost: 127.0.0.1:{upstreamPort}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
                "4;source=test\r\nWiki\r\n5\r\npedia\r\n0\r\nX-Checksum: demo\r\n\r\n");
            await stream.WriteAsync(request);
            var responseText = await ReadToEndAsync(stream);
            var upstreamResult = await upstreamTask;

            Assert.Contains("HTTP/1.1 200 OK", responseText, StringComparison.Ordinal);
            Assert.Contains("Transfer-Encoding: chunked", upstreamResult.header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Wiki\r\n5\r\npedia", upstreamResult.receivedBody, StringComparison.Ordinal);
            Assert.Contains("X-Checksum: demo", upstreamResult.receivedBody, StringComparison.Ordinal);
        }
        finally
        {
            await proxy.StopAsync();
        }
    }

    [Theory]
    [InlineData("Transfer-Encoding: gzip, chunked\\r\\n", "501")]
    [InlineData("Transfer-Encoding: chunked\\r\\nContent-Length: 4\\r\\n", "400")]
    [InlineData("Transfer-Encoding: chunked\\r\\nTransfer-Encoding: chunked\\r\\n", "400")]
    [InlineData("Content-Length: 0\\r\\nContent-Length: 0\\r\\n", "400")]
    public async Task RejectsAmbiguousOrUnsupportedRequestFraming(string framingHeaders, string expectedStatus)
    {
        var proxyPort = GetFreePort();
        var proxy = new ProxyServer();
        await proxy.StartAsync(IPAddress.Loopback, proxyPort);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(
                $"POST http://127.0.0.1:9/upload HTTP/1.1\\r\\nHost: 127.0.0.1:9\\r\\n{framingHeaders}Connection: close\\r\\n\\r\\n");
            await stream.WriteAsync(request);
            var responseText = await ReadToEndAsync(stream);

            Assert.Contains($"HTTP/1.1 {expectedStatus}", responseText, StringComparison.Ordinal);
        }
        finally
        {
            await proxy.StopAsync();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<byte[]> ReadHeaderAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            buffer.WriteByte(one[0]);
            var bytes = buffer.ToArray();
            if (bytes.Length >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return bytes;
        }
        return buffer.ToArray();
    }

    private static async Task ReadChunkedBodyAsync(Stream stream, Stream destination)
    {
        while (true)
        {
            var line = await ReadLineAsync(stream);
            await destination.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
            var size = Convert.ToInt32(line.Split(';', 2)[0].Trim(), 16);
            if (size == 0)
            {
                while (true)
                {
                    var trailer = await ReadLineAsync(stream);
                    await destination.WriteAsync(Encoding.ASCII.GetBytes(trailer + "\r\n"));
                    if (trailer.Length == 0) return;
                }
            }
            var chunk = new byte[size];
            await stream.ReadExactlyAsync(chunk);
            await destination.WriteAsync(chunk);
            var terminator = new byte[2];
            await stream.ReadExactlyAsync(terminator);
            await destination.WriteAsync(terminator);
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            if (one[0] == 10)
            {
                var bytes = buffer.ToArray();
                return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
            }
            buffer.WriteByte(one[0]);
        }
        throw new EndOfStreamException();
    }

    private static async Task<string> ReadToEndAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
