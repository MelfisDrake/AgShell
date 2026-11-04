// Portions adapted from umputun/agterm e0b4844.
// Copyright (c) 2026 Umputun. MIT; see licenses/agterm.txt.
namespace AgShell.Core
{
    /// <summary>What an agent in a session is doing, as reported by its hooks. Not persisted.</summary>
    public enum AgentStatus
    {
        Idle,
        Active,
        Completed,
        Blocked,
    }

    public enum StatusKeystroke
    {
        Other,
        Submit,
        Interrupt,
    }

    public static class AgentStatuses
    {
        /// <summary>agterm's status glyph silhouettes; circle is the default.</summary>
        public static readonly IReadOnlyList<string> Shapes = AgShell.Protocol.ControlStatusNames.Shapes;

        public static string Name(AgentStatus status)
        {
            return status.ToString().ToLowerInvariant();
        }

        public static bool TryParse(string? value, out AgentStatus status)
        {
            switch (value)
            {
                case "idle":
                    status = AgentStatus.Idle;
                    return true;
                case "active":
                    status = AgentStatus.Active;
                    return true;
                case "completed":
                    status = AgentStatus.Completed;
                    return true;
                case "blocked":
                    status = AgentStatus.Blocked;
                    return true;
                default:
                    status = AgentStatus.Idle;
                    return false;
            }
        }

        public static bool NeedsAttention(AgentStatus status)
        {
            return status == AgentStatus.Blocked || status == AgentStatus.Completed;
        }

        /// <summary>
        /// Whether a keystroke answers the status, by agterm's rule: blocked and completed clear as the reset setting
        /// says (any key, a submit, or never; under submit-only an interrupt also clears blocked, since cancelling a
        /// prompt fires no hook), active clears only on an interrupt.
        /// </summary>
        public static bool ClearedBy(AgentStatus status, StatusKeystroke keystroke, string reset = "firstKey")
        {
            return status switch
            {
                AgentStatus.Blocked or AgentStatus.Completed => reset switch
                {
                    "enter" => keystroke == StatusKeystroke.Submit || (status == AgentStatus.Blocked && keystroke == StatusKeystroke.Interrupt),
                    "never" => false,
                    _ => true,
                },
                AgentStatus.Active => keystroke == StatusKeystroke.Interrupt,
                _ => false,
            };
        }

        /// <summary>
        /// agterm's InterruptKeystroke.classify, for a key press as the page saw it (KeyboardEvent key and code): Escape
        /// interrupts under any modifiers, and so does a bare Ctrl+C, matched by its letter or, on a non-Latin layout, by the
        /// C key; Enter or keypad Enter with no modifier submits, so Shift+Enter and Alt+Enter, which add a line in Claude
        /// Code and Codex, stay typing.
        /// </summary>
        public static StatusKeystroke ClassifyKey(string key, string code, bool ctrl, bool alt, bool shift, bool meta)
        {
            if (code == "Escape")
            {
                return StatusKeystroke.Interrupt;
            }

            if (ctrl && !alt && !shift && !meta)
            {
                string letter = key.ToLowerInvariant();
                bool latin = letter.Length == 1 && letter[0] is >= 'a' and <= 'z';
                if (letter == "c" || (code == "KeyC" && !latin))
                {
                    return StatusKeystroke.Interrupt;
                }
            }

            return code is "Enter" or "NumpadEnter" && !ctrl && !alt && !shift && !meta ? StatusKeystroke.Submit : StatusKeystroke.Other;
        }

        /// <summary>agterm's classify(text): typed text (session type) submits with a line break in it, and never interrupts.</summary>
        public static StatusKeystroke ClassifyTyped(string text)
        {
            return text.Contains('\r') || text.Contains('\n') ? StatusKeystroke.Submit : StatusKeystroke.Other;
        }
    }
}
