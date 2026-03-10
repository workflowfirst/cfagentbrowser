using System.Net;
using System.Text;
using PuppeteerSharp;

public sealed partial class BrowserCommandApp
{
    private async Task<bool> TryRunWebServerAsync(string[] args)
    {
        if (!TryGetHttpPort(args, out var port))
        {
            return false;
        }

        Console.WriteLine($"cfagentbrowser http mode on http://127.0.0.1:{port}/");
        await using var server = new WebServerInterface(this, port, TimeSpan.FromMinutes(10));
        await server.RunAsync();
        await DisposeAsync();
        return true;
    }

    private static bool TryGetHttpPort(string[] args, out int port)
    {
        port = 8080;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--http", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var nextPort) && nextPort is > 0 and <= 65535)
                {
                    port = nextPort;
                }
                return true;
            }

            const string prefix = "--http=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg[prefix.Length..], out var inlinePort)
                && inlinePort is > 0 and <= 65535)
            {
                port = inlinePort;
                return true;
            }
        }

        return false;
    }

    internal async Task<string> ExecuteHttpCommandAsync(Guid sessionId, string? profileId, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "error: missing command";
        }

        var normalizedProfileId = NormalizeProfileId(profileId);
        if (string.Equals(command.Trim(), "close-session", StringComparison.OrdinalIgnoreCase))
        {
            await CloseSessionAsync(sessionId);
            return "ok";
        }

        var session = await GetOrCreateSessionAsync(sessionId, normalizedProfileId);
        await session.CommandGate.WaitAsync();
        try
        {
            session.ActiveCommandCount++;
            session.LastTouchedUtc = DateTime.UtcNow;
            using var writer = new StringWriter();
            using var _ = PushCommandContext(sessionId, writer);
            try
            {
                await HandleCommandAsync(command.Trim());
            }
            catch (Exception ex)
            {
                writer.WriteLine($"error: {ex.Message}");
            }
            finally
            {
                session.ActiveCommandCount--;
                session.LastTouchedUtc = DateTime.UtcNow;
            }

            var text = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(text) ? "ok" : text.TrimEnd();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private async Task<BrowserSession> GetOrCreateSessionAsync(Guid sessionId, string profileId)
    {
        if (sessions.TryGetValue(sessionId, out var existing))
        {
            if (!string.Equals(existing.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"session '{sessionId}' is already bound to profile '{existing.ProfileId}'");
            }

            existing.LastTouchedUtc = DateTime.UtcNow;
            return existing;
        }

        await EnsureBrowserAsync();

        var context = await browser!.CreateBrowserContextAsync();
        var page = await NewPageAsync(context);
        var session = new BrowserSession(sessionId, profileId, (BrowserContext)context, page)
        {
            LastTouchedUtc = DateTime.UtcNow
        };

        sessions[sessionId] = session;
        try
        {
            await BrowserSession.RestoreCookieJarAsync((BrowserContext)context, profileId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"warn: cookie restore failed ({ex.GetType().Name}: {ex.Message})");
        }

        return session;
    }

    private async Task CloseSessionAsync(Guid sessionId, bool requireCommandGateHeld = false)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        if (!requireCommandGateHeld)
        {
            await session.CommandGate.WaitAsync();
        }
        try
        {
            sessions.TryRemove(sessionId, out _);
            if (activeSessionId == sessionId)
            {
                activeSessionId = null;
            }

            try
            {
                await BrowserSession.PersistCookieJarAsync(session.Context, session.ProfileId);
            }
            catch
            {
                // Best-effort only.
            }

            try
            {
                await session.Context.CloseAsync();
            }
            catch
            {
                // Best-effort only.
            }
        }
        finally
        {
            if (!requireCommandGateHeld)
            {
                session.CommandGate.Release();
            }
        }
    }

    internal async Task CleanupIdleSessionsAsync(TimeSpan idleTimeout)
    {
        var cutoff = DateTime.UtcNow - idleTimeout;
        var staleIds = sessions.Values
            .Where(s => s.LastTouchedUtc < cutoff)
            .Select(s => s.Id)
            .ToArray();

        foreach (var sessionId in staleIds)
        {
            if (!sessions.TryGetValue(sessionId, out var session)) continue;
            if (session.ActiveCommandCount > 0) continue;
            if (!await session.CommandGate.WaitAsync(0)) continue;
            try
            {
                if (session.ActiveCommandCount > 0) continue;
                if (session.LastTouchedUtc >= cutoff) continue;
                await CloseSessionAsync(sessionId, requireCommandGateHeld: true);
            }
            finally
            {
                session.CommandGate.Release();
            }
        }
    }
}

internal sealed class WebServerInterface : IAsyncDisposable
{
    private readonly BrowserCommandApp app;
    private readonly HttpListener listener;
    private readonly TimeSpan idleTimeout;

    public WebServerInterface(BrowserCommandApp app, int port, TimeSpan idleTimeout)
    {
        this.app = app;
        this.idleTimeout = idleTimeout;
        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public async Task RunAsync()
    {
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"failed to listen on {listener.Prefixes.Cast<string>().First()} ({ex.GetType().Name}: {ex.Message})", ex);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancel;
        var cleanupTask = RunCleanupLoopAsync(cts.Token);
        try
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (HttpListenerException) when (!listener.IsListening)
                {
                    break;
                }

                await HandleRequestAsync(ctx);
            }
        }
        finally
        {
            cts.Cancel();
            listener.Close();
            try { await cleanupTask; } catch { }
            Console.CancelKeyPress -= OnCancel;
        }

        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cts.Cancel();
            try { listener.Stop(); } catch { }
        }
    }

    private async Task RunCleanupLoopAsync(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await app.CleanupIdleSessionsAsync(idleTimeout);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var response = ctx.Response;
        response.KeepAlive = false;
        response.ContentType = "text/plain; charset=utf-8";

        string body;
        int status;
        try
        {
            var sessionRaw = ctx.Request.QueryString["session"];
            var profileId = ctx.Request.QueryString["profile"];
            var command = ctx.Request.QueryString["command"];

            if (!Guid.TryParse(sessionRaw, out var sessionId))
            {
                status = 400;
                body = "error: query parameter 'session' must be a GUID";
            }
            else if (string.IsNullOrWhiteSpace(command))
            {
                status = 400;
                body = "error: query parameter 'command' is required";
            }
            else
            {
                status = 200;
                body = await app.ExecuteHttpCommandAsync(sessionId, profileId, command);
            }
        }
        catch (Exception ex)
        {
            status = 500;
            body = $"error: {ex.GetType().Name}: {ex.Message}";
        }

        var bytes = Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n", StringComparison.Ordinal));
        response.StatusCode = status;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.Close();
    }

    public ValueTask DisposeAsync()
    {
        try { listener.Close(); } catch { }
        return ValueTask.CompletedTask;
    }
}
