# MIT License
#
# Copyright (c) 2026 Umputun
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.
# agshell-agent-status — shell integration for fish.
# Sets the agshell session's agent-status indicator to `active` while a coding
# agent runs as a foreground command, and back to `idle` at the next prompt.
# Adapted from agterm's (MIT); `agshellctl install hooks` sources it from the rc files in WSL.
# Source this from your ~/.config/fish/config.fish.
#
# Which commands count as agents is a regex, override before sourcing to taste:
#   set -gx AGSHELL_AGENT_RE '^(gemini|cursor-agent|my-agent)([[:space:]]|$)'
#
# Claude Code, Codex, Pi, and OpenCode are intentionally NOT in the default list —
# their own hooks/extensions/plugins drive finer per-turn state, which the coarse
# process-level active/idle here would only fight. Add any of them here if you
# rely on the shell integration alone for it.
#
# Every entry point is best-effort and a clean no-op outside agshell (guarded by
# $AGSHELL_SESSION_ID), so sourcing it from a non-agshell shell does nothing.

if not set -q AGSHELL_SESSION_ID
    return 0 2>/dev/null
end

# Locate agshell-agent-status.sh relative to this file
set -l _ags_dir (dirname (dirname (status filename)))
if not set -q AGSHELL_AGENT_BIN
    set -g AGSHELL_AGENT_BIN "$_ags_dir/agshell-agent-status.sh"
end

# exported, unlike the shell-local state below: `agshellctl hook claude` reads an overridden value from
# its process environment, and a `set -g` would keep it invisible outside this shell. BOTH branches
# export — the one carrying an override set before sourcing (the value the adapter needs, and the
# form this file documented as `set -g` until now) as much as the default
if set -q AGSHELL_AGENT_RE
    set -gx AGSHELL_AGENT_RE $AGSHELL_AGENT_RE
else
    set -gx AGSHELL_AGENT_RE '^(gemini|cursor-agent|aider|crush|goose)([[:space:]]|$)'
end

function _ags_preexec --on-event fish_preexec
    if string match -r -q $AGSHELL_AGENT_RE $argv[1]
        $AGSHELL_AGENT_BIN active --blink
        set -g _ags_active 1
    end
end

function _ags_precmd --on-event fish_prompt
    if set -q _ags_active
        $AGSHELL_AGENT_BIN idle
        set -e _ags_active
    end
end
