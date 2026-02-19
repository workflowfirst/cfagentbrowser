# cfagentbrowser

`cfagentbrowser` is a .NET 8 command-line app that drives a headless Chromium browser through Playwright.

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
pwsh bin/Debug/net8.0/playwright.ps1 install chromium

dotnet run
```

Type one command per line on stdin.
