using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class PageBridgeTests
    {
        private static readonly Guid Session = Guid.Parse("9f3c1111-0000-0000-0000-000000000001");

        private const string Window = "w1";

        private static ControlRequest Built(string cmd, PaneRole slot = PaneRole.Overlay, string? target = null, ControlArgs? args = null)
        {
            return PageBridge.Build(new ControlRequest { Cmd = cmd, Target = target, Args = args }, Session, slot, Window).Request!;
        }

        [Fact]
        public void ASessionCommandGoesToThePageSessionAndItsOwnOverlayCommandsToItsPane()
        {
            ControlRequest submit = Built("session.overlay.submit", PaneRole.OverlayRight);
            Assert.Equal((Session.Wire(), "right"), (submit.Target, submit.Args!.Pane));
            Assert.Null(Built("session.overlay.open", PaneRole.OverlayRight).Args);
            Assert.Null(Built("session.hud.open", PaneRole.OverlayLeft).Args);
            Assert.Equal(Session.Wire(), Built("notify").Target);
            Assert.Equal(Session.Wire(), Built("ask.open").Target);
            Assert.Equal((null, Window), (Built("ask.open", args: new ControlArgs { Style = "gui" }).Target, Built("ask.open", args: new ControlArgs { Style = "gui" }).Args!.Window));
        }

        [Fact]
        public void TheRestTakesThePageWindowUnlessThePageAddressedIt()
        {
            Assert.Equal((null, Window), (Built("tree").Target, Built("tree").Args!.Window));
            Assert.Equal((null, Window), (Built("session.new").Target, Built("session.new").Args!.Window));
            Assert.Equal(Window, Built("window.rename").Target);
            Assert.Null(Built("window.list").Target);
            Assert.Null(Built("recent.list").Args);
            Assert.Equal(Window, Built("recent.open", target: "abc").Args!.Window);
            ControlRequest addressed = Built("session.overlay.close", PaneRole.OverlayRight, target: "other");
            Assert.Equal(("other", (ControlArgs?)null), (addressed.Target, addressed.Args));
            Assert.Null(Built("session.close", args: new ControlArgs { Window = "w2" }).Target);
        }

        [Fact]
        public void AReloadFromThePageKeepsWhatItShowsUnlessToldOtherwise()
        {
            Assert.True(Built("session.overlay.reload").Args!.Current);
            Assert.False(Built("session.overlay.reload", args: new ControlArgs { Current = false }).Args!.Current);
        }

        [Fact]
        public void AStreamOrAHelperCommandIsRefused()
        {
            Assert.Equal("session.overlay.job.run cannot be sent from a page", PageBridge.Build(new ControlRequest { Cmd = "session.overlay.job.run" }, Session, PaneRole.Overlay, Window).Error);
            Assert.Equal("live.present cannot be sent from a page", PageBridge.Build(new ControlRequest { Cmd = "live.present" }, Session, PaneRole.Overlay, Window).Error);
            Assert.Equal("live.reset cannot be sent from a page", PageBridge.Build(new ControlRequest { Cmd = "live.reset" }, Session, PaneRole.Overlay, Window).Error);
        }
    }
}
