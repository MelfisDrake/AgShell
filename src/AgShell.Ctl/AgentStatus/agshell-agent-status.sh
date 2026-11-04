#!/usr/bin/env sh
# MIT License
#
# Copyright (c) 2026 Umputun
# Copyright (c) 2026 MelfisDrake
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
# agshell-agent-status — set the current agshell session's agent-status indicator, adapted from agterm's
# agterm-agent-status.sh (MIT). Installed by `agshellctl install hooks` for the shell integration.
#
#   agshell-agent-status.sh active            # agent is busy
#   agshell-agent-status.sh idle              # clear the indicator
#
# The state and any further args go to `agshellctl hook`, which adds the session, the pane and its id.
# Outside agshell this is a silent no-op; it prints nothing and always exits 0.
#
# agshellctl resolution order: $AGSHELLCTL, the path the installer baked in, then agshellctl.exe on PATH.

[ -n "${AGSHELL_SESSION_ID:-}" ] || exit 0

ctl=${AGSHELLCTL:-__AGSHELLCTL__}
case "$ctl" in
  /*) [ -x "$ctl" ] || ctl=agshellctl.exe ;;
esac
"$ctl" hook "$@" >/dev/null 2>&1 || true
exit 0
