# Palisades

WPF .NET 8 desktop app — declutter Windows desktop with icon containers, gadgets, themes.

## Release process

1. Update version in `Palisades.Application/Palisades.Application.csproj` (`<Version>` tag)
2. Commit, tag, push:
   ```
   git add Palisades.Application/Palisades.Application.csproj
   git commit -m "Bump to X.Y.Z"
   git tag vX.Y.Z
   git push origin main --tags
   ```
3. CI builds setup.exe + portable.zip, creates GitHub Release automatically
   - Trigger: tag push matching `v*`
   - Portable: self-contained (`win-x64`)
   - Installer: framework-dependent (lighter), Inno Setup
   - Version auto-detected from built binary by Inno Setup

## Key commands

- `dotnet build -c Release` — build
- `taskkill //F //IM Palisades.exe` — kill running process before rebuild
- `dotnet run --project Palisades.Application/Palisades.Application.csproj -c Release` — launch

## Architecture

- `DesktopOverlayWindow` — full-screen transparent overlay behind desktop icons, hosts containers
- `ContainerControl` — per-container WPF UserControl rendered in overlay
- `ArcticShelterWindow` — dashboard/config GUI
- `MainViewModel` — central VM, owns containers collection
- `ThemeService` — theme presets + settings (singleton)
- `ContainerManager` — container persistence (singleton)
- App auto-starts with `--autostart` flag (from registry Run key), hides dashboard
- Tray click shows/recreates ArcticShelterWindow
