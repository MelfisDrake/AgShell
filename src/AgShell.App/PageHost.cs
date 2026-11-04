using System.IO;
using System.Security.Cryptography;
using System.Text;
using AgShell.Core;
using AgShell.Protocol;
using Microsoft.Web.WebView2.Core;

namespace AgShell.App
{
    /// <summary>
    /// Serves the local HTML files shown in page overlays. Each page gets its own host name, so it can read only its own file,
    /// or the directory it was opened with. The files go as they are; unless the page was opened with --js, a document's
    /// Content-Security-Policy runs none of its scripts, while the bridge, added to every document as it is created, still
    /// lets a page without JavaScript drive agshell through its data-agshell buttons.
    /// </summary>
    internal sealed class PageHost
    {
        /// <summary>Under localhost, as the window's own page: SmartScreen would hold every page's navigation about two seconds.</summary>
        public const string Domain = "agshell-page.localhost";

        private readonly Dictionary<string, PageEntry> pages = new Dictionary<string, PageEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Session background images, by a name derived from the path, served under their own host.</summary>
        private readonly Dictionary<string, string> images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Pages handed over by another machine, by overlay id: the channel their files come through, and once loaded the host
        /// the loopback server gives them.
        /// </summary>
        private readonly Dictionary<string, (RemotePageChannel Channel, string? Host)> sources = new Dictionary<string, (RemotePageChannel, string?)>();

        private ReplicaPageServer? replicaServer;

        private const string ImageHost = "bg." + Domain;
        private CoreWebView2Environment? environment;

        public PageHost(string bridgeScript)
        {
            BridgeScript = bridgeScript;
        }

        /// <summary>The script every local page's documents get as they are created: its data-agshell buttons and agshell.request.</summary>
        public string BridgeScript { get; }

        public void Attach(CoreWebView2 core, CoreWebView2Environment webViewEnvironment)
        {
            environment = webViewEnvironment;
            core.AddWebResourceRequestedFilter("https://*." + Domain + "/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += OnWebResourceRequested;
        }

        /// <summary>The address a page's browser loads: the file under the page's own host, or the web page itself.</summary>
        public string PageUrl(string overlayId, PageSpec page)
        {
            if (!page.IsFile)
            {
                return page.Source;
            }

            string file = PageFiles.AddressOf(page.Source, page.Root);
            if (sources.TryGetValue(overlayId, out (RemotePageChannel Channel, string? Host) replica))
            {
                replicaServer ??= new ReplicaPageServer();
                string replicaHost = replica.Host ?? replicaServer.Add(new ReplicaPageServer.Replica(replica.Channel, overlayId, page.Js));
                sources[overlayId] = (replica.Channel, replicaHost);
                pages[replicaHost] = new PageEntry(page.Source, page.Root, page.Js, overlayId);
                return "http://" + replicaHost + ":" + replicaServer.Port + file;
            }

            string host = HostOf(overlayId);
            pages[host] = new PageEntry(page.Source, page.Root, page.Js, overlayId);
            return "https://" + host + file;
        }

        /// <summary>Why a page handed over did not load: what the machine of its session answered, or null.</summary>
        public string? ReplicaError(string overlayId)
        {
            return sources.ContainsKey(overlayId) ? replicaServer?.DocumentError(overlayId) : null;
        }

        /// <summary>The channel of a page handed over, while it is open here.</summary>
        public RemotePageChannel? ChannelOf(string overlayId)
        {
            return sources.TryGetValue(overlayId, out (RemotePageChannel Channel, string? Host) replica) ? replica.Channel : null;
        }

        /// <summary>A page handed over: its files come from the machine it was opened on, through this channel.</summary>
        public void Serve(string overlayId, RemotePageChannel channel)
        {
            sources[overlayId] = (channel, null);
        }

        /// <summary>The file a local page's address names now, when it is one of its own; null for any other address.</summary>
        public string? LocalPath(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && pages.TryGetValue(uri.Host, out PageEntry? page)
                ? Resolve(page, Uri.UnescapeDataString(uri.AbsolutePath))
                : null;
        }

        /// <summary>The address a background image loads from; only files given as backgrounds are served there.</summary>
        public string ImageUrl(string path)
        {
            string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16] + Path.GetExtension(path).ToLowerInvariant();
            images[name] = path;
            return "https://" + ImageHost + "/" + name;
        }

        /// <summary>Forgets the hosts of pages that are no longer open.</summary>
        public void Retain(IEnumerable<string> overlayIds)
        {
            HashSet<string> open = overlayIds.ToHashSet();
            foreach (string host in pages.Where(pair => !open.Contains(pair.Value.OverlayId)).Select(pair => pair.Key).ToList())
            {
                pages.Remove(host);
            }

            foreach (string id in sources.Keys.Where(id => !open.Contains(id)).ToList())
            {
                sources.Remove(id, out (RemotePageChannel Channel, string? Host) replica);
                if (replica.Host is not null)
                {
                    replicaServer?.Remove(replica.Host);
                    pages.Remove(replica.Host);
                }

                replica.Channel.Dispose();
            }
        }

        private static string HostOf(string overlayId)
        {
            return "p" + overlayId.Replace("-", string.Empty, StringComparison.Ordinal) + "." + Domain;
        }

        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (environment is null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? uri))
            {
                return;
            }

            if (string.Equals(uri.Host, ImageHost, StringComparison.OrdinalIgnoreCase))
            {
                e.Response = images.TryGetValue(uri.AbsolutePath.TrimStart('/'), out string? image) && File.Exists(image)
                    ? environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(image)), 200, "OK", "Content-Type: " + PageFiles.ContentType(image))
                    : environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }

            if (!pages.TryGetValue(uri.Host, out PageEntry? page))
            {
                // A name under the domain no page holds: answered here, never sent on to this machine's own loopback.
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }

            string urlPath = Uri.UnescapeDataString(uri.AbsolutePath);
            string? path = PageFiles.ResolveReal(page.File, page.Root, urlPath);
            if (path is null || !File.Exists(path))
            {
                e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }

            string type = PageFiles.ContentType(path);
            string? policy = PolicyFor(type, page.Js);
            e.Response = environment.CreateWebResourceResponse(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), 200, "OK",
                "Content-Type: " + type + (policy is null ? string.Empty : "\nContent-Security-Policy: " + policy));
        }

        /// <summary>
        /// A page opened without --js runs none of its own scripts, as agterm's allowsContentJavaScript holds for every
        /// document: any response gets the policy, so an SVG or XHTML document opened from the folder runs none either. The
        /// bridge and the theme are no part of it: they go into every document as it is created, as agterm's user scripts,
        /// and no policy of the page reaches them.
        /// </summary>
        public static string? PolicyFor(string type, bool js)
        {
            return js ? null : "script-src 'none'; object-src 'none'; base-uri 'none'";
        }

        private static string? Resolve(PageEntry page, string urlPath)
        {
            return PageFiles.Resolve(page.File, page.Root, urlPath);
        }

        private sealed record PageEntry(string File, string? Root, bool Js, string OverlayId);
    }
}
