# Development Environment

## WSL build environment

This solution targets a Windows WPF desktop app (`PlcSoftware.App`, `net8.0-windows`, `UseWPF`), but local development happens on **WSL (Ubuntu 24.04 x64)**. Cross-building the WPF project requires a Windows Desktop SDK component that the stock distro SDK does not ship.

### Requirement: use an official full .NET SDK

The distro SDK (`/usr/lib/dotnet`, e.g. .NET 8.0.130) does not include the `Microsoft.NET.Sdk.WindowsDesktop` MSBuild targets, so any WPF cross-build fails with:

```
error MSB4019: The imported project ".../Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.targets" was not found.
```

`EnableWindowsTargeting=true` only pulls the `Microsoft.WindowsDesktop.App.Ref` reference pack; it does **not** supply the missing SDK `.targets`/`.props`. There is no Linux workload for WindowsDesktop and no compatible NuGet `Microsoft.NET.Sdk.WindowsDesktop` package for .NET 8.

**Fix:** install an official full `8.0.x` SDK (e.g. `8.0.424`) via the Microsoft install script into `~/.dotnet-ms`:

```bash
# Download and run the official installer (full SDK, includes WindowsDesktop targets)
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 8.0.424 --install-dir "$HOME/.dotnet-ms"
```

### Build prerequisite (run in every shell that builds WPF)

```bash
export DOTNET_ROOT=$HOME/.dotnet-ms PATH=$HOME/.dotnet-ms:$PATH
```

### Verify

```bash
dotnet build PlcSoftware.sln -c Release -p:EnableWindowsTargeting=true
```

Note: this produces a WPF binary that is **Windows-only** — it can be cross-compiled here but only **executed** on Windows. In CI, full build/test/publish for `win-x64` is performed on GitHub Actions `windows-latest` (planned; not yet wired up).

## Decisions

### Version pinning policy

NuGet package versions are pinned to **.NET 8-compatible lines** in `Directory.Packages.props` (central package management), rather than whatever a bare `dotnet add package <id>` resolves to on this host. In this 2026-era environment, "latest stable" resolves to .NET 10-era packages (e.g. `Microsoft.Extensions.Hosting 10.0.x`, `System.IO.Ports 10.0.x`, `Microsoft.Data.Sqlite 10.0.x`, `Microsoft.NET.Test.Sdk 18.x`, `xunit.runner.visualstudio 4.x`, `coverlet.collector 10.x`), which would be incompatible with the pinned .NET 8 target.

**Future tasks that add packages MUST select .NET 8-compatible versions** and add them as explicit pinned `PackageVersion` entries in `Directory.Packages.props`, not accept the host-resolved latest.
