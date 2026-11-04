namespace AgShell.Core
{
    /// <summary>
    /// agterm's SpawnPacer: a launch that replays captured commands does not start every pane's program at the same
    /// instant. A leaky bucket of at most one paced grant per interval, the next one measured from the actual previous
    /// grant, so a stalled thread releases one grant on waking rather than a burst. Arming grants nothing: a pane becomes
    /// grantable once it asks, and the queue drains in the armed order, a later ready pane never jumping the head. The
    /// burst set (the panes on screen) and an expedited pane go at once.
    /// </summary>
    public sealed class SpawnPacer<TKey> where TKey : notnull
    {
        private enum State
        {
            Expected,
            Ready,
            Granted,
        }

        private readonly TimeSpan interval;
        private readonly Func<DateTime> now;
        private readonly Action<TimeSpan, Action> schedule;
        private readonly Dictionary<TKey, State> states = new Dictionary<TKey, State>();
        private readonly HashSet<TKey> immediate = new HashSet<TKey>();
        private List<TKey> pending = new List<TKey>();
        private bool armed;
        private DateTime? armedAt;
        private DateTime? lastGrant;
        private bool wakeScheduled;

        /// <param name="schedule">Runs the action after the delay, on the caller's thread.</param>
        public SpawnPacer(TimeSpan interval, Func<DateTime> now, Action<TimeSpan, Action> schedule)
        {
            this.interval = interval;
            this.now = now;
            this.schedule = schedule;
        }

        /// <summary>A granted key: the caller starts that pane's program.</summary>
        public event Action<TKey>? Granted;

        /// <summary>
        /// agterm's onDrain: once per launch, when the last armed key is granted or dropped, with the time since it was armed.
        /// Diagnostics only: the pacer is already passthrough then.
        /// </summary>
        public event Action<TimeSpan>? Drained;

        /// <summary>Nothing queued: unarmed, or every armed key granted or dropped; a request then answers at once.</summary>
        public bool IsPassthrough => !armed || pending.Count == 0;

        public void Arm(IEnumerable<TKey> order, IEnumerable<TKey> burst)
        {
            armed = true;
            armedAt = now();
            pending = order.Distinct().ToList();
            states.Clear();
            foreach (TKey key in pending)
            {
                states[key] = State.Expected;
            }

            immediate.Clear();
            immediate.UnionWith(burst.Where(states.ContainsKey));
            lastGrant = null;
            wakeScheduled = false;
        }

        /// <summary>
        /// Adds a window's panes to the launch's queue, after those already in it: windows come up one by one, and agterm
        /// paces the whole app's launch as one. True when these panes armed the queue, agterm's arm at launch.
        /// </summary>
        public bool Enqueue(IEnumerable<TKey> order, IEnumerable<TKey> burst)
        {
            if (!armed)
            {
                Arm(order, burst);
                return true;
            }

            foreach (TKey key in order.Where(key => !states.ContainsKey(key)))
            {
                pending.Add(key);
                states[key] = State.Expected;
            }

            immediate.UnionWith(burst.Where(states.ContainsKey));
            ScheduleWake();
            return false;
        }

        /// <summary>Marks the key ready and says whether it may start now; false leaves it for a later Granted.</summary>
        public bool Request(TKey key)
        {
            if (IsPassthrough || !states.TryGetValue(key, out State state) || state == State.Granted)
            {
                return true;
            }

            if (immediate.Contains(key))
            {
                Grant(key, notify: false);
                return true;
            }

            states[key] = State.Ready;
            ScheduleWake();
            return false;
        }

        /// <summary>Grants the key now (it came on screen), taking the next token; one that has not asked yet goes when it asks.</summary>
        public void Expedite(TKey key)
        {
            if (!states.TryGetValue(key, out State state) || state == State.Granted || immediate.Contains(key))
            {
                return;
            }

            if (state != State.Ready)
            {
                immediate.Add(key);
                return;
            }

            Grant(key, notify: true);
        }

        /// <summary>Moves the keys to the front of the queue in the given order, releasing nothing.</summary>
        public void Prioritize(IEnumerable<TKey> keys)
        {
            HashSet<TKey> seen = new HashSet<TKey>();
            List<TKey> promoted = keys.Where(key => pending.Contains(key) && seen.Add(key)).ToList();
            if (promoted.Count == 0)
            {
                return;
            }

            pending = promoted.Concat(pending.Where(key => !seen.Contains(key))).ToList();
            ScheduleWake();
        }

        /// <summary>Drops the key: its pane is gone, or it starts with nothing to replay.</summary>
        public void Discard(TKey key)
        {
            if (!states.Remove(key))
            {
                return;
            }

            pending.Remove(key);
            immediate.Remove(key);
            ScheduleWake();
            NoteDrain();
        }

        private void Grant(TKey key, bool notify)
        {
            states[key] = State.Granted;
            pending.Remove(key);
            immediate.Remove(key);
            lastGrant = now();
            if (notify)
            {
                Granted?.Invoke(key);
            }

            ScheduleWake();
            NoteDrain();
        }

        private void NoteDrain()
        {
            if (armed && pending.Count == 0 && armedAt is DateTime start)
            {
                armed = false;
                armedAt = null;
                Drained?.Invoke(now() - start);
            }
        }

        private void ScheduleWake()
        {
            if (wakeScheduled || GrantableHead() is null)
            {
                return;
            }

            wakeScheduled = true;
            schedule(RemainingDelay(), Wake);
        }

        private void Wake()
        {
            wakeScheduled = false;
            if (GrantableHead() is not (TKey head, true))
            {
                return;
            }

            if (RemainingDelay() > TimeSpan.Zero)
            {
                ScheduleWake();
                return;
            }

            Grant(head, notify: true);
        }

        /// <summary>The head of the armed order, and only once it has asked.</summary>
        private (TKey, bool)? GrantableHead()
        {
            if (pending.Count == 0 || states[pending[0]] != State.Ready)
            {
                return null;
            }

            return (pending[0], true);
        }

        private TimeSpan RemainingDelay()
        {
            if (lastGrant is not DateTime last)
            {
                return TimeSpan.Zero;
            }

            TimeSpan elapsed = now() - last;
            return elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
        }
    }
}
