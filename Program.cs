using System.Text.Json;
using Microsoft.Playwright;

var app = new BrowserCommandApp();
await app.RunAsync();

internal sealed class BrowserCommandApp : IAsyncDisposable
{
    private readonly Dictionary<Guid, BrowserSession> sessions = new();
    private IPlaywright? playwright;
    private IBrowser? browser;
    private Guid? activeSessionId;

    public async Task RunAsync()
    {
        Console.WriteLine("cfagentbrowser ready");

        while (true)
        {
            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (await HandleCommandAsync(line.Trim()))
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error: {ex.Message}");
            }
        }

        await DisposeAsync();
    }

    private async Task<bool> HandleCommandAsync(string line)
    {
        var (command, args) = ParseCommand(line);

        switch (command)
        {
            case "new-session":
                await NewSessionAsync();
                break;
            case "use-session":
                UseSession(args);
                break;
            case "navigate":
                await NavigateAsync(args);
                break;
            case "get-snapshot":
                await GetSnapshotAsync(args);
                break;
            case "send-click":
                await SendClickAsync(args);
                break;
            case "enter-text":
                await EnterTextAsync(args);
                break;
            case "quit":
                Console.WriteLine("bye");
                return true;
            default:
                Console.WriteLine($"error: unknown command '{command}'");
                break;
        }

        return false;
    }

    private static (string Command, string Args) ParseCommand(string line)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
        {
            return (line.ToLowerInvariant(), string.Empty);
        }

        var command = line[..firstSpace].ToLowerInvariant();
        var args = line[(firstSpace + 1)..].Trim();
        return (command, args);
    }

    private async Task EnsureBrowserAsync()
    {
        if (browser is not null)
        {
            return;
        }

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    private async Task NewSessionAsync()
    {
        await EnsureBrowserAsync();

        var sessionId = Guid.NewGuid();
        var context = await browser!.NewContextAsync();
        var page = await context.NewPageAsync();

        sessions[sessionId] = new BrowserSession(sessionId, context, page);
        activeSessionId = sessionId;

        Console.WriteLine(sessionId);
    }

    private void UseSession(string args)
    {
        if (!Guid.TryParse(args, out var sessionId))
        {
            Console.WriteLine("error: use-session requires a valid GUID");
            return;
        }

        if (!sessions.ContainsKey(sessionId))
        {
            Console.WriteLine($"error: session '{sessionId}' not found");
            return;
        }

        activeSessionId = sessionId;
        Console.WriteLine($"using {sessionId}");
    }

    private BrowserSession? TryGetActiveSession()
    {
        if (activeSessionId is null)
        {
            Console.WriteLine("error: no active session. create one with new-session first");
            return null;
        }

        if (!sessions.TryGetValue(activeSessionId.Value, out var session))
        {
            Console.WriteLine("error: active session does not exist");
            activeSessionId = null;
            return null;
        }

        return session;
    }

    private async Task NavigateAsync(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            Console.WriteLine("error: navigate requires a URL");
            return;
        }

        var session = TryGetActiveSession();
        if (session is null)
        {
            return;
        }

        await session.Page.GotoAsync(args, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var snapshot = await session.Page.Accessibility.SnapshotAsync(new AccessibilitySnapshotOptions
        {
            InterestingOnly = false
        });

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        Console.WriteLine(json);
    }

    private async Task GetSnapshotAsync(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height))
        {
            Console.WriteLine("error: get-snapshot requires [width] [height] [tempfilename]");
            return;
        }

        var baseFileName = string.Join(' ', parts.Skip(2));
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            Console.WriteLine("error: tempfilename is required");
            return;
        }

        var session = TryGetActiveSession();
        if (session is null)
        {
            return;
        }

        await EnsureViewportAsync(session, width, height);
        var outputPath = $"{baseFileName}.jpg";

        await session.Page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = outputPath,
            Type = ScreenshotType.Jpeg,
            FullPage = false
        });

        Console.WriteLine(outputPath);
    }

    private async Task SendClickAsync(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height)
            || !float.TryParse(parts[2], out var x)
            || !float.TryParse(parts[3], out var y))
        {
            Console.WriteLine("error: send-click requires [width] [height] [x] [y]");
            return;
        }

        var session = TryGetActiveSession();
        if (session is null)
        {
            return;
        }

        await EnsureViewportAsync(session, width, height);
        await session.Page.Mouse.ClickAsync(x, y);
        Console.WriteLine("ok");
    }

    private async Task EnterTextAsync(string args)
    {
        if (string.IsNullOrEmpty(args))
        {
            Console.WriteLine("error: enter-text requires text");
            return;
        }

        var session = TryGetActiveSession();
        if (session is null)
        {
            return;
        }

        await session.Page.Keyboard.TypeAsync(args);
        Console.WriteLine("ok");
    }

    private static async Task EnsureViewportAsync(BrowserSession session, int width, int height)
    {
        if (session.ViewportWidth == width && session.ViewportHeight == height)
        {
            return;
        }

        await session.Page.SetViewportSizeAsync(width, height);
        session.ViewportWidth = width;
        session.ViewportHeight = height;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in sessions.Values)
        {
            await session.Context.CloseAsync();
        }

        sessions.Clear();

        if (browser is not null)
        {
            await browser.CloseAsync();
        }

        playwright?.Dispose();
    }

    private sealed class BrowserSession(Guid id, IBrowserContext context, IPage page)
    {
        public Guid Id { get; } = id;
        public IBrowserContext Context { get; } = context;
        public IPage Page { get; } = page;
        public int? ViewportWidth { get; set; }
        public int? ViewportHeight { get; set; }
    }
}
