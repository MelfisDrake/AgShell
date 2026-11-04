namespace AgShell.Protocol
{
    public enum TargetMatch
    {
        Resolved,
        NotFound,
        Ambiguous,
    }

    public readonly record struct TargetResolution(TargetMatch Match, Guid Id, IReadOnlyList<Guid> Hits);

    /// <summary>
    /// Turns a target string into an id: "active", a full id, or a unique prefix of one (git-style).
    /// Names never match, so a session called like an id prefix cannot be confused with one.
    /// </summary>
    public static class ControlTargets
    {
        public const string Active = "active";

        public static TargetResolution Resolve(string target, IEnumerable<Guid> candidates, Guid? active)
        {
            if (string.IsNullOrEmpty(target))
            {
                return new TargetResolution(TargetMatch.NotFound, Guid.Empty, Array.Empty<Guid>());
            }

            if (target == Active)
            {
                return active is Guid id
                    ? new TargetResolution(TargetMatch.Resolved, id, Array.Empty<Guid>())
                    : new TargetResolution(TargetMatch.NotFound, Guid.Empty, Array.Empty<Guid>());
            }

            string needle = target.ToLowerInvariant();
            List<Guid> hits = candidates.Where(candidate => candidate.ToString().StartsWith(needle, StringComparison.Ordinal)).ToList();
            Guid exact = hits.FirstOrDefault(candidate => candidate.ToString() == needle);
            if (exact != Guid.Empty)
            {
                return new TargetResolution(TargetMatch.Resolved, exact, Array.Empty<Guid>());
            }

            return hits.Count switch
            {
                0 => new TargetResolution(TargetMatch.NotFound, Guid.Empty, Array.Empty<Guid>()),
                1 => new TargetResolution(TargetMatch.Resolved, hits[0], Array.Empty<Guid>()),
                _ => new TargetResolution(TargetMatch.Ambiguous, Guid.Empty, hits),
            };
        }

        public static string ErrorMessage(string noun, string target, TargetResolution resolution)
        {
            if (resolution.Match != TargetMatch.Ambiguous)
            {
                return "no such " + noun + ": " + target;
            }

            string listed = string.Join(", ", resolution.Hits.Select(hit => hit.Wire()[..8]));
            return "ambiguous " + noun + " prefix '" + target + "' → " + listed;
        }
    }
}
