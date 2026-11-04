using AgShell.Protocol;

namespace AgShell.Ctl.Tests
{
    public class KeymapOutputTests
    {
        [Fact]
        public void AppliedMenuFollowsDiagnosticsAndMarksDisabledChords()
        {
            ControlKeymap keymap = new ControlKeymap
            {
                Path = "keymap.conf",
                Actions = new List<ControlKeymapAction>(),
                Commands = new List<ControlKeymapCommand>(),
                Diagnostics = new List<ControlConfigDiagnostic> { new ControlConfigDiagnostic { Line = 1, Message = "fixture" } },
                Menu = new List<ControlKeymapMenuItem>
                {
                    new ControlKeymapMenuItem { Menu = "File", Title = "New Session", Chord = "ctrl+shift+t", Selector = "action:new_session", Enabled = false },
                },
            };
            string text = Output.Format(new ControlResult { Keymap = keymap }, false)!;
            Assert.Contains("    ctrl+shift+t  File ▸ New Session  (disabled)", text);
            Assert.True(text.IndexOf("diagnostics:", StringComparison.Ordinal) < text.IndexOf("menu:", StringComparison.Ordinal));
            Assert.DoesNotContain("action:new_session", text);
            ControlResponse response = ControlJson.DeserializeResponse(ControlJson.Serialize(new ControlResponse { Ok = true, Result = new ControlResult { Keymap = keymap } }));
            Assert.False(Assert.Single(response.Result!.Keymap!.Menu!).Enabled);
        }
    }
}
