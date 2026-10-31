# LEDERG LAUNCHER

Windows x64 standalone launcher and installer for LEDERG STALMINE (Minecraft Java 1.12.2 / Forge).

## What it does

- Downloads the LEDERG client ZIP from Google Drive.
- Verifies the archive SHA-256 before extracting it.
- Backs up existing game folders before updating the client.
- Launches Forge 1.12.2 directly using the bundled Java 8 runtime and the local Forge libraries. Missing or damaged libraries are downloaded from official HTTPS Maven repositories and checked against their SHA-1 hashes.
- Does not open TLauncher or the official Minecraft Launcher.

## Account mode

The current built-in launch mode is an **offline profile**: enter a Minecraft nickname in the LEDERG window and start the game. No Microsoft password, launcher token, or external launcher is requested or stored.

Offline mode is suitable for local play and servers configured with `online-mode=false`. Servers configured with `online-mode=true` require valid Microsoft/Minecraft account authentication; this version does not implement that authentication and will not bypass it.

## Build

Use the .NET 8 SDK on Windows:

```powershell
dotnet test tests/LEDERG.Launcher.Tests/LEDERG.Launcher.Tests.csproj -c Release
dotnet publish src/LEDERG.Launcher/LEDERG.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

GitHub Actions runs the tests and publishes the Windows x64 executable and ZIP to the GitHub Release.

## Client distribution

The client ZIP URL and SHA-256 are embedded in the executable, so a separate `launcher.json` file is not required.
