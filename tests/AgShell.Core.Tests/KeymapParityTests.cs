namespace AgShell.Core.Tests
{
    public class KeymapParityTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ConflictingCommandsBothLoseRegardlessOfFileOrder(bool reverse)
        {
            string[] lines = { "command \"A\" ctrl+alt+k echo A", "command \"B\" ctrl+alt+k>x echo B" };
            KeymapResult parsed = Keymap.Parse(string.Join("\n", reverse ? lines.Reverse() : lines));
            Assert.All(parsed.Commands, command => Assert.Empty(command.Bindings));
            Assert.Equal(2, parsed.Diagnostics.Count);
        }

        [Theory]
        [InlineData("ctrl+a>x>y|ctrl+a>x")]
        [InlineData("ctrl+a>x|ctrl+a>x>y")]
        public void SameCommandKeepsTheShorterPrefix(string bindings)
        {
            KeymapResult parsed = Keymap.Parse("command \"A\" " + bindings + " echo A");
            Assert.Equal("ctrl+a>x", Assert.Single(parsed.Commands).Shortcut);
            Assert.Single(parsed.Diagnostics);
        }

        [Fact]
        public void BuiltinMonitorAndCustomAreEqualConflictParticipants()
        {
            KeymapResult parsed = Keymap.Parse("map ctrl+a>x rename_session\ncommand \"A\" ctrl+a>x echo A");
            Assert.Empty(parsed.Actions["rename_session"]);
            Assert.Empty(Assert.Single(parsed.Commands).Bindings);
            Assert.Equal(2, parsed.Diagnostics.Count);
        }

        [Fact]
        public void MenuChordShadowsItsOwnLongerAlternative()
        {
            KeymapResult parsed = Keymap.Parse("map ctrl+alt+k|ctrl+alt+k>x rename_session");
            Assert.Equal("ctrl+alt+k", Assert.Single(parsed.Actions["rename_session"]).ToString());
            Assert.Single(parsed.Diagnostics);
        }

        [Fact]
        public void ReadbackSeparatesTheMenuAndEveryMonitorAlternative()
        {
            KeymapResult parsed = Keymap.Parse("map ctrl+a>x|ctrl+shift+w close_session");
            AgShell.Protocol.ControlKeymapAction close = parsed.ActionReadback().Single(action => action.Action == "close_session");
            Assert.Equal("ctrl+shift+w", close.Chord);
            Assert.Equal(new[] { "ctrl+a>x" }, close.Alternates);
            Assert.Null(close.Overridden);
            parsed = Keymap.Parse("map ctrl+a>x close_session");
            close = parsed.ActionReadback().Single(action => action.Action == "close_session");
            Assert.Null(close.Chord);
            Assert.Equal(new[] { "ctrl+a>x" }, close.Alternates);
            Assert.True(close.Overridden);
            Assert.Equal(Keymap.Defaults.Keys, parsed.ActionReadback().Select(action => action.Action));
        }

        [Theory]
        [InlineData("control+option+k")]
        [InlineData("k+ctrl+alt")]
        [InlineData("alt+k+control")]
        public void AliasesAndModifierOrderResolveToTheSameChord(string spelling)
        {
            Assert.Equal(new KeyChord(true, true, false, "KeyK"), Keymap.ParseChord(spelling));
        }

        [Theory]
        [InlineData("ctrl+shift+g", "dashboard")]
        [InlineData("ctrl+shift+e", "toggle_horizontal_split")]
        public void ExplicitOldBindingsKeepLaterDefaults(string chord, string action)
        {
            KeymapResult parsed = Keymap.Parse("command \"A\" " + chord + " echo A");
            Assert.Equal(chord, Assert.Single(parsed.Commands).Shortcut);
            Assert.Empty(parsed.Actions[action]);
            Assert.Empty(parsed.Diagnostics);
        }

        [Fact]
        public void MenuChordOnlyShadowsTheFirstChordOfAMonitorSequence()
        {
            KeymapResult parsed = Keymap.Parse("command \"A\" ctrl+a>ctrl+shift+t echo A");
            Assert.Single(Assert.Single(parsed.Commands).Bindings);
            Assert.Empty(parsed.Diagnostics);
        }
    }
}
