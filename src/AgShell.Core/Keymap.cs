using System.Text;
using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>A key with modifiers; the key is the browser's physical key code (KeyA, Digit1, ArrowUp, F5, …).</summary>
    public sealed record KeyChord(bool Ctrl, bool Alt, bool Shift, string Code)
    {
        public bool IsFunctionKey => Code.Length > 1 && Code[0] == 'F' && Code.Skip(1).All(char.IsAsciiDigit);

        public bool Meta { get; init; }

        public bool HasModifier => Ctrl || Alt || Shift || Meta;

        public override string ToString()
        {
            StringBuilder text = new StringBuilder();
            text.Append(Ctrl ? "ctrl+" : string.Empty).Append(Meta ? "super+" : string.Empty)
                .Append(Alt ? "alt+" : string.Empty).Append(Shift ? "shift+" : string.Empty);
            return text.Append(Keymap.KeyName(Code)).ToString();
        }
    }

    /// <summary>One way to fire a binding: a chord, or a leader sequence such as ctrl+a>g.</summary>
    public sealed record KeyBinding(IReadOnlyList<KeyChord> Sequence, bool Repeat)
    {
        public override string ToString()
        {
            return string.Join(">", Sequence) + (Repeat ? " --repeat" : string.Empty);
        }

        public bool SameKeys(KeyBinding other)
        {
            return Sequence.SequenceEqual(other.Sequence);
        }

        /// <summary>One sequence starts the other, so the shorter would always fire first.</summary>
        public bool Overlaps(KeyBinding other)
        {
            int shorter = Math.Min(Sequence.Count, other.Sequence.Count);
            return Sequence.Take(shorter).SequenceEqual(other.Sequence.Take(shorter));
        }
    }

    /// <summary>A shell line bound to keys and listed in the palettes.</summary>
    public sealed record CustomCommand(string Name, IReadOnlyList<KeyBinding> Bindings, string Shell, bool ErrorHud, string? ErrorPosition, string? ErrorPane)
    {
        /// <summary>The kept bindings as written in keymap.conf, joined by '|', as agterm shows them; empty for a palette-only command.</summary>
        public string Shortcut { get; init; } = string.Empty;
    }

    public sealed class KeymapResult
    {
        /// <summary>Every bindable action with the bindings it ended up with; an empty list is an action without keys.</summary>
        public Dictionary<string, List<KeyBinding>> Actions { get; } = new Dictionary<string, List<KeyBinding>>();

        public Dictionary<string, KeyChord?> MenuChords { get; } = new Dictionary<string, KeyChord?>();

        public List<CustomCommand> Commands { get; } = new List<CustomCommand>();

        public List<ControlKeymapAction> ActionReadback()
        {
            return Keymap.Defaults.Select(pair =>
            {
                KeyChord? menu = MenuChords.GetValueOrDefault(pair.Key);
                KeyChord? shipped = pair.Value is string text ? Keymap.ParseBindings(text, false)[0].Sequence[0] : null;
                List<KeyBinding> alternatives = Actions[pair.Key].Where(binding => menu is null || binding.Sequence.Count != 1 || binding.Sequence[0] != menu).ToList();
                return new ControlKeymapAction
                {
                    Action = pair.Key,
                    Chord = menu?.ToString(),
                    Alternates = alternatives.Count > 0 ? alternatives.Select(binding => string.Join(">", binding.Sequence)).ToList() : null,
                    Overridden = menu != shipped ? true : null,
                    Repeats = alternatives.Any(binding => binding.Repeat) ? true : null,
                };
            }).ToList();
        }

        public KeyChord? GlobalHotkey { get; set; }

        public List<string> Diagnostics { get; } = new List<string>();
    }

    /// <summary>
    /// Reads keymap.conf, agterm's kitty-flavored format: map rebinds a built-in action, command binds a shell line,
    /// global-hotkey takes one system-wide chord. A bad line never spoils the rest; it becomes a diagnostic.
    /// </summary>
    public static class Keymap
    {
        /// <summary>The built-in actions agshell has, by agterm's names, with the chords they ship with.</summary>
        public static readonly IReadOnlyDictionary<string, string?> Defaults = new Dictionary<string, string?>
        {
            // agterm's ⌘⌥N.
            ["new_window"] = "ctrl+alt+n",
            ["rename_window"] = null,
            ["delete_window"] = null,
            ["previous_window"] = null,
            ["next_window"] = null,
            ["new_workspace"] = "ctrl+shift+n",
            ["rename_workspace"] = null,
            ["delete_workspace"] = null,
            ["new_session"] = "ctrl+shift+t",
            // agterm's ⌘O; Ctrl+O belongs to the terminal.
            ["open_directory"] = null,
            ["rename_session"] = null,
            ["duplicate_session"] = null,
            ["close_session"] = "ctrl+shift+w",
            ["reopen_recent"] = "ctrl+alt+shift+t",
            // agterm's ⌘Z and ⌘⇧T; Ctrl+Z belongs to the terminal and Ctrl+Shift+T is new_session here.
            ["undo_close"] = "ctrl+shift+z",
            ["clear_status"] = null,
            ["increase_font_size"] = "ctrl+=|ctrl+kp_add",
            ["decrease_font_size"] = "ctrl+-|ctrl+kp_subtract",
            ["reset_font_size"] = "ctrl+0|ctrl+kp_0",
            ["toggle_split"] = "ctrl+shift+d",
            ["toggle_horizontal_split"] = "ctrl+shift+e",
            ["toggle_scratch"] = "ctrl+shift+j",
            ["toggle_terminal_zoom"] = "ctrl+shift+enter",
            // agterm's ⇧⌘J; Ctrl+Shift+J is the scratch here, as agterm's ⌘J.
            ["toggle_overlay"] = "ctrl+alt+shift+j",
            ["toggle_search"] = "ctrl+shift+f",
            ["toggle_sidebar"] = "ctrl+shift+b",
            ["select_theme"] = null,
            // agterm's ⌃⌘F; F11 is the Windows chord for it.
            ["toggle_fullscreen"] = "f11",
            ["toggle_flagged_view"] = null,
            // Keyless: agterm's ⌘⇧F for toggle_flag is toggle_search here.
            ["toggle_flag"] = null,
            ["focus_workspace"] = null,
            ["toggle_workspace_filter"] = null,
            ["previous_workspace"] = null,
            ["next_workspace"] = null,
            ["toggle_workspace_collapse"] = null,
            ["focus_left_pane"] = "ctrl+alt+left",
            ["focus_right_pane"] = "ctrl+alt+right",
            ["previous_session"] = "ctrl+alt+up",
            ["next_session"] = "ctrl+alt+down",
            ["previous_attention_session"] = "ctrl+alt+shift+up",
            ["next_attention_session"] = "ctrl+alt+shift+down",
            ["first_session"] = null,
            ["last_session"] = null,
            ["quick_terminal"] = "ctrl+`",
            ["session_palette"] = "ctrl+p",
            ["command_palette"] = "ctrl+shift+p",
            ["custom_command_palette"] = "ctrl+shift+o",
            ["show_attention"] = "ctrl+shift+i",
            ["dashboard"] = "ctrl+shift+g",
            // agterm's ⌘⇧R.
            ["attach_remote"] = "ctrl+shift+r",
        };

        private static readonly Dictionary<string, string> NamedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tab"] = "Tab",
            ["space"] = "Space",
            ["enter"] = "Enter",
            ["return"] = "Enter",
            ["backspace"] = "Backspace",
            ["delete"] = "Delete",
            ["insert"] = "Insert",
            ["home"] = "Home",
            ["end"] = "End",
            ["page_up"] = "PageUp",
            ["page_down"] = "PageDown",
            ["left"] = "ArrowLeft",
            ["right"] = "ArrowRight",
            ["up"] = "ArrowUp",
            ["down"] = "ArrowDown",
            ["kp_add"] = "NumpadAdd",
            ["kp_subtract"] = "NumpadSubtract",
            ["kp_0"] = "Numpad0",
        };

        private static readonly Dictionary<char, string> Symbols = new Dictionary<char, string>
        {
            ['`'] = "Backquote",
            ['-'] = "Minus",
            ['='] = "Equal",
            ['['] = "BracketLeft",
            [']'] = "BracketRight",
            ['\\'] = "Backslash",
            [';'] = "Semicolon",
            ['\''] = "Quote",
            [','] = "Comma",
            ['.'] = "Period",
            ['/'] = "Slash",
        };

        public static KeymapResult Parse(string text)
        {
            KeymapResult result = new KeymapResult();
            Dictionary<string, Override> overrides = new Dictionary<string, Override>();
            List<ParsedCommand> commands = new List<ParsedCommand>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int number = 0; number < lines.Length; number++)
            {
                string line = StripComment(lines[number]).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string where = "line " + (number + 1) + ": ";
                int space = line.IndexOfAny(new[] { ' ', '\t' });
                string verb = space < 0 ? line : line[..space];
                string rest = space < 0 ? string.Empty : line[space..].Trim();
                switch (verb)
                {
                    case "map":
                        ParseMap(rest, result, overrides, where);
                        break;
                    case "command":
                        ParseCommand(rest, result, commands, where);
                        break;
                    case "global-hotkey":
                        ParseGlobalHotkey(rest, result, where);
                        break;
                    default:
                        result.Diagnostics.Add(where + "unknown verb '" + verb + "'");
                        break;
                }
            }

            ResolveBindings(result, overrides, commands);
            return result;
        }

        private sealed record Override(List<(string Raw, KeyBinding Binding)> Bindings, string Where, bool Siblings);

        private sealed record ParsedCommand(CustomCommand Command, List<(string Raw, KeyBinding Binding)> Bindings, string Where);

        private sealed record MonitorBind(string Name, bool Custom, string Raw, KeyBinding Binding, string Where, bool Siblings)
        {
            public string Owner => (Custom ? "custom command '" : "built-in '") + Name + "'";
            public string Subject => Owner + (Custom ? " shortcut '" : " chord '") + Raw + "'";
        }

        private static void ResolveBindings(KeymapResult result, Dictionary<string, Override> overrides, List<ParsedCommand> commands)
        {
            Dictionary<string, List<KeyBinding>> defaults = Defaults.ToDictionary(pair => pair.Key,
                pair => pair.Value is string text ? ParseBindings(text, false) : new List<KeyBinding>());
            Dictionary<string, KeyBinding> moved = new Dictionary<string, KeyBinding>();
            HashSet<string> unbound = new HashSet<string>();
            foreach ((string name, Override line) in overrides)
            {
                KeyBinding? menu = line.Bindings.Select(part => part.Binding).FirstOrDefault(binding => binding.Sequence.Count == 1);
                if (menu is null) unbound.Add(name);
                else moved[name] = menu;
            }

            foreach (string later in new[] { "toggle_horizontal_split", "dashboard", "attach_remote", "toggle_overlay" })
            {
                if (overrides.ContainsKey(later) || defaults[later].Count == 0) continue;
                KeyChord chord = defaults[later][0].Sequence[0];
                if (overrides.Values.Any(line => line.Bindings.Any(part => part.Binding.Sequence[0] == chord)) ||
                    commands.Any(line => line.Bindings.Any(part => part.Binding.Sequence[0] == chord)))
                {
                    unbound.Add(later);
                }
            }

            while (true)
            {
                Dictionary<string, KeyBinding> fixedMenus = defaults.Where(pair => !moved.ContainsKey(pair.Key) && !unbound.Contains(pair.Key) && pair.Value.Count > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value[0]);
                List<(string Name, string Holder)> conflicts = new List<(string, string)>();
                foreach ((string name, KeyBinding binding) in moved)
                {
                    string? holder = fixedMenus.FirstOrDefault(pair => pair.Value.SameKeys(binding)).Key
                        ?? moved.FirstOrDefault(pair => pair.Key != name && pair.Value.SameKeys(binding)).Key;
                    if (holder is not null) conflicts.Add((name, holder));
                }

                if (conflicts.Count == 0) break;
                foreach ((string name, string holder) in conflicts)
                {
                    moved.Remove(name);
                    Override line = overrides[name];
                    result.Diagnostics.Add(line.Where + "chord conflicts with built-in '" + holder + "'; " + (line.Siblings ? "alternative skipped" : "map skipped"));
                }
            }

            foreach (string name in Defaults.Keys)
            {
                result.MenuChords[name] = moved.TryGetValue(name, out KeyBinding? binding) ? binding.Sequence[0]
                    : !unbound.Contains(name) && defaults[name].Count > 0 ? defaults[name][0].Sequence[0] : null;
            }

            List<MonitorBind> monitors = new List<MonitorBind>();
            foreach ((string name, Override line) in overrides)
            {
                KeyBinding? menu = line.Bindings.Select(part => part.Binding).FirstOrDefault(binding => binding.Sequence.Count == 1);
                foreach ((string raw, KeyBinding binding) in line.Bindings.Where(part => part.Binding != menu))
                {
                    monitors.Add(new MonitorBind(name, false, raw, binding, line.Where, line.Siblings));
                }
            }

            foreach ((string name, List<KeyBinding> bindings) in defaults.Where(pair => !overrides.ContainsKey(pair.Key) || (!moved.ContainsKey(pair.Key) && !unbound.Contains(pair.Key))))
            {
                foreach (KeyBinding binding in bindings.Skip(1))
                {
                    monitors.Add(new MonitorBind(name, false, string.Join(">", binding.Sequence), binding, string.Empty, true));
                }
            }

            foreach (ParsedCommand line in commands)
            {
                foreach ((string raw, KeyBinding binding) in line.Bindings)
                {
                    monitors.Add(new MonitorBind(line.Command.Name, true, raw, binding, line.Where, line.Bindings.Count > 1));
                }
            }

            List<MonitorBind> unshadowed = new List<MonitorBind>();
            foreach (MonitorBind bind in monitors)
            {
                string? owner = result.MenuChords.FirstOrDefault(pair => pair.Value == bind.Binding.Sequence[0]).Key;
                string? holder = owner is not null ? "built-in '" + owner + "'" : bind.Binding.Sequence.Any(IsReserved) ? "a reserved shortcut" : null;
                if (holder is null) unshadowed.Add(bind);
                else Diagnose(bind, holder);
            }

            Dictionary<int, string> rejected = new Dictionary<int, string>();
            for (int i = 0; i < unshadowed.Count; i++)
            {
                for (int j = i + 1; j < unshadowed.Count; j++)
                {
                    MonitorBind first = unshadowed[i];
                    MonitorBind second = unshadowed[j];
                    if (!first.Binding.Overlaps(second.Binding)) continue;
                    if (first.Custom == second.Custom && first.Name == second.Name)
                    {
                        int longer = first.Binding.Sequence.Count > second.Binding.Sequence.Count ? i : j;
                        rejected.TryAdd(longer, first.Owner);
                    }
                    else
                    {
                        rejected.TryAdd(i, second.Owner);
                        rejected.TryAdd(j, first.Owner);
                    }
                }
            }

            List<MonitorBind> survivors = new List<MonitorBind>();
            for (int i = 0; i < unshadowed.Count; i++)
            {
                if (rejected.TryGetValue(i, out string? owner)) Diagnose(unshadowed[i], owner);
                else survivors.Add(unshadowed[i]);
            }

            HashSet<KeyChord> occupied = result.MenuChords.Values.OfType<KeyChord>().Concat(survivors.Select(bind => bind.Binding.Sequence[0])).ToHashSet();
            foreach (string name in unbound.Where(name => !survivors.Any(bind => !bind.Custom && bind.Name == name)))
            {
                if (defaults[name].Count == 0) continue;
                KeyChord shipped = defaults[name][0].Sequence[0];
                if (!occupied.Contains(shipped)) result.MenuChords[name] = shipped;
            }

            foreach (string name in Defaults.Keys)
            {
                List<KeyBinding> resolved = result.MenuChords[name] is KeyChord chord
                    ? new List<KeyBinding> { new KeyBinding(new[] { chord }, false) } : new List<KeyBinding>();
                resolved.AddRange(survivors.Where(bind => !bind.Custom && bind.Name == name).Select(bind => bind.Binding));
                result.Actions[name] = resolved;
            }

            foreach (ParsedCommand line in commands)
            {
                List<MonitorBind> kept = survivors.Where(bind => bind.Custom && bind.Name == line.Command.Name).ToList();
                result.Commands.Add(line.Command with { Bindings = kept.Select(bind => bind.Binding).ToList(), Shortcut = string.Join("|", kept.Select(bind => bind.Raw)) });
            }

            void Diagnose(MonitorBind bind, string holder)
            {
                result.Diagnostics.Add(bind.Where + bind.Subject + " conflicts with " + holder + "; " + (bind.Siblings ? "alternative dropped" : "keybind dropped"));
            }
        }

        /// <summary>
        /// agterm's map line, `map &lt;chord&gt; [--repeat] &lt;action&gt;`; the last line of an action wins. Of its alternatives the
        /// first single chord may go without a modifier (not a bare arrow), the rest need one on their first key, and a
        /// reserved shortcut is refused anywhere; each that breaks a rule drops alone.
        /// </summary>
        private static void ParseMap(string rest, KeymapResult result, Dictionary<string, Override> overrides, string where)
        {
            string chordText = FirstWord(rest);
            string actionName = rest[chordText.Length..].Trim();
            bool repeat = FirstWord(actionName) == "--repeat";
            if (repeat)
            {
                actionName = actionName["--repeat".Length..].Trim();
            }

            if (chordText.Length == 0 || actionName.Length == 0)
            {
                result.Diagnostics.Add(where + "map requires a chord and an action");
                return;
            }

            if (Alternatives(chordText, repeat) is not List<(string Raw, KeyBinding Binding)> parsed)
            {
                result.Diagnostics.Add(where + "invalid chord '" + chordText + "'");
                return;
            }

            string scope = parsed.Count > 1 ? "alternative skipped" : "map skipped";
            List<(string, KeyBinding)> kept = new List<(string, KeyBinding)>();
            bool first = true;
            foreach ((string raw, KeyBinding binding) in parsed)
            {
                if (binding.Sequence.Any(IsReserved))
                {
                    result.Diagnostics.Add(where + "chord '" + raw + "' is a reserved shortcut; " + scope);
                    continue;
                }

                if (first && binding.Sequence.Count == 1)
                {
                    KeyChord chord = binding.Sequence[0];
                    if (!chord.HasModifier && chord.Code.StartsWith("Arrow", StringComparison.Ordinal))
                    {
                        result.Diagnostics.Add(where + "bare arrow chord '" + raw + "' needs a modifier; " + scope);
                        continue;
                    }

                    first = false;
                    kept.Add((raw, binding));
                    continue;
                }

                if (!CanStartShortcut(binding.Sequence[0]))
                {
                    result.Diagnostics.Add(where + "chord '" + raw + "' needs a modifier on its first key; " + scope);
                    continue;
                }

                kept.Add((raw, binding));
            }

            // Nothing survived: no word on the action on top of the alternatives', as in agterm.
            if (kept.Count == 0)
            {
                return;
            }

            if (!Defaults.ContainsKey(actionName))
            {
                result.Diagnostics.Add(where + "unknown action '" + actionName + "'");
                return;
            }

            overrides[actionName] = new Override(kept, where, parsed.Count > 1);
        }

        /// <summary>
        /// agterm's command line, `command "name" [chord] [options] shell…`. A first word that reads as chords binds the
        /// command; if none of them has a modifier the line stays in the palettes only, the word part of the shell line.
        /// </summary>
        private static void ParseCommand(string rest, KeymapResult result, List<ParsedCommand> commands, string where)
        {
            int close = rest.StartsWith('"') ? rest.IndexOf('"', 1) : -1;
            if (close < 0)
            {
                result.Diagnostics.Add(where + "command requires a quoted name");
                return;
            }

            string name = rest[1..close];
            string afterName = rest[(close + 1)..].Trim();
            string firstToken = FirstWord(afterName);
            string shellLine = afterName;
            List<(string Raw, KeyBinding Binding)> bindings = new List<(string, KeyBinding)>();
            if (firstToken.Length > 0 && Alternatives(firstToken, repeat: false) is List<(string Raw, KeyBinding Binding)> parsed)
            {
                bindings = parsed.Where(alternative => CanStartShortcut(alternative.Binding.Sequence[0])).ToList();
                if (bindings.Count == 0)
                {
                    result.Diagnostics.Add(where + "command '" + name + "' shortcut '" + firstToken + "' must include a modifier; treating the line as palette-only");
                }
                else
                {
                    foreach ((string raw, KeyBinding _) in parsed.Where(alternative => !CanStartShortcut(alternative.Binding.Sequence[0])))
                    {
                        result.Diagnostics.Add(where + "command '" + name + "' shortcut '" + raw + "' must include a modifier; alternative skipped");
                    }

                    shellLine = afterName[firstToken.Length..].Trim();
                }
            }
            else if (firstToken.Contains('|') && firstToken.Split('|').Any(part => Alternatives(part, repeat: false) is not null))
            {
                result.Diagnostics.Add(where + "command '" + name + "' shortcut '" + firstToken + "' has an invalid alternative; treating the line as palette-only");
            }

            if (ParseCommandOptions(shellLine, name, result, where) is not var (shell, repeat, errorHud, position, pane))
            {
                return;
            }

            // The name is what a command is known by, so a second definition cannot share it.
            if (commands.Any(existing => existing.Command.Name == name))
            {
                result.Diagnostics.Add(where + "command '" + name + "' is already defined; command skipped");
                return;
            }

            bindings = bindings.Select(alternative => (alternative.Raw, alternative.Binding with { Repeat = repeat && alternative.Binding.Sequence.Count > 1 })).ToList();
            commands.Add(new ParsedCommand(new CustomCommand(name, bindings.Select(alternative => alternative.Binding).ToList(), shell, errorHud, position, pane), bindings, where));
        }

        /// <summary>agterm's command options: --repeat and the --error-* ones; anything else starts the shell line, -- ends them.</summary>
        private static (string Shell, bool Repeat, bool ErrorHud, string? Position, string? Pane)? ParseCommandOptions(string body, string name, KeymapResult result, string where)
        {
            string? Reject(string reason)
            {
                result.Diagnostics.Add(where + "command '" + name + "' " + reason);
                return null;
            }

            bool repeat = false;
            bool errorHud = false;
            string? position = null;
            string? pane = null;
            HashSet<string> seen = new HashSet<string>();
            while (body.Length > 0)
            {
                string option = FirstWord(body);
                if (option == "--")
                {
                    body = body[option.Length..].TrimStart();
                    break;
                }

                if (option == "--repeat")
                {
                    if (repeat)
                    {
                        Reject("repeats option '" + option + "'");
                        return null;
                    }

                    body = body[option.Length..].TrimStart();
                    repeat = true;
                    continue;
                }

                if (!option.StartsWith("--error-", StringComparison.Ordinal))
                {
                    break;
                }

                if (option is not ("--error-hud" or "--error-position" or "--error-pane"))
                {
                    Reject("has unknown option '" + option + "'");
                    return null;
                }

                if (!seen.Add(option))
                {
                    Reject("repeats option '" + option + "'");
                    return null;
                }

                body = body[option.Length..].TrimStart();
                switch (option)
                {
                    case "--error-hud":
                        errorHud = true;
                        break;
                    case "--error-position":
                        string positionText = FirstWord(body);
                        body = body[positionText.Length..].TrimStart();
                        if ((position = HudPositions.Normalize(positionText)) is null)
                        {
                            Reject("requires a valid HUD position after --error-position");
                            return null;
                        }

                        break;
                    default:
                        pane = FirstWord(body);
                        body = body[pane.Length..].TrimStart();
                        if (pane is not ("left" or "right"))
                        {
                            Reject("requires left or right after --error-pane");
                            return null;
                        }

                        break;
                }
            }

            if (!errorHud && seen.Count > 0)
            {
                Reject("requires --error-hud for error placement options");
                return null;
            }

            if (body.Trim().Length == 0)
            {
                Reject("has no shell line");
                return null;
            }

            return (body.Trim(), repeat, errorHud, position, pane);
        }

        /// <summary>agterm's global-hotkey: one chord with a modifier, or a function key; the last line wins.</summary>
        private static void ParseGlobalHotkey(string rest, KeymapResult result, string where)
        {
            string token = FirstWord(rest);
            if (token.Length == 0)
            {
                result.Diagnostics.Add(where + "global-hotkey needs a chord");
                return;
            }

            if (rest[token.Length..].Trim().Length > 0)
            {
                result.Diagnostics.Add(where + "global-hotkey takes one chord, found '" + rest + "'");
                return;
            }

            List<KeyBinding>? hotkey = token.Contains('|') ? null : Alternatives(token, repeat: false)?.Select(alternative => alternative.Binding).ToList();
            if (hotkey is not { Count: 1 } || hotkey[0].Sequence.Count != 1)
            {
                result.Diagnostics.Add(where + "invalid global-hotkey chord '" + token + "'");
                return;
            }

            if (!CanStartShortcut(hotkey[0].Sequence[0]))
            {
                result.Diagnostics.Add(where + "global-hotkey '" + token + "' must include a modifier");
                return;
            }

            result.GlobalHotkey = hotkey[0].Sequence[0];
        }

        /// <summary>agterm's alternativeKeybinds: each | alternative with its own spelling, a repeat dropped; null for a typo in any.</summary>
        private static List<(string Raw, KeyBinding Binding)>? Alternatives(string token, bool repeat)
        {
            List<(string, KeyBinding)> kept = new List<(string, KeyBinding)>();
            foreach (string part in token.Split('|'))
            {
                KeyBinding binding;
                try
                {
                    List<KeyChord> sequence = part.Split('>').Select(ParseChord).ToList();
                    binding = new KeyBinding(sequence, repeat && sequence.Count > 1);
                }
                catch (FormatException)
                {
                    return null;
                }

                if (!kept.Any(existing => existing.Item2.SameKeys(binding)))
                {
                    kept.Add((part, binding));
                }
            }

            return kept;
        }

        /// <summary>agterm's canStartShortcut: a modifier, or a function key.</summary>
        private static bool CanStartShortcut(KeyChord chord)
        {
            return chord.HasModifier || chord.IsFunctionKey;
        }

        /// <summary>
        /// agterm's reserved shortcuts, which the app takes before any binding: Tab with Ctrl held, whatever else is, for the
        /// session switcher, and Ctrl+1 or Ctrl+2 alone for the panes.
        /// </summary>
        public static bool IsReserved(KeyChord chord)
        {
            return (chord.Ctrl && chord.Code == "Tab") || (chord.Ctrl && !chord.Alt && !chord.Shift && !chord.Meta && chord.Code is "Digit1" or "Digit2");
        }

        /// <summary>agterm's stripComment: a # after whitespace, outside quotes, starts a comment.</summary>
        private static string StripComment(string line)
        {
            bool single = false;
            bool quoted = false;
            bool afterSpace = true;
            for (int index = 0; index < line.Length; index++)
            {
                char c = line[index];
                if (c == '"' && !single)
                {
                    quoted = !quoted;
                }
                else if (c == '\'' && !quoted)
                {
                    single = !single;
                }
                else if (c == '#' && !single && !quoted && afterSpace)
                {
                    return line[..index];
                }

                afterSpace = char.IsWhiteSpace(c);
            }

            return line;
        }

        /// <summary>Chords joined by | for alternatives and by > for a sequence. A typo anywhere rejects the whole token.</summary>
        public static List<KeyBinding> ParseBindings(string token, bool repeat)
        {
            List<KeyBinding> bindings = new List<KeyBinding>();
            foreach (string alternative in token.Split('|'))
            {
                List<KeyChord> sequence = alternative.Split('>').Select(ParseChord).ToList();
                bindings.Add(new KeyBinding(sequence, repeat && sequence.Count > 1));
            }

            return bindings;
        }

        public static KeyChord ParseChord(string text)
        {
            if (text.Length == 0)
            {
                throw new FormatException("empty chord");
            }

            string[] parts = text.ToLowerInvariant().Split('+').Select(part => part.Trim()).ToArray();
            bool ctrl = false;
            bool alt = false;
            bool shift = false;
            bool meta = false;
            string? key = null;
            foreach (string part in parts)
            {
                switch (part)
                {
                    case "ctrl":
                    case "control": ctrl = true; break;
                    case "alt":
                    case "opt":
                    case "option": alt = true; break;
                    case "shift": shift = true; break;
                    case "cmd":
                    case "command":
                    case "super":
                    case "win": meta = true; break;
                    default:
                        if (part.Length == 0 || key is not null) throw new FormatException("invalid chord " + text);
                        key = part;
                        break;
                }
            }

            return new KeyChord(ctrl, alt, shift, KeyCode(key ?? throw new FormatException("missing key in " + text), text)) { Meta = meta };
        }

        private static string KeyCode(string key, string chord)
        {
            if (NamedKeys.TryGetValue(key, out string? named))
            {
                return named;
            }

            if (key.Length == 1 && char.IsAsciiLetterLower(key[0]))
            {
                return "Key" + char.ToUpperInvariant(key[0]);
            }

            if (key.Length == 1 && char.IsAsciiDigit(key[0]))
            {
                return "Digit" + key;
            }

            if (key.Length == 1 && Symbols.TryGetValue(key[0], out string? symbol))
            {
                return symbol;
            }

            if (key.Length > 1 && key[0] == 'f' && int.TryParse(key[1..], out int number) && number is >= 1 and <= 20)
            {
                return "F" + number;
            }

            throw new FormatException("unknown key " + key + " in " + chord);
        }

        /// <summary>How a key code is written in the file, for keymap list and the palettes.</summary>
        public static string KeyName(string code)
        {
            if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4)
            {
                return code[3..].ToLowerInvariant();
            }

            if (code.StartsWith("Digit", StringComparison.Ordinal))
            {
                return code[5..];
            }

            string? named = NamedKeys.FirstOrDefault(pair => pair.Value == code && pair.Key != "return").Key;
            if (named is not null)
            {
                return named;
            }

            char? symbol = Symbols.FirstOrDefault(pair => pair.Value == code).Key;
            return symbol is char found && found != default ? found.ToString() : code.ToLowerInvariant();
        }

        private static string FirstWord(string text)
        {
            int end = text.IndexOfAny(new[] { ' ', '\t' });
            return end < 0 ? text : text[..end];
        }

        /// <summary>The commented starter written on first launch.</summary>
        public const string Starter = """
            # agshell keymap — the format of agterm's keymap.conf. Saved changes apply at once.
            # Chords: ctrl, alt, shift joined by + with a key (a letter, a digit, `-=[]\;',./,
            # tab, space, enter, backspace, delete, left, right, up, down, f1-f20). A leader
            # sequence joins chords with > (ctrl+a>g); alternatives with | and no spaces.
            #
            # Rebind a built-in action (the list is in docs/agshellctl.md):
            # map ctrl+shift+l  toggle_split
            # map ctrl+a>ctrl+l --repeat  next_session
            #
            # Bind a shell line (run by cmd.exe without a window; tokens like {AGSHELL_SESSION_PWD}
            # are also set as environment variables):
            # command "Open in VS Code"  ctrl+alt+o  code "{AGSHELL_SESSION_PWD}"
            # command "Lazygit"  ctrl+a>g  agshellctl session overlay open lazygit --target {AGSHELL_SESSION_ID}
            # command "Build"  ctrl+a>b --error-hud  build.cmd
            #
            # One system-wide chord that brings agshell up with the quick terminal:
            # global-hotkey ctrl+alt+space
            """;
    }
}
