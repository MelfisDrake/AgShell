using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using AgShell.Core;
using AgShell.Protocol;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AgShell.App
{
    /// <summary>
    /// Web pages (overlay --url) shown as top-level browsers, as agterm shows them in WKWebView, rather than in frames: any site
    /// opens, logins work, and history can go back and forward. A page keeps its cookies and storage only while it is open,
    /// in a private profile of its own, unless opened with --persistent, which uses one saved profile shared by such pages.
    /// </summary>
    internal sealed class NativePages
    {
        private const string PersistentProfile = "agshell-pages";

        private readonly Canvas layer;
        private readonly CoreWebView2Environment environment;
        private readonly Func<string, SessionOverlay?> findPage;
        private readonly PageHost? pageHost;
        private readonly Func<IntPtr> interfaceWindow;
        private readonly Dictionary<string, NativePage> pages = new Dictionary<string, NativePage>();
        private double zoom = 1;
        private TerminalTheme? theme;

        public NativePages(Canvas layer, CoreWebView2Environment environment, Func<string, SessionOverlay?> findPage, PageHost? pageHost, Func<IntPtr> interfaceWindow)
        {
            this.layer = layer;
            this.interfaceWindow = interfaceWindow;
            this.environment = environment;
            this.findPage = findPage;
            this.pageHost = pageHost;
            if (pageHost is not null)
            {
            }
        }

        /// <summary>A local page's bridge asked for an agshell command: overlay id, request id, command, target, args as JSON.</summary>
        public event Action<string, int, string, string?, string?>? BridgeRequest;

        /// <summary>Hands a command's answer back to the local page whose bridge asked for it.</summary>
        public void Reply(string overlayId, int requestId, ControlResponse response)
        {
            ReplyRaw(overlayId, requestId, ControlJson.Serialize(response));
        }

        /// <summary>
        /// A response as JSON, passed on as it is: agterm hands a page the reply of the machine it was opened on as received,
        /// so a field this machine does not know still reaches it.
        /// </summary>
        public void ReplyRaw(string overlayId, int requestId, string responseJson)
        {
            if (!pages.TryGetValue(overlayId, out NativePage? page) || page.View.CoreWebView2 is not CoreWebView2 core || !CanBridge(page, core.Source))
            {
                return;
            }

            string ok = "false";
            string result = string.Empty;
            string error = ",\"error\":\"invalid reply\"";
            try
            {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(responseJson);
                System.Text.Json.JsonElement root = document.RootElement;
                ok = root.TryGetProperty("ok", out System.Text.Json.JsonElement okay) && okay.ValueKind == System.Text.Json.JsonValueKind.True ? "true" : "false";
                result = root.TryGetProperty("result", out System.Text.Json.JsonElement found) ? ",\"result\":" + found.GetRawText() : string.Empty;
                error = root.TryGetProperty("error", out System.Text.Json.JsonElement failure) ? ",\"error\":" + failure.GetRawText() : string.Empty;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            core.PostWebMessageAsJson("{\"agshellPage\":{\"type\":\"reply\",\"id\":" + requestId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"ok\":" + ok + result + error + "}}");
        }

        /// <summary>The page called window.close(): its overlay closes, as the strip's close button closes it.</summary>
        public event Action<string>? CloseRequested;

        /// <summary>What the page shows, its title, load state or history changed: the overlay id.</summary>
        public event Action<string>? Changed;

        /// <summary>A page took the keyboard, as a click on it gives it: the overlay id.</summary>
        public event Action<string>? Focused;

        /// <summary>A page that had the keyboard was covered and hidden; the interface takes the keyboard back.</summary>
        public event Action? KeyboardDropped;

        /// <summary>An agshell shortcut pressed while a page had focus, for the interface to handle.</summary>
        public event Action<ForwardedKey>? KeyForwarded;

        /// <summary>
        /// Places a page where the interface left room for it, creating its browser on first use. A page covered by something of
        /// the interface's own (a palette, a menu) stays shown under the interface's browser, which leaves a clear hole there,
        /// so the overlay draws over the live page as agterm's.
        /// </summary>
        public async void Place(string overlayId, Rect bounds, bool visible, bool raised, bool focus, double radius = 0, bool roundTop = false)
        {
            if (!pages.TryGetValue(overlayId, out NativePage? page))
            {
                // A local page needs the host that serves its file with the bridge; without one there is nothing to show.
                if (findPage(overlayId) is not { Page: PageSpec spec } overlay || (spec.IsFile && pageHost is null))
                {
                    return;
                }

                page = new NativePage(overlayId, spec, overlay.BackgroundColor);
                pages[overlayId] = page;
                layer.Children.Add(page.View);
                await InitializeAsync(page);
            }

            Canvas.SetLeft(page.View, bounds.X);
            Canvas.SetTop(page.View, bounds.Y);
            page.View.Width = Math.Max(0, bounds.Width);
            page.View.Height = Math.Max(0, bounds.Height);
            raised = visible && raised;
            // Covered while it has the keyboard (the Ctrl+Tab switcher opened from it): the keys that end what covers it
            // must reach the interface, as agterm's app-wide key monitors take them from a focused web view.
            bool dropsKeyboard = !raised && page.Raised && HoldsKeyboard();
            page.View.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            page.Raised = raised;
            if (!raised)
            {
                page.EndPrompt();
            }

            if (dropsKeyboard)
            {
                KeyboardDropped?.Invoke();
            }

            Order(page);
            Round(page, radius, roundTop);
            // A toolbar popup can uncover an inactive page; WebView2.Focus would raise its owner again.
            if (raised && focus && page.Ready && GetForegroundWindow() == GetAncestor(interfaceWindow(), 2))
            {
                page.View.Focus();
            }
        }

        /// <summary>Where the pages over the interface's browser are, in the layer's coordinates: what a HUD must be drawn above.</summary>
        public IEnumerable<Rect> RaisedBounds()
        {
            return pages.Values.Where(page => page.Raised && page.View.Visibility == Visibility.Visible)
                .Select(page => new Rect(Canvas.GetLeft(page.View), Canvas.GetTop(page.View), page.View.Width, page.View.Height));
        }

        /// <summary>Closes the browsers of pages that are gone; a private profile forgets everything with its last browser.</summary>
        /// <returns>The pages whose browsers closed.</returns>
        public List<string> Retain(IEnumerable<string> overlayIds)
        {
            HashSet<string> live = overlayIds.ToHashSet();
            List<string> gone = pages.Keys.Where(id => !live.Contains(id)).ToList();
            foreach (string id in gone)
            {
                NativePage page = pages[id];
                pages.Remove(id);
                page.EndPrompt();
                layer.Children.Remove(page.View);
                page.View.Dispose();
            }

            return gone;
        }

        /// <summary>Drives a page; agterm's refusal when history has nowhere to go or the page was never shown, else null.</summary>
        public string? Command(string overlayId, string action, string? url)
        {
            if (!pages.TryGetValue(overlayId, out NativePage? page) || page.View.CoreWebView2 is not CoreWebView2 core)
            {
                return action is "back" or "forward" ? NotRealized : null;
            }

            switch (action)
            {
                case "reload":
                    core.Reload();
                    break;
                case "back":
                    if (!core.CanGoBack)
                    {
                        return "no page to go back to";
                    }

                    core.GoBack();
                    break;
                case "forward":
                    if (!core.CanGoForward)
                    {
                        return "no page to go forward to";
                    }

                    core.GoForward();
                    break;
                case "navigate" when url is not null:
                    page.Origin = OriginOf(url);
                    core.Navigate(url);
                    break;
                case "original" when url is not null:
                    // As agterm's reload of the source: what the page showed is forgotten until the source shows again.
                    page.Committed = false;
                    page.ShownUrl = null;
                    page.ShownTitle = null;
                    page.State = "loading";
                    page.Error = null;
                    Changed?.Invoke(page.OverlayId);
                    core.Navigate(url);
                    break;
            }

            return null;
        }

        /// <summary>agterm's refusal for a page whose browser does not exist yet: it has never been on screen.</summary>
        /// <summary>The site a web page is kept to now: where it was opened, or where navigate --url took it.</summary>
        public string? SiteOf(string overlayId)
        {
            return pages.TryGetValue(overlayId, out NativePage? page) && page.Origin.Length > 0 ? page.Origin : null;
        }

        public const string NotRealized = "html overlay not realized";

        public bool Realized(string overlayId)
        {
            return pages.TryGetValue(overlayId, out NativePage? page) && page.View.CoreWebView2 is not null;
        }

        public string? CurrentUrl(string overlayId)
        {
            return pages.TryGetValue(overlayId, out NativePage? page) ? page.View.CoreWebView2?.Source : null;
        }

        /// <summary>
        /// How the page's last navigation went and, once a document of it shows, its address, title and history, as agterm's
        /// current page; null for a page not shown here.
        /// </summary>
        public (string State, string? Error, string? Url, string? Title, bool? CanGoBack, bool? CanGoForward)? LoadState(string overlayId)
        {
            if (!pages.TryGetValue(overlayId, out NativePage? page))
            {
                return null;
            }

            CoreWebView2? core = page.View.CoreWebView2;
            if (page.ShownUrl is null || core is null)
            {
                return (page.State, page.Error, null, null, null, null);
            }

            return (page.State, page.Error, page.ShownUrl, string.IsNullOrEmpty(page.ShownTitle) ? null : page.ShownTitle, core.CanGoBack, core.CanGoForward);
        }

        /// <summary>
        /// agterm's theme refresh: a local page wears the new theme and reloads what it shows; a web page gets the new colors
        /// at its next load. Nothing runs in the live document.
        /// </summary>
        public void SetTheme(TerminalTheme theme)
        {
            this.theme = theme;
            foreach (NativePage page in pages.Values.Where(page => page.Ready))
            {
                _ = ApplyThemeAsync(page);
            }
        }

        private PageTheme ThemeOf(string overlayId)
        {
            string? background = pages.TryGetValue(overlayId, out NativePage? page) ? page.BackgroundColor : null;
            return PageTheme.Of(theme ?? Themes.Parse(Themes.DefaultName, "background = #1b1a18\nforeground = #e6e1d7")!, background);
        }

        private async Task ApplyThemeAsync(NativePage page)
        {
            if (page.View.CoreWebView2 is not CoreWebView2 core)
            {
                return;
            }

            PageTheme look = ThemeOf(page.OverlayId);
            string style = look.Stylesheet(themed: page.Spec.IsFile);
            if (style == page.AppliedStyle)
            {
                return;
            }

            bool first = page.AppliedStyle is null;
            page.AppliedStyle = style;
            // A web app styled against the browser's own canvas keeps it; a local page shows the theme behind it.
            page.View.DefaultBackgroundColor = page.Spec.IsFile ? System.Drawing.ColorTranslator.FromHtml(look.Background) : System.Drawing.Color.White;
            if (page.ThemeScript is string previous)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(previous);
            }

            // A local page's own styles always win over the theme, as agterm's: the theme sits in a cascade layer, which any
            // style of the page outranks.
            string json = System.Text.Json.JsonSerializer.Serialize(page.Spec.IsFile ? "@layer agshell-theme { " + style + " }" : style);
            page.ThemeScript = await core.AddScriptToExecuteOnDocumentCreatedAsync(
                // The document has no root element yet when this runs, so the rule goes in as an adopted sheet.
                "(() => { const sheet = new CSSStyleSheet(); sheet.replaceSync(" + json + "); document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]; })();");
            if (page.Spec.IsFile && !first)
            {
                core.Reload();
            }
        }

        public void SetZoom(double factor)
        {
            zoom = factor;
            foreach (NativePage page in pages.Values)
            {
                page.View.ZoomFactor = factor;
            }
        }

        /// <summary>Forgets what pages opened with --persistent have kept.</summary>
        public async Task ClearPersistentAsync()
        {
            // No persistent page is open (the caller refuses otherwise), so a hidden browser on that profile does the clearing.
            WebView2 helper = new WebView2 { Visibility = Visibility.Hidden, Width = 1, Height = 1 };
            layer.Children.Add(helper);
            try
            {
                CoreWebView2ControllerOptions options = environment.CreateCoreWebView2ControllerOptions();
                options.ProfileName = PersistentProfile;
                await helper.EnsureCoreWebView2Async(environment, options);
                await helper.CoreWebView2.Profile.ClearBrowsingDataAsync();
            }
            finally
            {
                layer.Children.Remove(helper);
                helper.Dispose();
            }
        }

        private async Task InitializeAsync(NativePage page)
        {
            CoreWebView2ControllerOptions options = environment.CreateCoreWebView2ControllerOptions();
            if (page.Spec.Persistent)
            {
                options.ProfileName = PersistentProfile;
            }
            else
            {
                options.ProfileName = "agshell-page-" + page.OverlayId;
                options.IsInPrivateModeEnabled = true;
            }

            try
            {
                await page.View.EnsureCoreWebView2Async(environment, options);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
            {
                return;
            }

            CoreWebView2 core = page.View.CoreWebView2;
            // A local page always runs the bridge; without --js its documents' Content-Security-Policy runs none of their own scripts.
            core.Settings.IsScriptEnabled = page.Spec.Js || page.Spec.IsFile;
            if (page.Spec.IsFile && pageHost is not null)
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(pageHost.BridgeScript);
            }

            // agterm's runOpenPanelWith answers nil: a page opens no file chooser. WebView2 has no event for the chooser of a
            // file input, so every document refuses it itself: a click on a file input or a label for one, a key that would
            // open it, and showPicker.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FileChooserGuard);
            string start = page.Spec.Source;
            if (page.Spec.IsFile && pageHost is not null)
            {
                pageHost.Attach(core, environment);
                start = pageHost.PageUrl(page.OverlayId, page.Spec);
                page.Origin = OriginOf(start);
                core.WebMessageReceived += (sender, args) => OnBridgeMessage(page, args);
            }

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // As agterm's pages: no alert, confirm or prompt, no camera or microphone, and no files dropped in.
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.ScriptDialogOpening += (sender, args) =>
            {
                // A page asking to stay would otherwise keep every navigation away from it.
                if (args.Kind == CoreWebView2ScriptDialogKind.Beforeunload)
                {
                    args.Accept();
                }
            };
            core.PermissionRequested += (sender, args) =>
            {
                // The file system access pickers are no part of agterm's page either.
                if (args.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.FileReadWrite)
                {
                    args.State = CoreWebView2PermissionState.Deny;
                }
            };
            // agterm refuses a drop or paste that carries files, which would hand the page their contents; a paste of text
            // goes through. A drop is refused whole: WebView2 takes external drops or not, with nothing to sort them by.
            page.View.AllowExternalDrop = false;
            core.ContextMenuRequested += (sender, args) =>
            {
                if (ClipboardHoldsFiles())
                {
                    foreach (CoreWebView2ContextMenuItem item in args.MenuItems.Where(item => item.Name is "paste" or "pasteAndMatchStyle").ToList())
                    {
                        args.MenuItems.Remove(item);
                    }
                }
            };
            page.View.ZoomFactor = zoom;
            await ApplyThemeAsync(page);
            // agterm's current page: what a document of the page shows. Chromium's own error page is no document of it, so a
            // failed load leaves the last one in place, or none.
            void Report()
            {
                if (!page.OnErrorPage && page.Committed)
                {
                    page.ShownUrl = core.Source;
                    page.ShownTitle = core.DocumentTitle;
                }

                Changed?.Invoke(page.OverlayId);
            }

            core.DocumentTitleChanged += (sender, args) => Report();
            core.HistoryChanged += (sender, args) => Report();
            core.SourceChanged += (sender, args) => Report();
            core.ContentLoading += (sender, args) =>
            {
                page.OnErrorPage = args.IsErrorPage;
                page.Committed = true;
                Report();
            };
            core.NavigationStarting += (sender, args) =>
            {
                PageNavigationDecision decision = Decide(page, args.Uri, PageNavigationTarget.MainFrame, args.IsUserInitiated);
                if (decision == PageNavigationDecision.Allow)
                {
                    page.State = "loading";
                    page.Error = null;
                    Changed?.Invoke(page.OverlayId);
                    return;
                }

                args.Cancel = true;
                if (decision == PageNavigationDecision.OpenExternal)
                {
                    AskToOpen(page, args.Uri);
                }
                else if (!args.IsUserInitiated && page.State == "loading")
                {
                    // A load the page started on its own, which this policy stopped, ends failed rather than loading forever.
                    page.State = "failed";
                    page.Error = "navigation blocked: " + args.Uri;
                    Changed?.Invoke(page.OverlayId);
                }
            };
            core.FrameNavigationStarting += (sender, args) =>
            {
                PageNavigationDecision decision = Decide(page, args.Uri, PageNavigationTarget.Subframe, args.IsUserInitiated);
                if (decision != PageNavigationDecision.Allow)
                {
                    args.Cancel = true;
                    if (decision == PageNavigationDecision.OpenExternal)
                    {
                        AskToOpen(page, args.Uri);
                    }
                }
            };
            core.NavigationCompleted += (sender, args) =>
            {
                // A navigation this policy cancelled, or one the next replaced, is not a failed page.
                if (!args.IsSuccess && args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    return;
                }

                page.State = args.IsSuccess ? "loaded" : "failed";
                page.Error = args.IsSuccess ? null
                    // agterm shows the channel's refusal of a page handed over as the load's error.
                    : page.Spec.IsFile && args.HttpStatusCode == 404 && pageHost?.ReplicaError(page.OverlayId) is string reason ? reason
                    : page.Spec.IsFile && args.HttpStatusCode == 404 ? "no such file: " + (pageHost?.LocalPath(core.Source) ?? page.Spec.Source)
                    : Describe(args.WebErrorStatus);
                Changed?.Invoke(page.OverlayId);
            };
            core.ProcessFailed += (sender, args) =>
            {
                if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                {
                    page.State = "failed";
                    page.Error = "web content process terminated";
                    Changed?.Invoke(page.OverlayId);
                }
            };
            // Deferred: closing the overlay disposes the browser that is raising the event.
            core.WindowCloseRequested += (sender, args) => layer.Dispatcher.BeginInvoke(() => CloseRequested?.Invoke(page.OverlayId));
            core.NewWindowRequested += (sender, args) =>
            {
                args.Handled = true;
                if (Decide(page, args.Uri, PageNavigationTarget.NewWindow, args.IsUserInitiated) == PageNavigationDecision.OpenExternal)
                {
                    AskToOpen(page, args.Uri);
                }
            };
            // The WPF control turns the browser's accelerator keys into WPF key events; a handled one never reaches the page.
            page.View.PreviewKeyDown += OnPreviewKeyDown;
            page.View.GotFocus += (sender, args) => Focused?.Invoke(page.OverlayId);
            page.Ready = true;
            Order(page);
            core.Navigate(start);
        }

        /// <summary>A failed load in words, as agterm's pages report the system's description rather than a code.</summary>
        private static string Describe(CoreWebView2WebErrorStatus status)
        {
            return status switch
            {
                CoreWebView2WebErrorStatus.HostNameNotResolved => "A server with the specified hostname could not be found.",
                CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable => "Could not connect to the server.",
                CoreWebView2WebErrorStatus.Timeout => "The request timed out.",
                CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset => "The network connection was lost.",
                CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
                    or CoreWebView2WebErrorStatus.CertificateRevoked or CoreWebView2WebErrorStatus.CertificateIsInvalid => "The certificate for this server is invalid.",
                CoreWebView2WebErrorStatus.RedirectFailed => "Too many HTTP redirects.",
                CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => "The server returned an invalid response.",
                CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired or CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired => "Authentication is required.",
                CoreWebView2WebErrorStatus.Unknown => "The page could not be loaded.",
                _ => "The page could not be loaded (" + status + ").",
            };
        }

        private PageNavigationDecision Decide(NativePage page, string url, PageNavigationTarget target, bool userActivated)
        {
            return PageNavigation.Decide(url, target, userActivated, page.Spec, page.Origin, uri => pageHost?.LocalPath(uri.AbsoluteUri) is not null);
        }

        /// <summary>
        /// agterm's hand-off: a page shown on screen asks one question at a time, and a request while one is up is dropped. A
        /// declined question silences the page until real input comes, as a script can click links in a loop but cannot make
        /// native input. The page's own input goes to the browser's process, out of this one's sight, so any input the system
        /// saw since the decline counts.
        /// </summary>
        private void AskToOpen(NativePage page, string url)
        {
            if (page.Prompt is not null || !page.Raised || page.SilencedAt == LastInputTicks() || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || Window.GetWindow(layer) is not Window owner)
            {
                return;
            }

            page.SilencedAt = null;
            page.Prompt = LinkPrompt.Show(owner, uri, approved =>
            {
                page.Prompt = null;
                if (approved)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                }
                else
                {
                    page.SilencedAt = LastInputTicks();
                }
            });
        }

        /// <summary>A message from a local page's bridge; only requests for agshell commands are taken.</summary>
        private void OnBridgeMessage(NativePage page, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!CanBridge(page, args.Source))
            {
                return;
            }

            try
            {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson);
                if (!document.RootElement.TryGetProperty("agshellPage", out System.Text.Json.JsonElement message)
                    || message.GetProperty("type").GetString() != "request")
                {
                    return;
                }

                string? target = message.TryGetProperty("target", out System.Text.Json.JsonElement chosen) && chosen.ValueKind == System.Text.Json.JsonValueKind.String ? chosen.GetString() : null;
                string? arguments = message.TryGetProperty("args", out System.Text.Json.JsonElement raw) && raw.ValueKind == System.Text.Json.JsonValueKind.Object ? raw.GetRawText() : null;
                BridgeRequest?.Invoke(page.OverlayId, message.GetProperty("id").GetInt32(), message.GetProperty("cmd").GetString() ?? string.Empty, target, arguments);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is InvalidOperationException || ex is KeyNotFoundException || ex is FormatException)
            {
                // A message the bridge did not write: the page's own script, or a broken one.
            }
        }

        private bool CanBridge(NativePage page, string source)
        {
            return page.Spec.IsFile && pages.TryGetValue(page.OverlayId, out NativePage? live) && ReferenceEquals(page, live)
                && findPage(page.OverlayId)?.Page?.IsFile == true && page.View.CoreWebView2 is CoreWebView2 core
                && PageMessageSource.IsTrusted(source, core.Source, page.Origin, uri => pageHost?.LocalPath(uri.AbsoluteUri) is not null);
        }

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            System.Windows.Input.Key pressed = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            System.Windows.Input.ModifierKeys modifiers = System.Windows.Input.Keyboard.Modifiers;
            bool paste = (pressed == System.Windows.Input.Key.V && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                || (pressed == System.Windows.Input.Key.Insert && modifiers == System.Windows.Input.ModifierKeys.Shift);
            if (paste && ClipboardHoldsFiles())
            {
                e.Handled = true;
                return;
            }

            uint virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(pressed);
            if (ForwardedKey.From(virtualKey) is ForwardedKey key && key.IsAgShellShortcut)
            {
                e.Handled = true;
                KeyForwarded?.Invoke(key);
            }
        }

        /// <summary>
        /// WPF does not order sibling native windows: a page in front goes over the interface's browser, a covered one just under
        /// it, where it shows through the interface's hole while the interface takes the mouse.
        /// </summary>
        private void Order(NativePage page)
        {
            if (page.View.Handle != IntPtr.Zero)
            {
                SetWindowPos(page.View.Handle, page.Raised ? IntPtr.Zero : interfaceWindow(), 0, 0, 0, 0, NoMoveSizeActivate);
            }
        }

        /// <summary>
        /// agterm's clipShape on a floating panel: the page's browser window is cut to the panel's radius at its bottom corners,
        /// and at the top too without a strip, as the panel's rounded border would otherwise be crossed by its square corners.
        /// </summary>
        private void Round(NativePage page, double radius, bool roundTop)
        {
            IntPtr handle = page.View.Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (radius <= 0)
            {
                SetWindowRgn(handle, IntPtr.Zero, true);
                return;
            }

            double scale = PresentationSource.FromVisual(page.View)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            int width = (int)Math.Round(page.View.Width * scale);
            int height = (int)Math.Round(page.View.Height * scale);
            int diameter = (int)Math.Round(radius * 2 * scale);
            IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
            if (!roundTop)
            {
                IntPtr top = CreateRectRgn(0, 0, width, Math.Min(height, diameter));
                CombineRgn(region, region, top, CombineOr);
                DeleteObject(top);
            }

            // The window owns the region from here on.
            SetWindowRgn(handle, region, true);
        }

        /// <summary>Whether one of the pages has the keyboard now; their windows belong to the browser's process, not this one.</summary>
        public bool HoldsKeyboard()
        {
            GuiThreadInfo info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero)
            {
                return false;
            }

            return pages.Values.Any(page => page.View.Handle != IntPtr.Zero && (page.View.Handle == info.Focus || IsChild(page.View.Handle, info.Focus)));
        }

        private const string FileChooserGuard = """
            (() => {
              const chooser = (node) => {
                const input = node instanceof HTMLLabelElement ? node.control : node;
                return input instanceof HTMLInputElement && input.type === "file";
              };
              const refuse = (event) => {
                const path = event.composedPath ? event.composedPath() : [event.target];
                if (path.some(chooser) && (event.type === "click" || event.key === "Enter" || event.key === " ")) {
                  event.preventDefault();
                  event.stopImmediatePropagation();
                }
              };
              window.addEventListener("click", refuse, true);
              window.addEventListener("keydown", refuse, true);
              const showPicker = HTMLInputElement.prototype.showPicker;
              HTMLInputElement.prototype.showPicker = function () {
                if (this.type !== "file") {
                  return showPicker.call(this);
                }
              };
              const click = HTMLInputElement.prototype.click;
              HTMLInputElement.prototype.click = function () {
                if (this.type !== "file") {
                  click.call(this);
                }
              };
              window.showOpenFilePicker = window.showSaveFilePicker = window.showDirectoryPicker = undefined;
            })();
            """;

        /// <summary>Whether a paste now would bring files; a clipboard another program holds counts as none.</summary>
        private static bool ClipboardHoldsFiles()
        {
            try
            {
                return Clipboard.ContainsFileDropList();
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }

        /// <summary>When the user last pressed a key or moved the mouse anywhere, in system ticks.</summary>
        public static uint LastInputTicks()
        {
            LastInputInfo info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            return GetLastInputInfo(ref info) ? info.Time : 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Active;
            public IntPtr Focus;
            public IntPtr Capture;
            public IntPtr MenuOwner;
            public IntPtr MoveSize;
            public IntPtr Caret;
            public int CaretLeft;
            public int CaretTop;
            public int CaretRight;
            public int CaretBottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint Size;
            public uint Time;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        private const uint NoMoveSizeActivate = 0x0001 | 0x0002 | 0x0010;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        private const int CombineOr = 2;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

        private static string OriginOf(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.GetLeftPart(UriPartial.Authority) : string.Empty;
        }

        private sealed class NativePage
        {
            public NativePage(string overlayId, PageSpec spec, string? backgroundColor)
            {
                OverlayId = overlayId;
                Spec = spec;
                BackgroundColor = backgroundColor;
                Origin = OriginOf(spec.Source);
                View = new WebView2();
            }

            public string OverlayId { get; }

            public PageSpec Spec { get; }

            /// <summary>The overlay's --background-color, which replaces the theme background.</summary>
            public string? BackgroundColor { get; }

            /// <summary>The theme rule the page was last given; null before its first.</summary>
            public string? AppliedStyle { get; set; }

            /// <summary>The document-start script that gives a web page the theme variables.</summary>
            public string? ThemeScript { get; set; }

            public string Origin { get; set; }

            public WebView2 View { get; }

            public bool Ready { get; set; }

            /// <summary>Over the interface's browser; a covered page sits under it.</summary>
            public bool Raised { get; set; }

            /// <summary>agterm's page state: loading, loaded or failed, with the error of a failed load.</summary>
            public string State { get; set; } = "loading";

            public string? Error { get; set; }

            /// <summary>A document has shown since the page opened or its source was reloaded.</summary>
            public bool Committed { get; set; }

            /// <summary>The document shown now is the browser's page for a failed load.</summary>
            public bool OnErrorPage { get; set; }

            /// <summary>The address and title of the last document of the page itself; null before its first.</summary>
            public string? ShownUrl { get; set; }

            public string? ShownTitle { get; set; }

            /// <summary>Takes away the page's open question to the browser, when it has one.</summary>
            public Action? Prompt { get; set; }

            /// <summary>The input tick a declined question left the page silenced at; null while it may ask.</summary>
            public uint? SilencedAt { get; set; }

            public void EndPrompt()
            {
                Action? dismiss = Prompt;
                Prompt = null;
                dismiss?.Invoke();
            }
        }
    }

    /// <summary>A key pressed in a native page, described the way the interface's shortcut handler reads keys.</summary>
    internal sealed record ForwardedKey(string Code, string Key, bool Ctrl, bool Shift, bool Alt)
    {
        private static readonly string[] CtrlOnly = { "Tab", "KeyP", "Backquote", "Equal", "Minus", "Digit0", "NumpadAdd", "NumpadSubtract", "Numpad0" };

        /// <summary>The combinations agshell itself handles; everything else stays with the page (Ctrl+C, Ctrl+F, …).</summary>
        public bool IsAgShellShortcut => Ctrl && (Shift || Alt || CtrlOnly.Contains(Code));

        public static ForwardedKey? From(uint virtualKey)
        {
            bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            bool alt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;
            (string code, string key) = virtualKey switch
            {
                >= 0x41 and <= 0x5A => ("Key" + (char)virtualKey, ((char)virtualKey).ToString().ToLowerInvariant()),
                >= 0x30 and <= 0x39 => ("Digit" + (char)virtualKey, ((char)virtualKey).ToString()),
                0x09 => ("Tab", "Tab"),
                0x0D => ("Enter", "Enter"),
                0x1B => ("Escape", "Escape"),
                0x25 => ("ArrowLeft", "ArrowLeft"),
                0x26 => ("ArrowUp", "ArrowUp"),
                0x27 => ("ArrowRight", "ArrowRight"),
                0x28 => ("ArrowDown", "ArrowDown"),
                0xBB => ("Equal", "="),
                0xBD => ("Minus", "-"),
                0xC0 => ("Backquote", "`"),
                0x6B => ("NumpadAdd", "+"),
                0x6D => ("NumpadSubtract", "-"),
                0x60 => ("Numpad0", "0"),
                _ => (string.Empty, string.Empty),
            };
            return code.Length == 0 ? null : new ForwardedKey(code, key, ctrl, shift, alt);
        }
    }
}
