using System.Net;
using System.Net.Sockets;
using System.Text;
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
    public async Task RejectsChunkedRequestBodiesWithNotImplemented()
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
                "POST http://127.0.0.1:9/upload HTTP/1.1\r\nHost: 127.0.0.1:9\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request);
            var responseText = await ReadToEndAsync(stream);

            Assert.Contains("HTTP/1.1 501", responseText, StringComparison.Ordinal);
            Assert.Contains("Chunked request bodies are not supported", responseText, StringComparison.Ordinal);
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

    private static async Task<string> ReadToEndAsync(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
