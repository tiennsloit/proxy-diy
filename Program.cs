using System.Net;
using System.Text;

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:8080/");
listener.Start();

Console.WriteLine("Proxy with auth listening on http://localhost:8080/");
Console.WriteLine("Username: testuser | Password: testpass");

var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("testuser:testpass"));

while (true)
{
    var context = await listener.GetContextAsync();
    var authHeader = context.Request.Headers["Proxy-Authorization"];

    if (authHeader != expectedAuth)
    {
        context.Response.StatusCode = 407; // Proxy Authentication Required
        context.Response.AddHeader("Proxy-Authenticate", "Basic realm=\"Test Proxy\"");
        await context.Response.OutputStream.FlushAsync();
        context.Response.Close();
        Console.WriteLine("Rejected request: missing or invalid Proxy-Authorization header");
        continue;
    }

    Console.WriteLine($"[{context.Request.HttpMethod}] {context.Request.Url}");

    try
    {
        var client = new HttpClient();
        var message = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), context.Request.Url);
        var response = await client.SendAsync(message);

        context.Response.StatusCode = (int)response.StatusCode;
        await response.Content.CopyToAsync(context.Response.OutputStream);
        context.Response.Close();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error forwarding request: {ex.Message}");
        context.Response.StatusCode = 502;
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Bad Gateway"));
        context.Response.Close();
    }
}
