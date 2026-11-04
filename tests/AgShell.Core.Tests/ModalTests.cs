using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class ModalTests
    {
        private readonly AppModel model = new AppModel();
        private readonly ControlDispatcher dispatcher;
        private readonly Workspace work;
        private readonly Session session;

        public ModalTests()
        {
            dispatcher = new ControlDispatcher(model, new DashboardTests.NullHost());
            work = model.AddWorkspace("work");
            session = model.AddSession(work.Id, "cmd", null);
        }

        private Task<ControlResponse> Send(string cmd, string? target = null, ControlArgs? args = null)
        {
            return dispatcher.HandleAsync(new ControlRequest { Cmd = cmd, Target = target, Args = args });
        }

        private static List<ControlPickItem> Items(params string[] labels)
        {
            return labels.Select(label => new ControlPickItem { Id = label.ToLowerInvariant(), Label = label }).ToList();
        }

        [Fact]
        public async Task PickChecksItsItems()
        {
            Assert.Equal("pick.open requires items", (await Send("pick.open")).Error);
            Assert.Equal("pick.open requires at least one item", (await Send("pick.open", args: new ControlArgs { Items = new List<ControlPickItem>() })).Error);
            Assert.Equal("pick item ids must be unique", (await Send("pick.open", args: new ControlArgs { Items = Items("A", "a") })).Error);
            Assert.Equal("pick select must name an item id", (await Send("pick.open", args: new ControlArgs { Items = Items("A"), Selection = "z" })).Error);
            List<ControlPickItem> bad = new List<ControlPickItem> { new ControlPickItem { Id = "x", Label = "X", StatusShape = "hexagon" } };
            Assert.StartsWith("invalid pick item shape", (await Send("pick.open", args: new ControlArgs { Items = bad })).Error);
            Assert.True((await Send("pick.open", args: new ControlArgs { Items = new List<ControlPickItem>(), AllowCustom = true })).Ok);
        }

        [Fact]
        public async Task OnePickerAtATimeAndItsAnswerStays()
        {
            string id = (await Send("pick.open", args: new ControlArgs { Items = Items("Staging", "Production") })).Result!.Id!;
            Assert.Equal("pick already pending", (await Send("pick.open", args: new ControlArgs { Items = Items("X") })).Error);
            Assert.Equal("pick already pending", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("ok"), Style = "gui" })).Error);
            Assert.Equal("pending", (await Send("pick.result", id)).Result!.Pick!.Result);

            Assert.True(model.AnswerPick(id, "production", null));
            ControlPickResult picked = (await Send("pick.result", id)).Result!.Pick!;
            Assert.Equal(("picked", "production", "Production", 1), (picked.Result, picked.Id, picked.Label, picked.Index));
            Assert.True((await Send("pick.cancel", id)).Ok);
            Assert.Equal("picked", (await Send("pick.result", id)).Result!.Pick!.Result);
            Assert.Equal("unknown pick: nope", (await Send("pick.result", "nope")).Error);
        }

        [Fact]
        public async Task CustomAnswersOnlyWhenAllowed()
        {
            string strict = (await Send("pick.open", args: new ControlArgs { Items = Items("A") })).Result!.Id!;
            model.AnswerPick(strict, null, "typed");
            Assert.Equal("cancelled", model.PickResult(strict)!.Result);

            string open = (await Send("pick.open", args: new ControlArgs { Items = Items("A"), AllowCustom = true })).Result!.Id!;
            model.AnswerPick(open, null, "typed");
            Assert.Equal(("custom", "typed"), (model.PickResult(open)!.Result, model.PickResult(open)!.Query));
        }

        private static List<ControlAskButton> Buttons(params string[] ids)
        {
            return ids.Select(id => new ControlAskButton { Id = id, Label = id.ToUpperInvariant() }).ToList();
        }

        [Fact]
        public async Task AskChecksItsArguments()
        {
            Assert.Equal("ask.open requires a title", (await Send("ask.open", args: new ControlArgs { Title = " ", Buttons = Buttons("ok") })).Error);
            Assert.Equal("ask.open requires buttons", (await Send("ask.open", args: new ControlArgs { Title = "Q" })).Error);
            Assert.Equal("too many buttons (max 6)", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("a", "b", "c", "d", "e", "f", "g") })).Error);
            Assert.Equal("unknown default button: z", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("a"), DefaultButton = "z" })).Error);
            Assert.Equal("default button must not be destructive", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("a", "b"), DefaultButton = "a", DestructiveButton = "a" })).Error);
            Assert.Equal("unknown style", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("a"), Style = "fancy" })).Error);
            Assert.Equal("width must be 10 to 100", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("a"), Width = 5 })).Error);
            Assert.Equal("input and choices are exclusive", (await Send("ask.open", args: new ControlArgs { Title = "Q", Input = "line", Choices = new List<ControlAskChoice> { new ControlAskChoice { Id = "a", Label = "A" } } })).Error);
            Assert.Equal("placeholder requires input", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("a"), Placeholder = "x" })).Error);
            Assert.Equal("radio choices accept one checked id", (await Send("ask.open", args: new ControlArgs
            {
                Title = "Q",
                Choices = new List<ControlAskChoice> { new ControlAskChoice { Id = "a", Label = "A" }, new ControlAskChoice { Id = "b", Label = "B" } },
                Checked = new List<string> { "a", "b" },
            })).Error);
            List<ControlAskButton> keyed = new List<ControlAskButton> { new ControlAskButton { Id = "a", Label = "A", Hotkey = "1" } };
            Assert.Equal("ask button hotkey must be one ASCII letter", (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = keyed })).Error);
        }

        [Fact]
        public async Task TerminalAskWaitsInItsSessionUntilAnswered()
        {
            string id = (await Send("ask.open", args: new ControlArgs { Title = "Deploy?", Buttons = Buttons("yes", "no"), CancelButton = "no" })).Result!.Id!;
            Assert.Same(model.TerminalAskOf(session.Id), model.TerminalAskOf(session.Id));
            Assert.Equal("ask already pending", (await Send("ask.open", args: new ControlArgs { Title = "Again?", Buttons = Buttons("ok") })).Error);
            Assert.Equal(id, (await Send("tree")).Result!.Tree!.Workspaces.Single(w => w.Id == work.Id.Wire()).Sessions.Single().Ask!.Id);

            model.AnswerAsk(id, "yes", null, null);
            ControlAskResult answered = model.AskResult(id)!;
            Assert.Equal(("answered", "yes", "YES", 0), (answered.Result, answered.Id, answered.Label, answered.Index));
            Assert.Null(model.TerminalAskOf(session.Id));

            string second = (await Send("ask.open", args: new ControlArgs { Title = "Sure?", Buttons = Buttons("yes", "no"), CancelButton = "no" })).Result!.Id!;
            model.AnswerAsk(second, "no", null, null);
            Assert.Equal("escaped", model.AskResult(second)!.Result);
        }

        [Fact]
        public async Task FormsGetOkCancelAndReturnTheirValues()
        {
            string field = (await Send("ask.open", args: new ControlArgs { Title = "Name?", Input = "line", Value = "x" })).Result!.Id!;
            model.AnswerAsk(field, "ok", "agshell", null);
            Assert.Equal(("answered", "agshell"), (model.AskResult(field)!.Result, model.AskResult(field)!.Text));

            List<ControlAskChoice> choices = new List<ControlAskChoice> { new ControlAskChoice { Id = "a", Label = "A" }, new ControlAskChoice { Id = "b", Label = "B" } };
            string list = (await Send("ask.open", args: new ControlArgs { Title = "Which?", Choices = choices, Multiple = true, Other = "something else" })).Result!.Id!;
            model.AnswerAsk(list, "ok", "c", new[] { "b", "a" });
            Assert.Equal(new[] { "a", "b" }, model.AskResult(list)!.Selected);
            Assert.Equal("c", model.AskResult(list)!.Text);
        }

        [Fact]
        public async Task ClosingTheSessionCancelsItsQuestion()
        {
            string id = (await Send("ask.open", args: new ControlArgs { Title = "Q", Buttons = Buttons("ok") })).Result!.Id!;
            model.RemoveSession(session.Id);
            Assert.Equal("cancelled", model.AskResult(id)!.Result);
        }

        [Fact]
        public async Task AnchoredGuiAskEndsWhenAnotherSessionIsSelected()
        {
            Session other = model.AddSession(work.Id, "cmd", null);
            model.SelectSession(session.Id);
            string id = (await Send("ask.open", session.Id.Wire(), new ControlArgs { Title = "Q", Buttons = Buttons("ok"), Style = "gui" })).Result!.Id!;
            Assert.Equal(id, model.GuiAsk!.Id);

            model.SelectSession(other.Id);
            Assert.Equal("cancelled", model.AskResult(id)!.Result);
            Assert.Null(model.GuiAsk);
        }

        [Fact]
        public void AGuiQuestionOverAPaneFollowsItAndGoesWithIt()
        {
            model.SelectSession(session.Id);
            model.SetSplit(session.Id, true);
            AskRequest ask = new AskRequest { Id = "g", Title = "Q", Buttons = Buttons("ok"), Style = "gui", Align = "center", SessionId = session.Id, Pane = PaneRole.Split };
            Assert.Null(model.OpenAsk(ask));

            model.PromoteSplit(session.Id);
            Assert.Equal(PaneRole.Main, model.GuiAsk!.Pane);

            model.SetSplit(session.Id, true);
            model.CloseSplit(session.Id);
            Assert.Equal("g", model.GuiAsk!.Id);
            model.SetSplit(session.Id, true);
            model.SwapPanes(session.Id);
            Assert.Equal(PaneRole.Split, model.GuiAsk!.Pane);
            model.CloseSplit(session.Id);
            Assert.Null(model.GuiAsk);
            Assert.Equal("cancelled", model.AskResult("g")!.Result);
        }

        [Fact]
        public void AGuiQuestionOverAPaneOffScreenIsCancelled()
        {
            model.SelectSession(session.Id);
            model.SetSplit(session.Id, true);
            Assert.Null(model.OpenAsk(new AskRequest { Id = "g", Title = "Q", Buttons = Buttons("ok"), Style = "gui", Align = "center", SessionId = session.Id, Pane = PaneRole.Split }));
            model.CancelGuiAskOffAnchor();
            Assert.NotNull(model.GuiAsk);

            model.FocusPane(session.Id, PaneRole.Main);
            model.SetSplit(session.Id, false);
            model.CancelGuiAskOffAnchor();
            Assert.Null(model.GuiAsk);
        }

        [Fact]
        public void AnsweredPickersAreKeptForTheLatest8PerWindow()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < AppModel.RetainedPickResults + 1; i++)
            {
                PickRequest pick = new PickRequest { Id = "p" + i, Items = Items("A") };
                model.OpenPick(pick);
                model.CancelPick(pick.Id);
                ids.Add(pick.Id);
            }

            Assert.Null(model.PickResult(ids[0]));
            Assert.NotNull(model.PickResult(ids[^1]));
        }

        [Fact]
        public void FinishedQuestionsAreKeptForTheLatest32AcrossWindows()
        {
            AskResultLog shared = new AskResultLog();
            AppModel other = new AppModel();
            model.ShareAskResults(shared);
            other.ShareAskResults(shared);
            Session session = model.AddSession(model.AddWorkspace("w").Id, "pwsh", null);
            Session elsewhere = other.AddSession(other.AddWorkspace("w").Id, "pwsh", null);
            model.OpenAsk(new AskRequest { Id = "first", Title = "Q", Buttons = Buttons("ok"), SessionId = session.Id, Style = "terminal", Align = "center" });
            model.CancelAsk("first");
            for (int i = 0; i < AskResultLog.RetainedResults; i++)
            {
                other.OpenAsk(new AskRequest { Id = "a" + i, Title = "Q", Buttons = Buttons("ok"), SessionId = elsewhere.Id, Style = "terminal", Align = "center" });
                other.CancelAsk("a" + i);
            }

            Assert.Null(model.AskResult("first"));
            Assert.NotNull(other.AskResult("a0"));
            Assert.Null(model.AskResult("a0"));
        }
    }
}
