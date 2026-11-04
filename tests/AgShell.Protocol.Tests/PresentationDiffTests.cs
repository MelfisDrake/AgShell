namespace AgShell.Protocol.Tests
{
    public class PresentationDiffTests
    {
        [Fact]
        public void TheLayoutGoesFirstAndACountdownAloneIsNoChange()
        {
            ControlPresentation shown = new ControlPresentation { Hud = new ControlPresentationHud { Id = "h", Message = "x", Remaining = 5 } };
            ControlPresentation next = shown with
            {
                Layout = new ControlPresentationLayout { Panes = new List<string> { "A", "B" }, Primary = "A", Axis = "vertical" },
                Status = new ControlPresentationStatus { Status = "blocked" },
                Hud = shown.Hud! with { Remaining = 4 },
            };
            Assert.Equal(new[] { ControlPresentationKinds.Layout, ControlPresentationKinds.Status }, PresentationDiff.Changes(shown, next).Select(frame => frame.Kind));
        }

        [Fact]
        public void APageResizeOrVisibilityGoesOutAsAgtermsPageCommands()
        {
            ControlPresentationPage page = new ControlPresentationPage { Id = "p", Owner = 1, Url = "https://example.com", SizePercent = 60 };
            ControlPresentation first = new ControlPresentation { Pages = new List<ControlPresentationPage> { page } };
            ControlPresentation second = new ControlPresentation { Pages = new List<ControlPresentationPage> { page with { SizePercent = 40, Hidden = true } } };
            Assert.Equal(new[] { ("resize", (int?)40), ("hide", (int?)null) }, PresentationDiff.Changes(first, second).Select(frame => (frame.PageCommand!.Action, frame.PageCommand.SizePercent)));
        }

        [Fact]
        public void APageCommandGoesOutOnce()
        {
            ControlPresentationPage page = new ControlPresentationPage { Id = "p", Owner = 1, Url = "https://example.com" };
            ControlPresentation first = new ControlPresentation { Pages = new List<ControlPresentationPage> { page } };
            ControlPresentation second = new ControlPresentation { Pages = new List<ControlPresentationPage> { page with { Commands = new List<ControlPresentationPageCommand> { new ControlPresentationPageCommand { Seq = 1, Action = "reload-original" } } } } };
            Assert.Equal(ControlPresentationKinds.PageRequest, Assert.Single(PresentationDiff.Changes(new ControlPresentation(), first)).Kind);
            Assert.Equal(ControlPresentationKinds.PageCommandKind, Assert.Single(PresentationDiff.Changes(first, second)).Kind);
            Assert.Empty(PresentationDiff.Changes(second, second));
        }
    

        [Fact]
        public void TheLayoutComparesByValue()
        {
            ControlPresentation shown = new ControlPresentation { Layout = new ControlPresentationLayout { Panes = new List<string> { "A", "B" }, Primary = "A", Axis = "vertical", Shown = true } };
            ControlPresentation same = new ControlPresentation { Layout = new ControlPresentationLayout { Panes = new List<string> { "A", "B" }, Primary = "A", Axis = "vertical", Shown = true } };
            Assert.Empty(PresentationDiff.Changes(shown, same));
        }
    }

    public class PresentationWireTests
    {
        private static readonly string Left = Guid.NewGuid().Wire();
        private static readonly string Right = Guid.NewGuid().Wire();

        [Fact]
        public void AFrameTravelsInAgtermsEnvelope()
        {
            string line = ControlJson.Serialize(new ControlPresentationFrame { Kind = ControlPresentationKinds.AskResolve, Gen = 3, Rev = 7, Id = "q", Owner = 2, Button = "yes" });
            Assert.Equal("{\"kind\":\"ask.resolve\",\"gen\":3,\"rev\":7,\"ask\":{\"id\":\"q\",\"owner\":2,\"button\":\"yes\"}}", line);
            ControlPresentationFrame back = ControlJson.DeserializeFrame(line);
            Assert.Equal((3, 7, "q", (int?)2, "yes"), (back.Gen, back.Rev, back.Id, back.Owner, back.Button));
        }

        [Fact]
        public void HelloNotifyAndPageStateNestUnderTheirKeys()
        {
            ControlPresentationFrame hello = ControlJson.DeserializeFrame(ControlJson.Serialize(new ControlPresentationFrame { Kind = ControlPresentationKinds.Hello, Version = 1, Kinds = new List<string> { "status" }, PresentationMode = "presenter" }));
            Assert.Equal((1, "presenter", "status"), (hello.Version!.Value, hello.PresentationMode!, Assert.Single(hello.Kinds!)));
            string notify = ControlJson.Serialize(new ControlPresentationFrame { Kind = ControlPresentationKinds.Notify, Title = "t", Body = "b" });
            Assert.Contains("\"notify\":{\"title\":\"t\",\"body\":\"b\"}", notify);
            string state = ControlJson.Serialize(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageState, Id = "p", Owner = 1, PageState = new ControlPresentationPageState { State = "loaded", Page = "https://example.com" } });
            Assert.Contains("\"page\":{\"page\":\"p\",\"owner\":1,", state);
            Assert.Contains("\"address\":\"https://example.com\"", state);
            ControlPresentationFrame read = ControlJson.DeserializeFrame(state);
            Assert.Equal(("p", "https://example.com"), (read.Id, read.PageState!.Page));
        }

        [Theory]
        [InlineData("{\"kind\":\"ping\",\"rev\":0}")]
        [InlineData("{\"kind\":\"hello\",\"gen\":0,\"rev\":0,\"hello\":{\"kinds\":[],\"mode\":\"mirror\"}}")]
        [InlineData("{\"kind\":\"notify\",\"gen\":0,\"rev\":0}")]
        [InlineData("{\"kind\":\"status\",\"gen\":0,\"rev\":0,\"status\":{\"status\":\"blocked\",\"pane\":\"left\"}}")]
        public void AFrameMissingWhatItsKindNeedsIsMalformed(string line)
        {
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => ControlJson.DeserializeFrame(line));
        }

        [Fact]
        public void ALayoutItCannotReadIsInvalidAndTheFrameStands()
        {
            ControlPresentationFrame frame = ControlJson.DeserializeFrame("{\"kind\":\"layout\",\"gen\":1,\"rev\":2,\"layout\":{\"panes\":[\"x\"],\"primary\":\"x\",\"shown\":false}}");
            Assert.False(frame.Layout!.IsValid);
            ControlPresentationLayout layout = ControlJson.DeserializeFrame(ControlJson.Serialize(new ControlPresentationFrame { Kind = ControlPresentationKinds.Layout, Layout = new ControlPresentationLayout { Panes = new List<string> { Left, Right }, Primary = Right, Axis = "horizontal", Shown = true } })).Layout!;
            Assert.True(layout.IsValid);
            Assert.Equal((Right, Left), (layout.MainId, layout.SplitId));
        }

        [Theory]
        [InlineData(1, false, "vertical", true)]
        [InlineData(1, true, null, false)]
        [InlineData(2, true, "horizontal", true)]
        [InlineData(2, false, null, false)]
        [InlineData(2, true, "diagonal", false)]
        public void ALayoutIsValidAsAgtermsIsValid(int panes, bool shown, string? axis, bool valid)
        {
            List<string> ids = new List<string> { Left, Right }.Take(panes).ToList();
            Assert.Equal(valid, new ControlPresentationLayout { Panes = ids, Primary = Left, Axis = axis, Shown = shown }.IsValid);
            Assert.False(new ControlPresentationLayout { Panes = new List<string> { Left, Left }, Primary = Left, Axis = "vertical" }.IsValid);
            Assert.False(new ControlPresentationLayout { Panes = ids, Primary = Guid.NewGuid().Wire(), Axis = "vertical" }.IsValid);
        }
    }
}
