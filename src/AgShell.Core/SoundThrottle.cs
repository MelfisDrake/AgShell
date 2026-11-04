namespace AgShell.Core
{
    /// <summary>
    /// agterm's SoundThrottle: a replay of the same sound within the window is suppressed, so a burst of status sounds cannot
    /// machine-gun one clip; a different sound never is. The window runs from the last allowed play, not the last attempt.
    /// </summary>
    public sealed class SoundThrottle
    {
        private readonly TimeSpan window;
        private readonly Dictionary<string, TimeSpan> lastPlayed = new Dictionary<string, TimeSpan>();

        public SoundThrottle(TimeSpan window)
        {
            this.window = window;
        }

        /// <summary>Whether the sound may play at the monotonic time given, recording the play when it may.</summary>
        public bool Allow(string name, TimeSpan now)
        {
            if (lastPlayed.TryGetValue(name, out TimeSpan last) && now - last < window)
            {
                return false;
            }

            lastPlayed[name] = now;
            return true;
        }
    }
}
