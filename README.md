# AgShell

English | [Русский](README.ru.md)

A Windows terminal built for working with several coding agents at once: sessions are grouped into named workspaces, each session shows the status of its agent (working / waiting / done), and everything the window can do is available through a local API and the `agshellctl` CLI, so agents can drive the terminal themselves.

AgShell is an independent Windows implementation in C# based on the model and command conventions of [umputun/agterm](https://github.com/umputun/agterm) (macOS, MIT), with selected adapted components, scripts and tests. AgShell is a separate project and name; it is not affiliated with or endorsed by agterm. See [third-party notices](THIRD-PARTY-NOTICES.md) for attribution.

**Status:** experimental 0.x software; public source is available at [MelfisDrake/AgShell](https://github.com/MelfisDrake/AgShell). The first public binary release is being prepared; there are no public binaries yet. Verified usage is limited to Windows 11 x64 at 96 DPI. Back up your state and save shell work before upgrading. There is no support SLA or promise of compatibility across all systems.

## Installation

Technical prerequisites: Windows x64 with ConPTY (Windows 10 1809+ API baseline), and the Microsoft Edge WebView2 Runtime. This is a technical baseline, not a Windows 10 support claim. The distributed self-contained build does not require a separate .NET SDK; building from source requires .NET SDK 10 and Node.js LTS.

- **Installer** `agshell-<version>-setup.exe` will be available on the [Releases page](https://github.com/MelfisDrake/AgShell/releases) after the first public binary release. Installs per user without administrator rights and adds `agshellctl` to PATH. Optionally installs the agent status hooks and the skill for Claude Code and Codex right away, and registers the AgShell event log (this one asks for administrator rights).
- **Portable** `agshell-<version>-win-x64.zip`: unpack anywhere and run `agshell.exe`. `agshellctl` on PATH, the hooks and the skill are installed from the command palette (Ctrl+Shift+P): “Add agshellctl to PATH”, “Install the agent status hooks”, “Install the AgShell skill for Claude Code and Codex”. The welcome window offers the same on first launch. Running them again is safe: “Add agshellctl to PATH” also takes other folders holding `agshellctl.exe` off the user’s PATH, and off the system PATH after a Windows administrator prompt (only when it has any; cancelling the prompt changes nothing), so an older copy is not found first.

When upgrading from published 0.3.0, save your work and back up your state. Close the old windows before explicitly stopping their shared `agshell.exe --host`; the installer cannot reliably replace the running legacy host. Its live processes do not transfer to 0.4.0. See the [live restoration notes](docs/agshellctl.md) for the preserved legacy pane ids.

Published 0.4.0 can remove web/themes before refusing to replace a busy file; close AgShell before rerunning that installer. The 0.4.1 installer fix restores the previous installation files after an ordinary refusal or cancellation and still removes outdated web/themes files on a successful upgrade. It needs temporary space for a copy of the existing installation. If restoration cannot finish, Setup reports the retained backup's location.

The skill can also be installed as a plugin from this repository (use only one of the two ways, otherwise it is unclear which copy the agent picks up):

```
claude plugin marketplace add MelfisDrake/AgShell
claude plugin install agshell@agshell

codex plugin marketplace add MelfisDrake/AgShell
codex plugin add agshell@agshell
```

The interface speaks Russian or English: Settings → General → Language; by default it follows Windows. `agshellctl` output is in English.

Future distributions must include `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses/` with the bundled components' license texts. Keep them with the portable files when redistributing them.

Builds are unsigned; SmartScreen may warn about an unknown publisher. Verify the download source and published asset digest before deciding to run it. Before uninstalling, remove the hooks from `~/.claude/settings.json` (and `~/.codex/config.toml`, the Pi extension and the OpenCode plugin, if installed), otherwise the agents will keep calling a missing `agshellctl`.

Uninstall removes the program, its shortcuts, Explorer entries and its directory from the user PATH. State, configuration, custom themes, hooks and skills are retained. Inactive daemon copies are removed; copies still in use remain under `%LOCALAPPDATA%\agshell\live` until they can be removed after their processes end.

## Release status and tested scope

This repository publishes source, tests and build instructions. The first public binary release is being prepared separately; the Releases page currently has no downloadable binaries. Earlier versions mentioned in upgrade notes describe historical compatibility behavior and are not public downloads from this repository.

Windows 10, other or mixed DPI, IME, accessibility, text scaling, high contrast, multiple monitors, sleep/logoff/reboot and the full graphics/transparency matrix remain unverified. Transparency without blur has known limitations. Earlier upgrade precautions above still apply. Compatible live daemons can preserve shells; incompatible builds require an explicit restart and process survival is not guaranteed.

## Bugs, contributions and security

Use [CONTRIBUTING.md](CONTRIBUTING.md) for bug reports and small contributions, and [SECURITY.md](SECURITY.md) for private security reporting. Remove private terminal content, credentials and machine identifiers from attachments. Review of reports and fixes is best effort.

## Documentation

- [docs/agshellctl.md](docs/agshellctl.md) — reference for `agshellctl` and what the window can do
- [docs/roadmap.md](docs/roadmap.md) — work stages (in Russian)
- [docs/release.md](docs/release.md) — how to build and publish a release (in Russian)
- [docs/decisions/](docs/decisions/) — decisions taken and why (in Russian)
- [AGENTS.md](AGENTS.md) — rules for agents and developers (in Russian)

The shared title bar uses a native WindowChrome frame with workspace/session/window identity and the toolbar actions: recent sessions, attention, scratch, split, overlays, dashboard, Quick Terminal and custom commands. Recent, attention and custom commands open themed keyboard-accessible popovers. Normal and compact keep system caption buttons; Hidden keeps a thin drag strip (native top frame plus 3 DIP in a normal window). Interface visibility settings and existing menu, API and keymap actions still apply.

## License

Own contributions: [MIT](LICENSE), Copyright (c) 2026 MelfisDrake. Upstream copyrights and license terms remain in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), including the adapted agterm icon. No Microsoft or upstream endorsement is claimed.

Before a first source build, prepare the native VT with `scripts/build-agshell-vt.ps1` using the pinned Zig version; then run `dotnet build agshell.slnx` and `dotnet test agshell.slnx`. See [build prerequisites](docs/release.md).
