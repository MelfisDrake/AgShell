---
name: agshell
description: >
  Drive AgShell, a Windows terminal for coding agents, via agshellctl. Use inside an AgShell session to
  create, rename, move, select or close sessions and workspaces; type into another session and read
  its screen back; start worker sessions and wait until their agent is done or blocked; set this
  session's agent status; post a desktop notification; follow status and lifecycle events.
when_to_use: >
  Trigger on: AgShell, agshellctl, AGSHELL_SESSION_ID, and, from inside an AgShell session, requests such as
  open a new session/tab for X, run this in another session, start a worker and tell me when it is done,
  read what that session printed, rename this session, notify me when finished.
allowed-tools: Bash(agshellctl *), Bash(agshellctl.exe *), PowerShell(agshellctl *)
---

<!-- agshell-skill -->

# Driving AgShell

AgShell is a Windows terminal organised as workspaces holding sessions (one shell each: PowerShell, cmd or
WSL). It exposes a control pipe driven by the CLI `agshellctl`, modelled on agterm's `agtermctl`.

## Am I inside AgShell?

Every shell AgShell starts has:

- `AGSHELL_ENABLED=1` — this shell runs inside agshell. Check it before using agshellctl.
- `AGSHELL_SESSION_ID` — this session's id. Pass it as `--target` to act on your own session.
- `AGSHELL_WORKSPACE_ID` — the workspace the shell started in (not updated if the session moves; read `tree`).
- `AGSHELL_PIPE` — the control pipe; agshellctl picks it up automatically.
- `AGSHELL_PANE` — which terminal of the session this shell is: `left` (main), `right` (split) or `scratch`.

`agshellctl.exe` is on PATH in every session. Inside WSL call it as `agshellctl.exe`.

## Addressing

`--target`, `--workspace`, `--after`, `--before` take an id, a unique id prefix, or `active`
(the selected session / the workspace a new session lands in). Names never match. `--target` defaults to
`active`, which is whatever the user has selected — NOT your session — so always pass
`--target $env:AGSHELL_SESSION_ID` (PowerShell) or `--target "$AGSHELL_SESSION_ID"` (bash) for your own session.

Create commands print the new id. Errors go to stderr as `error: ...` with exit code 1. `--json` prints the raw
response. `agshellctl tree` lists workspaces and sessions with ids, shells, directories, statuses (`*` = active).

## Commands

```
agshellctl tree [--json]
agshellctl workspace new [name] | rename <name> | delete | select | collapse | expand  [--target W]
agshellctl workspace go --to next|prev ; workspace move --to up|down|top|bottom
agshellctl session new [--cwd DIR] [--name N] [--shell pwsh|powershell|cmd|wsl:<distro>] [--command "PROGRAM ARGS" [--wait]]   # --command: no shell; the session closes when it exits
                     [--workspace W | --workspace-name NAME [--create-workspace] | --after S | --before S] [--no-select]
agshellctl session duplicate | select | reveal | seen  [--target S]
agshellctl session restart --pane left|right | --pane-id ID [--command "LINE"] [--target S]   # new shell in the pane; no --command re-runs its program
agshellctl session close [--target S]...            # repeat --target for a batch
agshellctl session rename <name> [--target S]       # empty name restores the directory name
agshellctl session move <workspace> | --after S | --before S | --to up|down|top|bottom  [--target S]...
agshellctl session go --to next|prev|first|last|next-attention|prev-attention
agshellctl session type "<text>" [--target S]       # every newline is Enter; or --stdin
agshellctl session text [--lines N | --all] [--pane P | --pane-id $AGSHELL_PANE_ID] [--target S]
agshellctl session copy | select-all [--target S] ; session paste [--pane P] [--target S] ; clipboard set TEXT   # selection and clipboard
agshellctl session status idle|active|completed|blocked [--blink] [--auto-reset] [--sound NAME] [--color #rrggbb] [--shape circle|square|triangle|diamond|capsule|star] [--target S]
agshellctl window new [NAME] | list | select|close|delete [W] | rename W NAME | go --to next|prev   # windows; any command takes --window W
agshellctl session context "TEXT" | --clear [--target S]   # what the session is for (a PR, a task), shown in the title bar
agshellctl session background image PATH | text WORD | color #rrggbb | clear [--pane left|right|scratch] [--opacity F]   # behind the terminal; only when asked
printf '%s\n' a b | agshellctl pick [--prompt T] [--allow-custom]   # the user picks one; prints {"result":"picked","id":…} (exit 0) or cancelled (exit 2)
agshellctl ask "TITLE" --button yes=Yes --button no=No [--input line] [--choice ID=LABEL]... [--style gui]   # ask the user; prints {"result":"answered","id":…} (0), escaped (3), cancelled (2)
agshellctl session wait --until completed --until blocked [--timeout SEC] [--target S]
agshellctl session split [on|off|toggle] [--axis vertical|horizontal] ; session split close ; session swap ; session lead [--pane left|right]  # take a live pane's lead (its size) back
agshellctl session focus left|right|other ; session resize --split-ratio 0.6 ; session scratch [on|off|toggle]
agshellctl surface zoom [show|hide|toggle]
agshellctl session overlay open "<program>" [--size-percent N] [--wait | --block] [--follow] [--pane left|right]
agshellctl session overlay open --html FILE [--cwd DIR] [--js] [--navigation] [--block] | --url URL [--browse] ; overlay close|result [--page ID]|text|copy|resize|reload [--current]|navigate back|forward|browser|finder|submit --value V   # open refuses a busy slot
agshellctl session hud [open] "<message>" | --file F [--detail T] [--spinner] [--position top-right] [--hide-after SEC] [--pane left|right] ; hud update ; hud close   # one HUD per session; refused over a running overlay
agshellctl quick [show|hide|toggle] ; quick type ; quick text
agshellctl dashboard S1 S2[:left|:right] … [--auto-size | --font-size N] | --mru | --close   # view-only grid of live panes
agshellctl session search "<needle>" | --next | --prev | --close [--target S]   # prints "N of M"
agshellctl font inc|dec|reset [--target S]
agshellctl notify "<body>" [--title T] [--target S]
agshellctl events [--kind status,notify,session.created,session.closed,session.selected,pane.split,pane.scratch,tree.changed,app.launched,remote.opened,remote.closed] [--json]
agshellctl keymap reload | list | run "<name>" [--target S]   # keymap.conf: key bindings and the user's own commands
agshellctl hooks reload | list                                # hooks.conf: shell lines run on AgShell events
agshellctl session flag [on|off|toggle|clear] [--target S]... ; sidebar mode tree|flagged|toggle ; sidebar flagged-layout flat|plain|tree
agshellctl workspace focus [on|off|toggle|add] [--target W] ; workspace filter on|off|toggle   # the user's view; change only when asked
agshellctl restore mode [none|rerun|live] ; live list | prune | reset --force | tree [HOST] | attach HOST ID  # how sessions come back on launch, and sessions of another machine; change only when asked
agshellctl restore capture | clear ; session restore "CMD" | --none | --clear [--pane left|right]   # rerun: capture now, forget, pin what a pane re-runs
agshellctl recent list | open [ID] | clear                    # recently closed sessions/workspaces, reopened with fresh shells
agshellctl theme list ; theme set NAME [--dark NAME|none]     # terminal color theme (Ghostty format); change only when asked
agshellctl sidebar visibility show|hide|toggle ; sidebar width <px> ; sidebar expand | collapse
```

Notes:

- `keymap list --json` separates each action's menu `chord` from `alternates`; `menu` reads the applied menu,
  including dispatch ids in `selector` and disabled entries (`enabled: false`). Compare both when diagnosing shortcuts.
- `hooks.list` and `hooks.reload` are app-wide and refuse target/window before acting; unchanged hooks keep their queues
  and run/failure counters when reloaded.
- `session new` without `--cwd` opens in the home directory; use `--no-select` so the user stays where they are.
  For WSL pass a Linux path (`--cwd /home/me/src`).
- `session type` sends text to the shell; a trailing newline submits it. In PowerShell write `` "dir`n" ``,
  in bash `$'make test\n'`. Typing into a blocked or completed session clears its status.
- `session text` returns what the terminal shows: the visible screen by default, `--lines N` for the last N lines
  including scrollback, `--all` for everything. Full-screen programs (vim, htop, an agent's TUI) have no
  scrollback, so only their current screen is readable.
- A session can hold a split (a second shell, `right`) and a scratch shell. `type`, `text` and `search`
  take `--pane left|right|scratch`; `type` defaults to the main pane, the others to the focused one.
- An overlay runs one program over a session and disappears when it exits; the program is started as is, so wrap shell
  syntax (`pwsh -c "..."`, `sh -c '...'`). `--block` returns the program's exit status; `overlay result` errors with
  `overlay still running` while it runs and `no overlay result` when it left none. A HUD is a passive message panel;
  use it to show progress without taking the user's focus (`--markdown` for formatted text).
- `--html FILE` shows a local page, such as a report you generated, over the session. Its own JavaScript is off unless
  `--js`; buttons and forms tagged `data-agshell="<command>"` (with `data-agshell-args` JSON, `data-agshell-target`,
  `data-agshell-into`; a tagged button outside a tagged form needs `type="button"`) still run AgShell commands. Every page
  gets the terminal theme as `--agshell-background`, `--agshell-foreground` and `--agshell-color-0..15`; a local page also
  takes the theme's text color and scheme by default. Use them with a fallback: `var(--agshell-background, Canvas)`. With `--js` the page gets `agshell.request(cmd, {target, args})`. A page
  answers the caller with `session.overlay.submit` and `--block` prints `{"pageID":...,"outcome":"submitted","value":"..."}` (exit 0) or
  dismissed (exit 2). `--url` shows any web page, such as a dev server, as a real browser tab: logins work while it is
  open, and `--persistent` keeps them (`agshellctl browser clear` forgets them).
- `session status --pane right` tags which pane's agent reports; the hook forwards `$AGSHELL_PANE` for you.
- `session wait` exits 0 printing the matched status, 2 if the session closed, 124 on timeout.
- A row with `remoteHost` in `tree --json` is a session of another machine attached here (`live attach`): its panes run
  there, `session restart` refuses it, and a pane another window or machine leads refuses scripts with
  `pane is in use on the machine it runs on` (or `pane is covered while another machine leads it`) until `session lead`
  takes it back. Change leads only when asked.
- Statuses show as a coloured dot on the session's row: active (grey-violet), blocked (amber), completed (green).
  `--auto-reset` clears it when the user visits the session. Claude Code, Codex, Pi and OpenCode report their own status through hooks
  (`agshellctl install hooks`), so do not set your own status unless asked.
- Start interactive Codex with `--no-daemon`, keeping the user's launcher (for example, `codex-proxy`).
  Codex's shared app-server can launch hooks with another tab's environment, sending statuses to that tab.
  The flag keeps hooks in this tab's process; hook trust still needs review through `/hooks`.
  That running conversation cannot be controlled through the shared server from Codex desktop/mobile.
  Do not stop the shared server: other sessions may depend on it.

## Recipes

Run a command in a background session and read the result (PowerShell):

```powershell
$id = agshellctl session new --no-select --cwd C:\src\app --name tests
agshellctl session type --target $id "dotnet test`n"
Start-Sleep 20
agshellctl session text --target $id --lines 40
```

Start a worker agent and wait for it (bash):

```bash
id=$(agshellctl.exe session new --no-select --cwd "$PWD" --name worker)
agshellctl.exe session type --target "$id" $'claude -p "fix the failing test" \n'
agshellctl.exe session wait --target "$id" --until completed --until blocked --timeout 1800
agshellctl.exe session text --target "$id" --lines 60
```

Tell the user something needs them:

```
agshellctl notify "Deploy finished, check the logs" --target $env:AGSHELL_SESSION_ID
```
