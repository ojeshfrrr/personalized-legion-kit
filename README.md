# Project Legion

Project Legion is an unofficial independent derivative of Lenovo Legion Toolkit. It is not affiliated with or endorsed by Lenovo or the original maintainers.

This fork reuses the existing C# and WPF implementation, including its device detection, hardware interfaces, controls, automation, tray behavior, and configuration model. Compatibility remains governed by the upstream implementation; this fork does not claim support for unverified devices.

## Build

Use the .NET 9 SDK on Windows. Restore and build the WPF project from the repository root:

```powershell
dotnet restore LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj -p:Platform=x64
dotnet build LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj -c Release -p:Platform=x64 --no-restore -m:1
```

To run the application from source:

```powershell
dotnet run --project LenovoLegionToolkit.WPF/LenovoLegionToolkit.WPF.csproj -c Release -p:Platform=x64
```

`make.bat` also publishes the app and creates an Inno Setup installer; it requires Inno Setup 6. The app targets .NET 9 and its release manifest requests administrator access for hardware controls. UIAccess is disabled because this fork has no owner-provided trusted signing certificate. A build signed with your own trusted certificate can opt into `app.uiaccess.manifest` with `-p:ProjectLegionUiAccess=true`.

The LampArray MSIX project has a Project Legion package identity and signing is disabled by default. The installer does not install or trust a certificate; the Windows Dynamic Lighting extension was not packaged in this baseline and requires a package signed by a certificate you control.

## Project-specific settings

- Edit product name and the optional GitHub update owner/repository and expected signer thumbprint in `LenovoLegionToolkit.Lib/ProjectBranding.cs`. Updates stay disabled until all three values are configured.
- Edit the charcoal shell background in `LenovoLegionToolkit.WPF/Branding.xaml` and the default ice-blue accent in `LenovoLegionToolkit.WPF/Utils/ThemeManager.cs`.
- Replace the generated image assets with your own when ready. The app's data is stored under `%LOCALAPPDATA%\ProjectLegion`; on first launch, top-level JSON settings are copied from the upstream folder without overwriting or deleting the original data.

## License and attribution

The checked-out upstream revision is licensed under GPL-3.0. This derivative is distributed under the same license; see [LICENSE](LICENSE) and [NOTICE](NOTICE). The original work is by Bartosz Cichecki, Kaguya, and Dr. Skinner. Third-party copyright and license notices are retained in the source.

Original source: [Lenovo Legion Toolkit](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit).
