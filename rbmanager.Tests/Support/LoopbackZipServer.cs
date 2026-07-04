using System.Net;
using System.Net.Sockets;

namespace RbManager.Tests.Support;

// A minimal loopback HTTP server for the URL install path. Serves the given
// bytes at a single path and 404s everything else, so no external network is
// touched. Bound to 127.0.0.1 on an OS-assigned free port.
internal sealed class LoopbackZipServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _body;
    private readonly string _servedPath;
    private readonly Task _loop;

    public string BaseUrl { get; }
    public string ZipUrl => BaseUrl + _servedPath.TrimStart('/');

    // servedPath: the single route that returns the body (e.g. "/pkg.zip").
    public LoopbackZipServer(byte[] body, string servedPath = "/pkg.zip")
    {
        _body = body;
        _servedPath = servedPath;
        int port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; } // listener stopped

            using HttpListenerResponse res = ctx.Response;
            if (string.Equals(ctx.Request.Url?.AbsolutePath, _servedPath, StringComparison.Ordinal))
            {
                res.StatusCode = 200;
                res.ContentType = "application/zip";
                res.ContentLength64 = _body.Length;
                await res.OutputStream.WriteAsync(_body);
            }
            else
            {
                res.StatusCode = 404;
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _loop.Wait(1000); } catch { }
        ((IDisposable)_listener).Dispose();
    }
}
