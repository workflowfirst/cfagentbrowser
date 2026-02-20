using System.Runtime.InteropServices;
using System.Text.Json;
using PuppeteerSharp;

var app = new BrowserCommandApp();
await app.RunAsync();

internal sealed class BrowserCommandApp : IAsyncDisposable
{
    private readonly Dictionary<Guid, BrowserSession> sessions = new();
    private readonly BrowserLaunchConfig launchConfig = BrowserLaunchConfig.FromEnvironment();
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

        var chromiumPath = ResolveChromiumPath(launchConfig);

        if (chromiumPath is null && launchConfig.AllowDownloadChromium)
        {
            Console.WriteLine("Downloading compatible Chromium (first run)…");
            var fetcher = new BrowserFetcher();
            await fetcher.DownloadAsync();
        }

        var launchArgs = new List<string>
        {
            "--no-sandbox",
            "--disable-gpu",
            "--font-render-hinting=none",
            "--hide-scrollbars"
        };

        if (launchConfig.ExtraChromiumArgs.Count > 0)
        {
            launchArgs.AddRange(launchConfig.ExtraChromiumArgs);
        }

        var launchOptions = new LaunchOptions
        {
            Headless = true,
            ExecutablePath = chromiumPath,
            Args = launchArgs.ToArray()
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(launchConfig.TimeoutSeconds));
        browser = await Puppeteer.LaunchAsync(launchOptions).WaitAsync(cts.Token);
    }

    private static string? ResolveChromiumPath(BrowserLaunchConfig cfg)
    {
        // Prefer an explicit path if provided.
        string? chromiumPath = cfg switch
        {
            { } when !string.IsNullOrWhiteSpace(cfg.ChromiumPath) && File.Exists(cfg.ChromiumPath) => cfg.ChromiumPath,
            _ => null
        };

        if (chromiumPath is null)
        {
            IEnumerable<string> candidates = Array.Empty<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                candidates =
                [
                    "/usr/bin/chromium",
                    "/usr/bin/chromium-browser",
                    "/usr/bin/google-chrome",
                    "/usr/bin/chrome"
                ];
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
                var pfx86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
                var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                candidates =
                [
                    Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pfx86, "Google", "Chrome", "Application", "chrome.exe"),
                    Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(pfx86, "Microsoft", "Edge", "Application", "msedge.exe"),
                    Path.Combine(lad, "Chromium", "Application", "chrome.exe")
                ];
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                candidates =
                [
                    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                    "/Applications/Chromium.app/Contents/MacOS/Chromium"
                ];
            }

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    chromiumPath = candidate;
                    break;
                }
            }

            if (chromiumPath is null)
            {
                var env = Environment.GetEnvironmentVariable("PUPPETEER_EXECUTABLE_PATH")
                          ?? Environment.GetEnvironmentVariable("CHROME_PATH");

                if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
                {
                    chromiumPath = env;
                }
            }
        }

        return chromiumPath;
    }

    private async Task NewSessionAsync()
    {
        await EnsureBrowserAsync();

        var sessionId = Guid.NewGuid();
        var context = await browser!.CreateBrowserContextAsync();
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

        await session.Page.GoToAsync(args, new NavigationOptions
        {
            WaitUntil =
            [
                WaitUntilNavigation.Networkidle0
            ]
        });

        var snapshot = await session.Page.Accessibility.SnapshotAsync();

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

        await session.Page.ScreenshotAsync(outputPath, new ScreenshotOptions
        {
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
            || !decimal.TryParse(parts[2], out var x)
            || !decimal.TryParse(parts[3], out var y))
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

        await session.Page.SetViewportAsync(new ViewPortOptions
        {
            Width = width,
            Height = height
        });

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
            await browser.DisposeAsync();
        }
    }

    private sealed class BrowserSession(Guid id, BrowserContext context, IPage page)
    {
        public Guid Id { get; } = id;
        public BrowserContext Context { get; } = context;
        public IPage Page { get; } = page;
        public int? ViewportWidth { get; set; }
        public int? ViewportHeight { get; set; }
    }

    private sealed class BrowserLaunchConfig
    {
        public string? ChromiumPath { get; init; }
        public bool AllowDownloadChromium { get; init; }
        public int TimeoutSeconds { get; init; } = 30;
        public IReadOnlyList<string> ExtraChromiumArgs { get; init; } = [];

        public static BrowserLaunchConfig FromEnvironment()
        {
            var timeout = 30;
            if (int.TryParse(Environment.GetEnvironmentVariable("CFAGENT_TIMEOUT_SECONDS"), out var parsedTimeout) && parsedTimeout > 0)
            {
                timeout = parsedTimeout;
            }

            var allowDownload = true;
            var allowDownloadValue = Environment.GetEnvironmentVariable("CFAGENT_ALLOW_DOWNLOAD_CHROMIUM");
            if (bool.TryParse(allowDownloadValue, out var parsedAllowDownload))
            {
                allowDownload = parsedAllowDownload;
            }

            var chromiumPath = Environment.GetEnvironmentVariable("CFAGENT_CHROMIUM_PATH");
            var extraArgs = Environment.GetEnvironmentVariable("CFAGENT_EXTRA_CHROMIUM_ARGS")
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToArray() ?? [];

            return new BrowserLaunchConfig
            {
                ChromiumPath = chromiumPath,
                AllowDownloadChromium = allowDownload,
                TimeoutSeconds = timeout,
                ExtraChromiumArgs = extraArgs
            };
        }
    }
}
