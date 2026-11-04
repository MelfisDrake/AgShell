using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AgShell.Core;
using Microsoft.Web.WebView2.Core;

namespace AgShell.App.Tests
{
    public partial class NativePagesBridgeTests
    {
        [Theory]
        [InlineData("InitializeAsync", false, true)]
        [InlineData("InitializeAsync", true, true)]
        [InlineData("ApplyThemeAsync", false, true)]
        [InlineData("ApplyThemeAsync", true, true)]
        [InlineData("ApplyThemeAsync", false, false)]
        public async Task ClosingDuringInitializationDoesNotResumeADisposedBrowser(string method, bool js, bool local)
        {
            TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new Thread(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await CheckAsync(dispatcher, method, js, local);
                        completion.SetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.SetException(exception);
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                });
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        }

        private static async Task CheckAsync(Dispatcher dispatcher, string method, bool js, bool local)
        {
            string root = Path.Combine(Path.GetTempPath(), "agshell-lifetime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Canvas canvas = new Canvas();
            Window window = new Window { Content = canvas, Width = 640, Height = 480, ShowActivated = false, ShowInTaskbar = false };
            NativePages? pages = null;
            List<Exception> unhandled = new List<Exception>();
            dispatcher.UnhandledException += (sender, args) =>
            {
                unhandled.Add(args.Exception);
                args.Handled = true;
            };
            ContinuationGate gate = new ContinuationGate(dispatcher, method);
            try
            {
                string file = Path.Combine(root, "index.html");
                File.WriteAllText(file, "<!doctype html><title>Lifetime</title>");
                string sourceRoot = AppContext.BaseDirectory;
                while (!File.Exists(Path.Combine(sourceRoot, "agshell.slnx")))
                {
                    sourceRoot = Path.GetDirectoryName(sourceRoot)!;
                }

                PageHost host = new PageHost(File.ReadAllText(Path.Combine(sourceRoot, "src", "web", "public", "page-bridge.js")));
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"));
                string id = Guid.NewGuid().ToString("N");
                SessionOverlay? overlay = new SessionOverlay { Id = id, Page = new PageSpec { Source = local ? file : "about:blank", IsFile = local, Js = js } };
                pages = new NativePages(canvas, environment, candidate => candidate == id ? overlay : null, host, () => new WindowInteropHelper(window).Handle);
                window.Show();
                SynchronizationContext? previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(gate);
                try
                {
                    pages.Place(id, new Rect(0, 0, 600, 400), true, true, false);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }

                await gate.Paused.Task.WaitAsync(TimeSpan.FromSeconds(15));
                overlay = null;
                pages.Retain(Array.Empty<string>());
                Assert.Empty(canvas.Children);
                gate.Release();
                await gate.PlaceCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(pages.Realized(id));
                Assert.Empty(canvas.Children);
                Assert.Empty(unhandled);
            }
            finally
            {
                gate.Release();
                pages?.Retain(Array.Empty<string>());
                window.Close();
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    try
                    {
                        Directory.Delete(root, true);
                        break;
                    }
                    catch (IOException) when (attempt < 49)
                    {
                        await Task.Delay(100);
                    }
                }
            }
        }

        private sealed class ContinuationGate(Dispatcher dispatcher, string method) : SynchronizationContext
        {
            private Action? held;
            private bool paused;
            private int operations;
            public TaskCompletionSource Paused { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource PlaceCompleted { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            public override SynchronizationContext CreateCopy() => this;

            public override void OperationStarted() => Interlocked.Increment(ref operations);

            public override void OperationCompleted()
            {
                if (Interlocked.Decrement(ref operations) == 0)
                {
                    PlaceCompleted.TrySetResult();
                }
            }

            public override void Post(SendOrPostCallback callback, object? state)
            {
                dispatcher.BeginInvoke(() =>
                {
                    string target = (state as Action)?.Target?.GetType().FullName ?? string.Empty;
                    Action run = () =>
                    {
                        SynchronizationContext? previous = Current;
                        SetSynchronizationContext(this);
                        try
                        {
                            callback(state);
                        }
                        finally
                        {
                            SetSynchronizationContext(previous);
                        }
                    };
                    if (!paused && target.Contains("NativePages+<" + method + ">", StringComparison.Ordinal))
                    {
                        paused = true;
                        held = run;
                        Paused.TrySetResult();
                    }
                    else
                    {
                        run();
                    }
                });
            }

            public void Release()
            {
                Action? continuation = held;
                held = null;
                continuation?.Invoke();
            }
        }
    }
}
