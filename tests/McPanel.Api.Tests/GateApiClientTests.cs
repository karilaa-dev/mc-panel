using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class GateApiClientTests
{
    [Fact]
    public async Task Status_uses_the_documented_list_players_contract()
    {
        var requests = new ConcurrentQueue<(string Path, string Body)>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, requests, 1);
        var client = new GateApiClient();

        var status = await client.StatusAsync(port, CancellationToken.None);
        await server;

        Assert.Equal(2, status.ActiveConnections);
        Assert.Equal(2, status.OnlinePlayers);
        var calls = requests.ToArray();
        Assert.EndsWith("/ListPlayers", calls[0].Path);
        Assert.Equal("{}", calls[0].Body);
    }

    private static async Task ServeAsync(
        TcpListener listener, ConcurrentQueue<(string Path, string Body)> requests, int count)
    {
        for (var index = 0; index < count; index++)
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? throw new InvalidDataException("Missing HTTP request line.");
            var path = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            var contentLength = 0;
            var chunked = false;
            while (await reader.ReadLineAsync() is { Length: > 0 } header)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    contentLength = int.Parse(header[(header.IndexOf(':') + 1)..].Trim());
                if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && header.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                    chunked = true;
            }
            var body = new StringBuilder();
            if (chunked)
            {
                while (true)
                {
                    var sizeLine = await reader.ReadLineAsync() ?? throw new InvalidDataException("Missing HTTP chunk size.");
                    var size = int.Parse(sizeLine.Split(';')[0], System.Globalization.NumberStyles.HexNumber);
                    if (size == 0) { _ = await reader.ReadLineAsync(); break; }
                    var chunk = new char[size];
                    var readTotal = 0;
                    while (readTotal < size) readTotal += await reader.ReadAsync(chunk.AsMemory(readTotal));
                    body.Append(chunk);
                    _ = await reader.ReadLineAsync();
                }
            }
            else
            {
                var chars = new char[contentLength];
                var total = 0;
                while (total < chars.Length)
                {
                    var read = await reader.ReadAsync(chars.AsMemory(total));
                    if (read == 0) break;
                    total += read;
                }
                body.Append(chars, 0, total);
            }
            requests.Enqueue((path, body.ToString()));
            var response = path.EndsWith("ListPlayers", StringComparison.Ordinal)
                ? "{\"players\":[{\"id\":\"one\",\"username\":\"Alex\"},{\"id\":\"two\",\"username\":\"Steve\"}]}"
                : "{}";
            var bytes = Encoding.UTF8.GetBytes(response);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }
}
