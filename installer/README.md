# BIMCamel installer

`BIMCamel.Installer/` is a small WPF app that produces **BIMCamelSetup.exe** — the same graphical
installer (and black-and-white BIMCamel UI) as the sibling
[Dyncamelo](https://github.com/mrshoma99-rgb/dyncamelo) plug-in, so every BIMCamel tool installs the
same way. It replaced the earlier Inno Setup script (`BIMCamel.iss`, removed — see git history).

| Mode | What it does |
|---|---|
| double-click | Installs **per user** (no admin, no UAC) into `%AppData%\Autodesk\ApplicationPlugins\BIMCamel.bundle`, registers an Apps & Features entry, and upgrades/removes any previous install — including one made by the old Inno `BIMCamel_Setup.exe`. |
| `/uninstall` | Opens straight on the removal screen (this is what Apps & Features runs). |
| `/silent`, `/uninstall /silent` | Same actions with no window, for scripted deployment. |

> **Per-user only.** Earlier builds offered an "all users / just me" choice. The machine-wide
> (`%ProgramData%`) path proved unreliable — Navisworks would finish installing but not show the
> plug-in — so everything installs into the current user's profile, the location Navisworks
> reliably auto-loads.

The bundle ships **all four year folders** (2024 / 2025 / 2026 / 2027, Manage + Simulate);
`PackageContents.xml` points each Navisworks release at its matching per-year DLL, so nothing needs
selecting at install time. Because the files are written by the installer (not extracted by the
browser), they carry no Mark-of-the-Web — the `PLUGIN_LOAD_02` / `0x80131515` blocked-DLL failure
cannot happen with this install path.

## Payload

The Navisworks bundle ships **embedded in the exe** as a zip. The release workflow builds it:

```powershell
dotnet build installer/BIMCamel.Installer -c Release -p:BundlePayload=<abs path to payload.zip> -p:Version=<x.y.z>
```

where `payload.zip` contains a single root folder `BIMCamel.bundle/` with one plug-in DLL per
Navisworks year. Without `BundlePayload` the exe still compiles; at run time it falls back to a
`BIMCamel.bundle` folder sitting next to it (the layout inside the release zip).

## Releasing

Nothing to run locally: commit the new tag (e.g. `v0.6.0`) to `dist/RELEASE_VERSION` and push.
`.github/workflows/release.yml` builds the per-year DLLs, stages the bundle, embeds it into
`BIMCamelSetup.exe`, and publishes the GitHub release — identical flow to Dyncamelo's.
