# Security reporting — preparation

English | [Русский](SECURITY.ru.md)

**Public-opening blocker:** a working private reporting channel has not been verified. This document is a preparation draft. Do not post vulnerability details, credentials or exploit attachments in public issues. The owner must enable and verify GitHub private vulnerability reporting before public opening, or provide another verified private contact; this audit does not change that setting. No private advisory endpoint or email is promised yet.

## Scope

AgShell is experimental 0.x software. There is no guaranteed security maintenance period, response deadline or backport policy. Report the version and distribution source, impact, minimal reproduction and prerequisites, with sanitized evidence. Retain sensitive details for a verified private channel.

The CLI and local control pipe can read terminals and run commands as the user. Other processes under the same user identity are not isolated from that authority. Hooks, plugins, custom commands, SSH targets and local HTML overlays must be trusted. Local overlay buttons can invoke AgShell commands even without page JavaScript; disabling JavaScript is not a sandbox for untrusted HTML. A browser overlay and its persistent profile may hold sensitive web sessions. Do not run AgShell elevated merely to use the terminal.

Pending owner action after creating the chosen public repository: Settings → Security and quality → Advanced Security → Private vulnerability reporting → Enable; verify the “Report a vulnerability” form from a non-owner account. Only then replace this preparation text with the verified channel instructions.
