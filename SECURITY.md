# Security reporting

English | [Русский](SECURITY.ru.md)

Use [Security → Advisories → Report a vulnerability](https://github.com/MelfisDrake/AgShell/security/advisories/new) to send a private vulnerability report through GitHub. Sign in to GitHub to use the form. Do not post vulnerability details, credentials or exploit attachments in public issues.

## Scope

AgShell is experimental 0.x software. There is no guaranteed security maintenance period, response deadline or backport policy. Report the version and distribution source, impact, minimal reproduction and prerequisites, with sanitized evidence. Retain sensitive details for a verified private channel.

The CLI and local control pipe can read terminals and run commands as the user. Other processes under the same user identity are not isolated from that authority. Hooks, plugins, custom commands, SSH targets and local HTML overlays must be trusted. Local overlay buttons can invoke AgShell commands even without page JavaScript; disabling JavaScript is not a sandbox for untrusted HTML. A browser overlay and its persistent profile may hold sensitive web sessions. Do not run AgShell elevated merely to use the terminal.
