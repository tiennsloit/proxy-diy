using System.Net;
using System.Net.Sockets;
using System.Text;

var listener = new TcpListener(IPAddress.Loopback, 8080);
listener.Start();
Console.WriteLine("Proxy with auth + CONNECT support listening on 127.0.0.1:8080");
Console.WriteLine("Username: testuser | Password: testpass");

var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:testpass"));

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleClient(client, expectedAuth);
}

async Task HandleClient(TcpClient client, string expectedAuth)
{
    using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
    using var writer = new StreamWriter(stream, Encoding.ASCII, 8192, leaveOpen: true) { AutoFlush = true };

    string? requestLine = await reader.ReadLineAsync();
    if (requestLine == null) return;

    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? line;
    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
    {
        int idx = line.IndexOf(':');
        if (idx > 0)
        {
            headers[line[..idx]] = line[(idx + 1)..].Trim();
        }
    }

    // Check Proxy-Authorization
    if (!headers.TryGetValue("Proxy-Authorization", out var auth) || auth != expectedAuth)
    {
        await writer.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\n");
        await writer.WriteAsync("Proxy-Authenticate: Basic realm=\"Test Proxy\"\r\n\r\n");
        return;
    }

    string[] parts = requestLine.Split(' ');
    if (parts.Length < 3) return;

    string method = parts[0];
    string target = parts[1];

    if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
    {
        // CONNECT host:port
        string[] hostParts = target.Split(':');
        string host = hostParts[0];
        int port = hostParts.Length > 1 ? int.Parse(hostParts[1]) : 443;

        try
        {
            using var remote = new TcpClient();
            await remote.ConnectAsync(host, port);

            await writer.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n");

            var remoteStream = remote.GetStream();

            // Start bidirectional copy
            _ = remoteStream.CopyToAsync(stream);
            await stream.CopyToAsync(remoteStream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CONNECT error: {ex.Message}");
            await writer.WriteAsync("HTTP/1.1 502 Bad Gateway\r\n\r\n");
        }
        return;
    }
    else
    {
        // Normal HTTP request (GET, POST, etc.)
        try
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(target);
            await writer.WriteAsync($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n");

            foreach (var header in response.Headers)
            {
                await writer.WriteAsync($"{header.Key}: {string.Join(",", header.Value)}\r\n");
            }
            await writer.WriteAsync("\r\n");
            await response.Content.CopyToAsync(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error forwarding request: {ex.Message}");
            await writer.WriteAsync("HTTP/1.1 502 Bad Gateway\r\n\r\n");
        }
    }
}
