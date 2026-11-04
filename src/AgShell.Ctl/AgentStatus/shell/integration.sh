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
# agshell-agent-status — shell integration for bash and zsh.
# Sets the agshell session's agent-status indicator to `active` while a coding
# agent runs as a foreground command, and back to `idle` at the next prompt.
# Adapted from agterm's (MIT); `agshellctl install hooks` sources it from the rc files in WSL.
# Source this from your ~/.zshrc and/or ~/.bashrc.
#
# Which commands count as agents is a regex, override before sourcing to taste:
#   export AGSHELL_AGENT_RE='^(gemini|cursor-agent|my-agent)([[:space:]]|$)'
#
# Claude Code, Codex, Pi, and OpenCode are intentionally NOT in the default list —
# their own hooks/extensions/plugins drive finer per-turn state, which the coarse
# process-level active/idle here would only fight. Add any of them here if you
# rely on the shell integration alone for it.
#
# Every entry point is best-effort and a clean no-op outside agshell (guarded by
# $AGSHELL_SESSION_ID), so sourcing it from a non-agshell shell does nothing.

[ -n "${AGSHELL_SESSION_ID:-}" ] || return 0 2>/dev/null

# Locate agshell-agent-status.sh relative to this file (layout is preserved on
# install: shell/integration.sh sits next to agshell-agent-status.sh's dir).
if [ -n "${ZSH_VERSION:-}" ]; then
  _ags_self="${(%):-%x}"
  _ags_dir="${_ags_self:a:h:h}"
else
  _ags_self="${BASH_SOURCE[0]}"
  _ags_dir="$(cd "$(dirname "$_ags_self")/.." >/dev/null 2>&1 && pwd)"
fi
: "${AGSHELL_AGENT_BIN:=$_ags_dir/agshell-agent-status.sh}"
: "${AGSHELL_AGENT_RE:=^(gemini|cursor-agent|aider|crush|goose)([[:space:]]|$)}"

if [ -n "${ZSH_VERSION:-}" ]; then
  autoload -Uz add-zsh-hook
  # the redirect stays on the match, not the whole hook: an override valid as the PCRE fish and
  # RE_MATCH_PCRE zsh accept can be invalid here, and zsh prints the compile error once per prompt
  _ags_preexec() { [[ "$1" =~ $AGSHELL_AGENT_RE ]] 2>/dev/null && { "$AGSHELL_AGENT_BIN" active --blink; _ags_active=1; }; }
  _ags_precmd()  { [[ -n "${_ags_active:-}" ]] && { "$AGSHELL_AGENT_BIN" idle; unset _ags_active; }; }
  add-zsh-hook preexec _ags_preexec
  add-zsh-hook precmd  _ags_precmd

elif [ -n "${BASH_VERSION:-}" ]; then
  _ags_preexec() {
    local cmd="${1:-$BASH_COMMAND}"               # bash-preexec passes the command as $1; a raw DEBUG trap uses $BASH_COMMAND
    [ -n "${COMP_LINE:-}" ] && return             # ignore tab-completion
    case "$cmd" in _ags_*) return ;; esac
    if [[ "$cmd" =~ $AGSHELL_AGENT_RE ]] 2>/dev/null; then
      "$AGSHELL_AGENT_BIN" active --blink
      _ags_active=1
    fi
  }
  _ags_precmd() { [ -n "${_ags_active:-}" ] && { "$AGSHELL_AGENT_BIN" idle; unset _ags_active; }; }

  if [ -n "${bash_preexec_imported:-}${__bp_imported:-}" ]; then
    # bash-preexec is active: register into its additive arrays so we don't clobber other consumers
    case " ${preexec_functions[*]-} " in *" _ags_preexec "*) : ;; *) preexec_functions+=(_ags_preexec) ;; esac
    case " ${precmd_functions[*]-} "  in *" _ags_precmd "*)  : ;; *) precmd_functions+=(_ags_precmd)  ;; esac
  else
    # no bash-preexec: install the DEBUG trap only when nothing else owns it, so we never break a user's
    # existing preexec tooling. an unrelated existing trap → skip the active-state hook with a notice.
    case "$(trap -p DEBUG 2>/dev/null)" in
      "")             trap '_ags_preexec' DEBUG ;;   # nothing there: safe to install
      *_ags_preexec*) : ;;                            # already ours: idempotent
      *) printf '%s\n' "agshell agent-status: an existing bash DEBUG trap is set; skipping the active-state hook (use bash-preexec, or wire a preexec yourself)." >&2 ;;
    esac
    # precmd via PROMPT_COMMAND is additive and safe regardless of any DEBUG trap
    case "${PROMPT_COMMAND:-}" in
      *_ags_precmd*) : ;;
      *) PROMPT_COMMAND="_ags_precmd${PROMPT_COMMAND:+; $PROMPT_COMMAND}" ;;
    esac
  fi
fi
