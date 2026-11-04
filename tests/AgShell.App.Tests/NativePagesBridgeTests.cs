using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AgShell.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgShell.App.Tests
{
    public partial class NativePagesBridgeTests
    {
        [Fact]
        public async Task LocalBridgeStaysWithItsLiveDocument()
        {
            TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await CheckAsync();
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

        private static async Task CheckAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "agshell-bridge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Canvas canvas = new Canvas();
            Window window = new Window { Content = canvas, Width = 640, Height = 480, ShowActivated = false, ShowInTaskbar = false };
            NativePages? pages = null;
            try
            {
                string file = Path.Combine(root, "index.html");
                File.WriteAllText(file, "<html><body><button type='button' data-agshell='probe'>Probe</button></body></html>");
                string sourceRoot = AppContext.BaseDirectory;
                while (!File.Exists(Path.Combine(sourceRoot, "agshell.slnx")))
                {
                    sourceRoot = Path.GetDirectoryName(sourceRoot)!;
                }

                PageHost host = new PageHost(File.ReadAllText(Path.Combine(sourceRoot, "src", "web", "public", "page-bridge.js")));
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "profile"));
                Dictionary<string, SessionOverlay> overlays = new Dictionary<string, SessionOverlay>();
                pages = new NativePages(canvas, environment, id => overlays.GetValueOrDefault(id), host, () => new WindowInteropHelper(window).Handle);
                List<string> requests = new List<string>();
                pages.BridgeRequest += (id, sequence, command, target, args) => requests.Add(command);
                window.Show();
                foreach (bool js in new[] { false, true })
                {
                    string id = Guid.NewGuid().ToString("N");
                    overlays[id] = new SessionOverlay { Id = id, Page = new PageSpec { Source = file, IsFile = true, Js = js } };
                    pages.Place(id, new Rect(0, 0, 600, 400), true, true, false);
                    await UntilAsync(() => pages.LoadState(id)?.State == "loaded");
                    WebView2 view = Assert.Single(canvas.Children.OfType<WebView2>());
                    CoreWebView2 core = view.CoreWebView2;
                    string initial = core.Source;
                    List<string> messageSources = new List<string>();
                    List<string> frameMessages = new List<string>();
                    core.WebMessageReceived += (sender, args) => messageSources.Add(args.Source);
                    core.FrameCreated += (sender, args) => args.Frame.WebMessageReceived += (frame, message) => frameMessages.Add(message.Source);
                    requests.Clear();
                    await core.ExecuteScriptAsync("document.querySelector('button').click()");
                    await UntilAsync(() => requests.Count == 1);
                    Assert.Equal("probe", requests[0]);
                    await core.ExecuteScriptAsync("window.documentScriptRan = false; const script = document.createElement('script'); script.textContent = 'window.documentScriptRan = true'; document.body.append(script)");
                    Assert.Equal(js ? "true" : "false", await core.ExecuteScriptAsync("window.documentScriptRan"));
                    if (js)
                    {
                        requests.Clear();
                        await core.ExecuteScriptAsync("agshell.request('helper')");
                        await UntilAsync(() => requests.Count == 1);
                        Assert.Equal("helper", requests[0]);
                        requests.Clear();
                        await core.ExecuteScriptAsync("const f = document.createElement('iframe'); f.srcdoc = `<script>window.chrome.webview.postMessage({agshellPage:{type:'request',id:7,cmd:'frame'}});<\\/script>`; document.body.append(f)");
                        await UntilAsync(() => frameMessages.Count == 1);
                        Assert.Empty(requests);
                    }

                    requests.Clear();
                    await core.ExecuteScriptAsync("location.href='https://example.invalid/foreign'");
                    await Task.Delay(400);
                    Assert.Equal(initial, core.Source);
                    Assert.Empty(requests);
                    await core.ExecuteScriptAsync("location.href='about:blank'");
                    await UntilAsync(() => core.Source == "about:blank");
                    await core.ExecuteScriptAsync("window.chrome.webview.postMessage({agshellPage:{type:'request',id:8,cmd:'about'}})");
                    await UntilAsync(() => messageSources.Contains("about:blank"));
                    Assert.Empty(requests);
                    await core.ExecuteScriptAsync("window.replySeen = false; window.chrome.webview.addEventListener('message', () => window.replySeen = true)");
                    pages.ReplyRaw(id, 8, "{\"ok\":true}");
                    await Task.Delay(100);
                    Assert.Equal("false", await core.ExecuteScriptAsync("window.replySeen"));
                    core.Navigate(initial + "#fragment");
                    await UntilAsync(() => core.Source.EndsWith("#fragment", StringComparison.Ordinal) && pages.LoadState(id)?.State == "loaded");
                    await core.ExecuteScriptAsync("document.querySelector('button').click()");
                    await UntilAsync(() => requests.Count == 1);
                    Assert.Equal("probe", requests[0]);
                    requests.Clear();
                    overlays.Remove(id);
                    int received = messageSources.Count;
                    await core.ExecuteScriptAsync("window.chrome.webview.postMessage({agshellPage:{type:'request',id:9,cmd:'closed'}})");
                    await UntilAsync(() => messageSources.Count > received);
                    Assert.Empty(requests);
                    pages.Retain(Array.Empty<string>());
                    host.Retain(Array.Empty<string>());
                    Assert.False(pages.Realized(id));
                    Assert.Empty(canvas.Children);
                }
            }
            finally
            {
                pages?.Retain(Array.Empty<string>());
                window.Close();
                // Chromium releases its profile asynchronously after the last controller closes.
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

        private static async Task UntilAsync(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("Isolated WebView2 condition was not reached: " + expression);
                }

                await Task.Delay(25);
            }
        }
    }
}
