namespace AgShell.Core.Tests
{
    public class KeymapTests
    {
        private static string Keys(KeymapResult result, string action)
        {
            return string.Join("|", result.Actions[action]);
        }

        [Theory]
        [InlineData("cmd")]
        [InlineData("command")]
        [InlineData("super")]
        [InlineData("win")]
        public void WindowsModifierIsIndependent(string alias)
        {
            KeyChord chord = Keymap.ParseChord(alias + "+ctrl+k");
            Assert.True(chord.Meta);
            Assert.True(chord.Ctrl);
            Assert.Equal("ctrl+super+k", chord.ToString());
            Assert.NotEqual(Keymap.ParseChord("ctrl+k"), chord);
            Assert.False(Keymap.IsReserved(Keymap.ParseChord(alias + "+ctrl+1")));
        }

        [Fact]
        public void DefaultsShipWithoutAFile()
        {
            KeymapResult result = Keymap.Parse(string.Empty);

            Assert.Equal("ctrl+shift+t", Keys(result, "new_session"));
            Assert.Equal("ctrl+=|ctrl+kp_add", Keys(result, "increase_font_size"));
            Assert.Equal(string.Empty, Keys(result, "rename_session"));
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public void MapReplacesTheShippedKeysAndCanReuseKeysMovedAway()
        {
            KeymapResult result = Keymap.Parse("""
                # comment
                map ctrl+shift+l|ctrl+a>s  toggle_split
                map ctrl+shift+d  toggle_scratch
                map ctrl+alt+j  toggle_split
                """);

            Assert.Equal("ctrl+alt+j", Keys(result, "toggle_split"));
            Assert.Equal("ctrl+shift+d", Keys(result, "toggle_scratch"));
            Assert.Empty(result.Diagnostics);
        }

        [Fact]
        public void AnUnmovedDefaultBeatsARemapAndTwoRemapsOnOneKeyBothLose()
        {
            KeymapResult result = Keymap.Parse("""
                map ctrl+shift+t  toggle_scratch
                map ctrl+shift+u  dashboard
                map ctrl+shift+u  toggle_sidebar
                """);

            Assert.Equal("ctrl+shift+t", Keys(result, "new_session"));
            Assert.Equal("ctrl+shift+j", Keys(result, "toggle_scratch"));
            Assert.Equal("ctrl+shift+g", Keys(result, "dashboard"));
            Assert.Equal("ctrl+shift+b", Keys(result, "toggle_sidebar"));
            Assert.Equal(3, result.Diagnostics.Count);
        }

        [Fact]
        public void RepeatSequencesAndPhysicalKeys()
        {
            KeymapResult result = Keymap.Parse("map ctrl+a>ctrl+l --repeat next_session\nmap ctrl+shift+/ show_attention");

            KeyBinding binding = Assert.Single(result.Actions["next_session"]);
            Assert.True(binding.Repeat);
            Assert.Equal(new KeyChord(true, false, false, "KeyA"), binding.Sequence[0]);
            Assert.Equal("Slash", Assert.Single(result.Actions["show_attention"]).Sequence[0].Code);
        }

        [Fact]
        public void BadLinesBecomeDiagnosticsAndTheRestApplies()
        {
            KeymapResult result = Keymap.Parse("""
                map ctrl+shift+q  no_such_action
                map ctrl+hyper+q  toggle_split
                map a|ctrl+shift+y  toggle_scratch
                map hyper+t  new_session
                bind ctrl+x dashboard
                """);

            // agterm's texts; and as in agterm a bare first chord of a map line is legal, only an arrow needs a modifier.
            Assert.Equal(new[]
            {
                "line 1: unknown action 'no_such_action'",
                "line 2: invalid chord 'ctrl+hyper+q'",
                "line 4: invalid chord 'hyper+t'",
                "line 5: unknown verb 'bind'",
            }, result.Diagnostics);
            Assert.Equal("a|ctrl+shift+y", Keys(result, "toggle_scratch"));
            Assert.Equal("ctrl+shift+d", Keys(result, "toggle_split"));
            Assert.Equal("ctrl+shift+t", Keys(result, "new_session"));
        }

        [Fact]
        public void MapAndCommandLinesFollowAgtermsRules()
        {
            KeymapResult result = Keymap.Parse("""
                map left toggle_split
                map ctrl+tab toggle_sidebar  # a comment
                map ctrl+shift+u|a>b toggle_search
                map ctrl+shift+u
                command "X" a echo hi
                command "Y" ctrl+1 echo y
                command "Z" ctrl+alt+z --error-pane left z.cmd
                command "W" ctrl+alt+w --error-oops w.cmd
                global-hotkey a
                global-hotkey ctrl+alt+space extra
                """);

            Assert.Equal(new[]
            {
                "line 1: bare arrow chord 'left' needs a modifier; map skipped",
                "line 2: chord 'ctrl+tab' is a reserved shortcut; map skipped",
                "line 3: chord 'a>b' needs a modifier on its first key; alternative skipped",
                "line 4: map requires a chord and an action",
                "line 5: command 'X' shortcut 'a' must include a modifier; treating the line as palette-only",
                "line 7: command 'Z' requires --error-hud for error placement options",
                "line 8: command 'W' has unknown option '--error-oops'",
                "line 9: global-hotkey 'a' must include a modifier",
                "line 10: global-hotkey takes one chord, found 'ctrl+alt+space extra'",
                "line 6: custom command 'Y' shortcut 'ctrl+1' conflicts with a reserved shortcut; keybind dropped",
            }, result.Diagnostics);
            Assert.Equal("ctrl+shift+u", Keys(result, "toggle_search"));
            Assert.Equal("a echo hi", result.Commands.Single(command => command.Name == "X").Shell);
            Assert.Empty(result.Commands.Single(command => command.Name == "Y").Bindings);
        }

        [Fact]
        public void CommandsTakeANameChordsFlagsAndAShellLine()
        {
            KeymapResult result = Keymap.Parse("""
                command "Open in VS Code"  ctrl+alt+o  code "{AGSHELL_SESSION_PWD}"
                command "Deploy"   deploy.cmd --fast
                command "Build"  ctrl+a>b --error-hud --error-position top --error-pane right  build.cmd --error-pane x
                command "Deploy"  other.cmd
                command "Empty"  ctrl+alt+e
                command "Bad"  --error-position top  x.cmd
                command "Taken"  ctrl+shift+t  y.cmd
                """);

            Assert.Equal(new[] { "Open in VS Code", "Deploy", "Build", "Taken" }, result.Commands.Select(command => command.Name));
            CustomCommand open = result.Commands[0];
            Assert.Equal("code \"{AGSHELL_SESSION_PWD}\"", open.Shell);
            Assert.Equal("ctrl+alt+o", Assert.Single(open.Bindings).ToString());
            Assert.Empty(result.Commands[1].Bindings);
            Assert.Equal("deploy.cmd --fast", result.Commands[1].Shell);
            CustomCommand build = result.Commands[2];
            Assert.True(build.ErrorHud);
            Assert.Equal("top-center", build.ErrorPosition);
            Assert.Equal("right", build.ErrorPane);
            Assert.Equal("build.cmd --error-pane x", build.Shell);
            Assert.Empty(result.Commands[3].Bindings);
            Assert.Equal("ctrl+shift+t", Keys(result, "new_session"));
            Assert.Equal(4, result.Diagnostics.Count);
        }

        [Fact]
        public void CommandShortcutKeepsTheSpellingOfItsSurvivingAlternatives()
        {
            KeymapResult result = Keymap.Parse("""
                command "Open"  Ctrl+Alt+O|ctrl+shift+t|ctrl+a>B  code .
                command "Plain"  plain.cmd
                """);

            Assert.Equal("Ctrl+Alt+O|ctrl+a>B", result.Commands[0].Shortcut);
            Assert.Equal(2, result.Commands[0].Bindings.Count);
            Assert.Equal(string.Empty, result.Commands[1].Shortcut);
        }

        [Fact]
        public void GlobalHotkeyIsOneChordAndTheLastWins()
        {
            KeymapResult result = Keymap.Parse("global-hotkey ctrl+alt+space\nglobal-hotkey f5\nglobal-hotkey ctrl+a>b");

            Assert.Equal(new KeyChord(false, false, false, "F5"), result.GlobalHotkey);
            Assert.Single(result.Diagnostics);
        }

        [Fact]
        public void TokensExpandRaw()
        {
            string expanded = CommandTokens.Expand("code \"{AGSHELL_SESSION_PWD}\" {AGSHELL_PANE} {UNKNOWN}", new Dictionary<string, string> { ["AGSHELL_SESSION_PWD"] = @"C:\src", ["AGSHELL_PANE"] = "left" });

            Assert.Equal("code \"C:\\src\" left {UNKNOWN}", expanded);
        }

        [Fact]
        public void TokensExpandInOnePassAndSessionTokensAreKnown()
        {
            string expanded = CommandTokens.Expand("echo {AGSHELL_SELECTION} {AGSHELL_WINDOW_NAME} {open", new Dictionary<string, string> { ["AGSHELL_SELECTION"] = "{AGSHELL_PIPE}", ["AGSHELL_PIPE"] = "pipe", ["AGSHELL_WINDOW_NAME"] = "w" });

            Assert.Equal("echo {AGSHELL_PIPE} w {open", expanded);
            Assert.True(CommandTokens.ReferencesSessionScopedContext("del /q \"%AGSHELL_SESSION_PWD%\\*\""));
            Assert.True(CommandTokens.ReferencesSessionScopedContext("echo {AGSHELL_WORKSPACE_NAME}"));
            Assert.False(CommandTokens.ReferencesSessionScopedContext("agshellctl --pipe {AGSHELL_PIPE} window new {AGSHELL_WINDOW_ID}"));
        }

        [Theory]
        [InlineData("\r")]
        [InlineData("\r\n")]
        [InlineData("\n")]
        public void HooksAcceptEveryLineEnding(string newline)
        {
            (List<HookLine> hooks, List<string> diagnostics) = HooksConfig.Parse("on status echo one" + newline + "on notify echo two");
            Assert.Empty(diagnostics);
            Assert.Equal(new[] { "status", "notify" }, hooks.Select(hook => hook.Kind));
        }

        [Fact]
        public void HooksAreParsedWithTheirDiagnostics()
        {
            (List<HookLine> lines, List<string> diagnostics) = HooksConfig.Parse("""
                on status  C:\scripts\status.cmd
                on session.closed echo %AGSHELL_SESSION_ID% >> log.txt
                on status  C:\scripts\status.cmd
                on sometimes x.cmd
                when status x.cmd
                on notify
                """);

            Assert.Equal(new[] { new HookLine("status", @"C:\scripts\status.cmd"), new HookLine("session.closed", "echo %AGSHELL_SESSION_ID% >> log.txt") }, lines);
            Assert.Equal(4, diagnostics.Count);
        }
    }
}
