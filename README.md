# cfagentbrowser

`cfagentbrowser` is a .NET 8 command-line app that drives a headless Chromium browser through PuppeteerSharp.

## Commands

- `new-session`
- `use-session [uuid]`
- `navigate [url]`
- `get-snapshot [width] [height] [tempfilename]`
- `send-click [width] [height] [x] [y]`
- `enter-text [text]`
- `quit`

## Build and run

```bash
dotnet restore
dotnet build

dotnet run
```

Type one command per line on stdin.

## Chromium configuration

The app can auto-detect Chromium/Chrome/Edge paths on Linux, Windows, and macOS, and also checks:

- `PUPPETEER_EXECUTABLE_PATH`
- `CHROME_PATH`

You can override behavior with these environment variables:

- `CFAGENT_CHROMIUM_PATH`: explicit browser executable path
- `CFAGENT_ALLOW_DOWNLOAD_CHROMIUM`: `true`/`false` (default `true`) to allow BrowserFetcher download fallback
- `CFAGENT_TIMEOUT_SECONDS`: launch timeout in seconds (default `30`)
- `CFAGENT_EXTRA_CHROMIUM_ARGS`: additional launch args separated by spaces
