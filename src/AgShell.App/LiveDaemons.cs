using AgShell.Core;
using AgShell.Protocol;
using AgShell.Pty;

namespace AgShell.App
{
    /// <summary>
    /// This machine's live panes, one daemon each, as agterm's zmx: a pane is opened on the daemon named by its pane id,
        /// started first when it does not run, and the daemons are listed by their pipes, as agterm lists its socket directory.
    /// </summary>
    internal sealed class LiveDaemons
    {
        /// <summary>How long a pane waits for its daemon to come up.</summary>
        private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(5);

        /// <summary>How long a request to a running daemon waits for its pipe.</summary>
        private static readonly TimeSpan ReachWait = TimeSpan.FromMilliseconds(500);

        private readonly string stateDirectory = ControlEndpoint.StateDirectory();

        /// <summary>
        /// Panes whose daemon was told to end and may still be going: a pane opened again under the same id (a restart) waits
        /// for it, as agterm's restart waits for the old daemon, or its open could reach the daemon that is ending.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> ending = new System.Collections.Concurrent.ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pipe prefix of this state directory's daemons, agterm's zmx socket directory.</summary>
        public string PipePrefix => ControlEndpoint.LivePipePrefix(stateDirectory);

        public string PipeOf(string paneId) => ControlEndpoint.LivePanePipe(stateDirectory, paneId);

        /// <summary>
        /// Opens a pane on its daemon: attaches to the shell it kept, or has it start the shell. The host adopts the pane
        /// before the background connection starts, so even an immediate failure reaches its owner.
        /// </summary>
        public void Open(string paneId, PtyStartOptions options, Action<RemotePane> adopt)
        {
            LiveClient client = new LiveClient { ClosesWhenEmpty = true };
            client.Stopping += MarkEnding;
            RemotePane pane = client.Open(paneId, options);
            string pipe = PipeOf(paneId);
            adopt(pane);
            Task before = ending.TryGetValue(paneId, out Task? going) ? going : Task.CompletedTask;
            _ = Task.Run(async () =>
            {
                await before;
                if (client.Connect(pipe, TimeSpan.Zero))
                {
                    return;
                }

                // A daemon that has not gone yet still holds the name, and the one started meanwhile leaves at once: start
                // again until one answers.
                DateTime deadline = DateTime.UtcNow + StartWait;
                while (DateTime.UtcNow < deadline)
                {
                    if (!LiveDaemonCopy.Start(pipe))
                    {
                        break;
                    }

                    if (client.Connect(pipe, TimeSpan.FromMilliseconds(500)))
                    {
                        return;
                    }
                }

                client.Abandon();
            });
        }

        /// <summary>
        /// agterm's PaneReattach: a pane joins its daemon again, taking the lead unless it follows, and never creates one. A
        /// daemon that is gone ends the pane with agterm's line instead of handing back a new shell under the old id. The
        /// pane is adopted before the daemon is reached, so its end is not missed.
        /// </summary>
        public void Rejoin(string paneId, int columns, int rows, bool follow, Action<RemotePane> adopt)
        {
            LiveClient client = new LiveClient { ClosesWhenEmpty = true };
            client.Stopping += MarkEnding;
            RemotePane pane = client.Attach(paneId, columns, rows, follow, borrowed: false);
            adopt(pane);
            string pipe = PipeOf(paneId);
            _ = Task.Run(() =>
            {
                if (client.Connect(pipe, TimeSpan.FromMilliseconds(500)))
                {
                    return;
                }

                pane.RaiseOutput("agshell: session is gone\r\n");
                client.Abandon(1);
            });
        }

        private void MarkEnding(string paneId)
        {
            Task gone = Task.Run(async () =>
            {
                DateTime deadline = DateTime.UtcNow + StartWait;
                while (DateTime.UtcNow < deadline && ControlEndpoint.LivePanes(stateDirectory)?.Contains(paneId, StringComparer.OrdinalIgnoreCase) != false)
                {
                    await Task.Delay(50);
                }
            });
            ending[paneId] = gone;
            _ = gone.ContinueWith(_ => ending.TryRemove(new KeyValuePair<string, Task>(paneId, gone)));
        }

        private const string NoAnswer = "the daemon did not answer";

        /// <summary>agterm's zmx list: every daemon that answers, with its shell; one that does not is left out.</summary>
        public async Task<List<LivePaneInfo>> ListAsync()
        {
            return (await RecordsAsync())?.SelectMany(record => record.Shells ?? new List<LivePaneInfo>()).ToList() ?? new List<LivePaneInfo>();
        }

        /// <summary>
        /// agterm's sessionRecords: every daemon by its pipe, with its shells, or none (null) for one that did not answer, an
        /// unreadable row; null when the pipes could not be listed at all, which is not an empty answer.
        /// </summary>
        public async Task<List<(string PaneId, List<LivePaneInfo>? Shells)>?> RecordsAsync()
        {
            if (ControlEndpoint.LivePanes(stateDirectory) is not List<string> names)
            {
                return null;
            }

            return (await Task.WhenAll(names.Select(paneId => Task.Run(async () =>
            {
                using LiveClient client = new LiveClient();
                if (!client.Connect(PipeOf(paneId), ReachWait))
                {
                    Log.Error("ZmxClient", "live list failed for " + paneId + ": " + NoAnswer);
                    return (paneId, (List<LivePaneInfo>?)null);
                }

                List<LivePaneInfo>? shells = await client.ListAsync();
                if (shells is null)
                {
                    Log.Error("ZmxClient", "live list failed for " + paneId + ": " + NoAnswer);
                }

                return (paneId, shells);
            })))).ToList();
        }

        /// <summary>agterm's zmx screen of a kept shell; null when no daemon has it, it cannot read it or it does not answer.</summary>
        public async Task<DaemonScreen?> ScreenAsync(string paneId, bool all)
        {
            if (!Guid.TryParse(paneId, out _))
            {
                return null;
            }

            using LiveClient client = new LiveClient();
            if (!client.Connect(PipeOf(paneId), ReachWait))
            {
                Log.Error("ZmxClient", "live screen failed for " + paneId + ": " + NoAnswer);
                return null;
            }

            return await client.ScreenAsync(paneId, all);
        }

        /// <summary>agterm's zmx type into a kept shell; false when no daemon has it, it refused the input or did not answer.</summary>
        public async Task<bool> TypeAsync(string paneId, string text)
        {
            if (!Guid.TryParse(paneId, out _))
            {
                return false;
            }

            using LiveClient client = new LiveClient();
            if (!client.Connect(PipeOf(paneId), ReachWait))
            {
                Log.Error("ZmxClient", "live type failed for " + paneId + ": " + NoAnswer);
                return false;
            }

            return await client.TypeAsync(paneId, text);
        }

        /// <summary>
        /// agterm's killConfirmed and killObservedOrphan: the daemon is told to end its shell and must say it did; null then,
        /// else why not.
        /// </summary>
        public async Task<string?> KillConfirmedAsync(string paneId)
        {
            if (!Guid.TryParse(paneId, out _))
            {
                return "not a daemon name";
            }

            using LiveClient client = new LiveClient();
            client.Stopping += MarkEnding;
            if (!await Task.Run(() => client.Connect(PipeOf(paneId), ReachWait)))
            {
                Log.Error("ZmxClient", "live kill failed for " + paneId + ": " + NoAnswer);
                return NoAnswer;
            }

            string? failure = await client.StopConfirmedAsync(paneId);
            if (failure is not null)
            {
                Log.Error("ZmxClient", "live kill failed for " + paneId + ": " + failure);
            }

            return failure;
        }

        /// <summary>Ends a kept shell and its daemon.</summary>
        public void Stop(string paneId)
        {
            if (!Guid.TryParse(paneId, out _))
            {
                return;
            }

            _ = Task.Run(() =>
            {
                using LiveClient client = new LiveClient();
                client.Stopping += MarkEnding;
                if (client.Connect(PipeOf(paneId), ReachWait))
                {
                    client.Stop(paneId);
                    client.Flush();
                }
                else
                {
                    Log.Error("ZmxClient", "live kill failed for " + paneId + ": " + NoAnswer);
                }
            });
        }

        /// <summary>
        /// agterm's killBatch: tells each named daemon to end its shell, all at once, within the timeout; false when one could
        /// not be told. A pane opened again under one of the names waits for its daemon to go.
        /// </summary>
        public bool StopBatch(IReadOnlyCollection<string> paneIds, TimeSpan timeout)
        {
            string[] names = paneIds.ToArray();
            Task<bool>[] stops = names.Select(paneId => Task.Run(() =>
            {
                using LiveClient client = new LiveClient();
                client.Stopping += MarkEnding;
                if (!client.Connect(PipeOf(paneId), ReachWait))
                {
                    return false;
                }

                client.Stop(paneId);
                client.Flush();
                return true;
            })).ToArray();
            bool finished = Task.WaitAll(stops, timeout);
            string[] failed = names.Where((paneId, index) => !stops[index].IsCompletedSuccessfully || !stops[index].Result).ToArray();
            if (failed.Length > 0)
            {
                Log.Error("ZmxClient", "live kill failed for " + string.Join(",", failed) + ": " + (finished ? NoAnswer : "timed out"));
            }

            return finished && failed.Length == 0;
        }
    }
}
