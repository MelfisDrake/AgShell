namespace AgShell.Core
{
    /// <summary>
    /// agterm's InputSourceLock: decides when the keyboard layout is switched to the configured one and when the earlier
    /// one comes back. It holds the layout that was selected when the lock engaged; the app feeds it the system's state and
    /// performs the selection it returns.
    /// </summary>
    public sealed class InputSourceLock
    {
        /// <summary>The layout to return to, non-null exactly while the lock holds an override.</summary>
        public string? Baseline { get; private set; }

        /// <summary>
        /// The next layout to select, or null if no change is needed. A target outside <paramref name="enabled"/> releases an
        /// existing override and cannot engage a new one.
        /// </summary>
        public string? Reconcile(bool terminalFocused, string? target, IReadOnlySet<string> enabled, string current)
        {
            if (!terminalFocused || target is null || !enabled.Contains(target))
            {
                return Release(current);
            }

            Baseline ??= current;
            return current == target ? null : target;
        }

        /// <summary>Ends the override and returns the layout to restore, null when none is held or it is already selected.</summary>
        public string? Release(string current)
        {
            if (Baseline is not string held)
            {
                return null;
            }

            Baseline = null;
            return held == current ? null : held;
        }

        /// <summary>Drops an override whose selection Windows refused, leaving nothing to restore.</summary>
        public void SelectionFailed()
        {
            Baseline = null;
        }
    }
}
