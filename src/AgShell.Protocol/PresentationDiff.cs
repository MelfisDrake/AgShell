namespace AgShell.Protocol
{
    /// <summary>
    /// agterm's presentation frames between two states of a session: what a stream sends when the session changes. The
    /// layout goes first, so a swap in the same change renames the panes the status and overlays name.
    /// </summary>
    public static class PresentationDiff
    {
        /// <summary>The first frames of a stream: the snapshot, then what is handed to its role.</summary>
        public static List<ControlPresentationFrame> Opening(ControlPresentation shown)
        {
            List<ControlPresentationFrame> frames = new List<ControlPresentationFrame>
            {
                new ControlPresentationFrame { Kind = ControlPresentationKinds.Snapshot, Snapshot = shown with { Ask = null, Overlays = null, Sounds = null, Pages = null } },
            };
            Role(new ControlPresentation(), shown, frames);
            return frames;
        }

        public static List<ControlPresentationFrame> Changes(ControlPresentation shown, ControlPresentation next)
        {
            List<ControlPresentationFrame> frames = new List<ControlPresentationFrame>();
            if (next.Layout != shown.Layout)
            {
                frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.Layout, Layout = next.Layout });
            }

            Role(shown, next, frames);
            if (next.Status != shown.Status)
            {
                frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.Status, Status = next.Status });
            }

            if (next.Context != shown.Context)
            {
                frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.Context, Context = next.Context });
            }

            // The countdown alone is no change: the other side keeps its own.
            if (WithoutCountdown(next.Hud) != WithoutCountdown(shown.Hud))
            {
                frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.Hud, Hud = next.Hud });
            }

            return frames;
        }

        /// <summary>The presenter role and what is handed to it, when they changed.</summary>
        private static void Role(ControlPresentation shown, ControlPresentation next, List<ControlPresentationFrame> frames)
        {
            foreach (ControlPresentationSound sound in next.Sounds ?? new List<ControlPresentationSound>())
            {
                frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.Sound, Title = sound.Name });
            }

            // A new job is shown there; one that failed or was canceled is taken down. One that exited needs nothing: its ssh
            // ends with the program, and --wait holds what it printed.
            Dictionary<string, ControlPresentationOverlay> before = (shown.Overlays ?? new List<ControlPresentationOverlay>()).ToDictionary(overlay => overlay.Job);
            foreach (ControlPresentationOverlay overlay in next.Overlays ?? new List<ControlPresentationOverlay>())
            {
                ControlPresentationOverlay? was = before.GetValueOrDefault(overlay.Job);
                if (was is null && overlay.Failure is null && overlay.ExitCode is null)
                {
                    frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayRequest, Overlay = overlay });
                }
                else if (was is not null && ((was.Failure is null && overlay.Failure is not null) || (!was.Dismissed && overlay.Dismissed)))
                {
                    frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayClose, Id = overlay.Job });
                }
                else if (was is not null && was.SizePercent != overlay.SizePercent)
                {
                    frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.OverlayResize, Overlay = overlay });
                }
            }

            Pages(shown, next, frames);
            if (shown.Ask?.Id == next.Ask?.Id && shown.Ask?.Owner == next.Ask?.Owner)
            {
                return;
            }

            if (shown.Ask is ControlPresentationAsk ended)
            {
                frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.AskDismiss, Id = ended.Id, Owner = ended.Owner });
            }

            if (next.Ask is ControlPresentationAsk asked)
            {
                frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.AskRequest, Ask = asked });
            }
        }

        /// <summary>
        /// agterm's page frames: a new page is shown there; one closed here is taken down; a change of size or of the visibility
        /// asked for is passed on; and each command past the last one sent goes out once.
        /// </summary>
        private static void Pages(ControlPresentation shown, ControlPresentation next, List<ControlPresentationFrame> frames)
        {
            Dictionary<string, ControlPresentationPage> before = (shown.Pages ?? new List<ControlPresentationPage>()).ToDictionary(page => page.Id);
            foreach (ControlPresentationPage page in next.Pages ?? new List<ControlPresentationPage>())
            {
                ControlPresentationPage? was = before.GetValueOrDefault(page.Id);
                if (was is null && !page.Closed)
                {
                    frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageRequest, Page = page with { Commands = null } });
                }
                else if (was is not null && !was.Closed && page.Closed)
                {
                    frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageClose, Id = page.Id, Owner = page.Owner });
                    continue;
                }
                else if (was is not null)
                {
                    // agterm sends a size or visibility change as a page command, as it does a reload.
                    if (was.SizePercent != page.SizePercent)
                    {
                        frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageCommandKind, Id = page.Id, Owner = page.Owner, PageCommand = new ControlPresentationPageCommand { Action = "resize", SizePercent = page.SizePercent } });
                    }

                    if (was.Hidden != page.Hidden)
                    {
                        frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageCommandKind, Id = page.Id, Owner = page.Owner, PageCommand = new ControlPresentationPageCommand { Action = page.Hidden ? "hide" : "show" } });
                    }
                }

                int sent = was?.Commands?.Count ?? 0;
                foreach (ControlPresentationPageCommand command in (page.Commands ?? new List<ControlPresentationPageCommand>()).Skip(sent))
                {
                    frames.Add(new ControlPresentationFrame { Kind = ControlPresentationKinds.PageCommandKind, Id = page.Id, Owner = page.Owner, PageCommand = command });
                }
            }
        }

        private static ControlPresentationHud? WithoutCountdown(ControlPresentationHud? hud)
        {
            return hud is null ? null : hud with { Remaining = null };
        }
    }
}
