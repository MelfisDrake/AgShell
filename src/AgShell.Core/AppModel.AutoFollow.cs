namespace AgShell.Core
{
    /// <summary>
    /// agterm's auto-follow: after a stretch of the user doing nothing, the window goes to the agent that has waited
    /// longest for an answer. The host owns the idle timer; the model decides where to go.
    /// </summary>
    public sealed partial class AppModel
    {
        private readonly HashSet<Guid> autoFollowConsumed = new HashSet<Guid>();

        /// <summary>
        /// Where auto-follow goes, or null to stay: nowhere while the current session is blocked itself, or, with
        /// <paramref name="stayOnActive"/>, running; otherwise the oldest blocked session not already followed to once.
        /// </summary>
        public Guid? AutoFollowTarget(bool stayOnActive)
        {
            Session? current = SelectedSessionId is Guid id ? FindSession(id) : null;
            if (current?.Status == AgentStatus.Blocked || (stayOnActive && current?.Status == AgentStatus.Active))
            {
                return null;
            }

            return AllSessions
                .Where(session => session.Status == AgentStatus.Blocked && !autoFollowConsumed.Contains(session.Id) && session.Id != current?.Id)
                .OrderBy(session => session.StatusChangedAt ?? DateTimeOffset.MaxValue)
                .Select(session => (Guid?)session.Id)
                .FirstOrDefault();
        }

        /// <summary>
        /// Goes to the auto-follow target, if any, and remembers it so a later idle stretch does not pull back to a
        /// session the user left, until it turns blocked again. As agterm's, the pane that set the status is brought up only
        /// with <paramref name="reveal"/> (the window active and not zoomed); otherwise the selection alone moves.
        /// Returns the session gone to.
        /// </summary>
        public Guid? AutoFollow(bool stayOnActive, bool reveal = false)
        {
            if (AutoFollowTarget(stayOnActive) is not Guid target)
            {
                return null;
            }

            autoFollowConsumed.Add(target);
            SelectSession(target, reveal);
            return target;
        }

        /// <summary>A session that turns blocked anew may be followed to again.</summary>
        private void ResetAutoFollow(Guid sessionId, AgentStatus previous, AgentStatus status)
        {
            if (status != AgentStatus.Blocked || previous != AgentStatus.Blocked)
            {
                autoFollowConsumed.Remove(sessionId);
            }
        }
    }
}
