# LEDERG LAUNCHER

Windows x64 launcher and installer for LEDERG STALMINE (Minecraft Java 1.12.2 / Forge).

The launcher downloads a public client ZIP, verifies its SHA-256, backs up conflicting client folders, and installs into the selected Minecraft game directory. It never packages or uploads account tokens, launcher profiles, saved worlds, or passwords.

## Build

Use .NET 8 SDK on Windows:

```powershell
dotnet test tests/LEDERG.Launcher.Tests/LEDERG.Launcher.Tests.csproj
dotnet publish src/LEDERG.Launcher/LEDERG.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

A GitHub Actions workflow builds the x64 EXE and attaches it to a GitHub Release when a `v*` tag is pushed.

## Client distribution

The client ZIP URL and SHA-256 are configured in `launcher.json` beside the EXE. A public direct-download URL to the ZIP is required; a Google Drive folder link is not a downloadable archive URL.
