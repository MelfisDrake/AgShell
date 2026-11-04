using System.IO;
using System.Reflection;
using AgShell.Core;
using AgShell.Protocol;

namespace AgShell.App.Tests
{
    public sealed class ClosedModalRoutingTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "agshell-closed-modal-" + Guid.NewGuid().ToString("N"));

        public void Dispose() => Directory.Delete(directory, true);

        [Theory]
        [InlineData("ask")]
        [InlineData("pick")]
        public async Task PollAndCancelAfterCloseStayScopedToTheOwningWindow(string family)
        {
            Directory.CreateDirectory(directory);
            WindowLibrary library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            List<WindowEntry> entries = (List<WindowEntry>)typeof(WindowLibrary).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(library)!;
            WindowEntry owner = new WindowEntry { Open = false };
            WindowEntry other = new WindowEntry { Open = false };
            entries.Add(owner);
            entries.Add(other);
            AppModel model = new AppModel();
            model.ShareAskResults(library.AskResults);
            if (family == "ask")
            {
                Assert.Null(model.OpenAsk(new AskRequest
                {
                    Id = "dialog", Title = "fixture", Style = "gui", Align = "center",
                    Buttons = new[] { new ControlAskButton { Id = "ok", Label = "OK" } },
                }));
            }
            else
            {
                Assert.Null(model.OpenPick(new PickRequest { Id = "dialog", Items = new[] { new ControlPickItem { Id = "a", Label = "A" } } }));
            }

            model.CancelAllModals();
            library.KeepModalResults(owner, model);
            foreach (string selector in new[] { owner.Id.ToString(), owner.Id.ToString()[..8] })
            {
                ControlResponse result = await Dispatch(library, family + ".result", selector);
                Assert.True(result.Ok, result.Error);
                Assert.Equal("cancelled", family == "ask" ? result.Result!.Ask!.Result : result.Result!.Pick!.Result);
                Assert.True((await Dispatch(library, family + ".cancel", selector)).Ok);
            }

            foreach (string action in new[] { ".result", ".cancel" })
            {
                ControlResponse wrong = await Dispatch(library, family + action, other.Id.ToString());
                Assert.False(wrong.Ok);
                Assert.Equal("unknown " + family + ": dialog", wrong.Error);
            }

            Assert.True((await Dispatch(library, family + ".result", null)).Ok);
        }

        private static Task<ControlResponse> Dispatch(WindowLibrary library, string command, string? window)
        {
            ControlRequest request = new ControlRequest { Cmd = command, Target = "dialog", Args = window is null ? null : new ControlArgs { Window = window } };
            return (Task<ControlResponse>)typeof(WindowLibrary).GetMethod("HandleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(library, new object?[] { request, null })!;
        }
    }
}
