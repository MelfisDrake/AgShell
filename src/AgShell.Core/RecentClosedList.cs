using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's recently closed list: one for the whole app, every window adding to it, newest first and at most
    /// <see cref="Limit"/> entries, one per session or workspace.
    /// </summary>
    public sealed class RecentClosedList
    {
        public const int Limit = 20;

        private readonly List<RecentClosedItem> items = new List<RecentClosedItem>();

        /// <summary>The list changed, so it can be saved and shown again.</summary>
        public event Action? Changed;

        public IReadOnlyList<RecentClosedItem> Items => items;

        /// <summary>Puts an entry first, replacing an older one for the same session or workspace, and keeps the list short.</summary>
        public void Add(RecentClosedItem item, Func<RecentClosedItem, bool> sameThing)
        {
            items.RemoveAll(existing => sameThing(existing));
            items.Insert(0, item);
            if (items.Count > Limit)
            {
                items.RemoveRange(Limit, items.Count - Limit);
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// agterm's RecentClosedTarget: the newest entry when no target is given, else the one whose id matches exactly or by a
        /// unique prefix, as every other target resolves; else why not.
        /// </summary>
        public (RecentClosedItem? Item, string? Error) Resolve(string? target)
        {
            if (items.Count == 0)
            {
                return (null, "no recently closed items");
            }

            if (target is null)
            {
                return (items[0], null);
            }

            TargetResolution resolution = ControlTargets.Resolve(target, items.Select(item => item.Id), null);
            return resolution.Match == TargetMatch.Resolved
                ? (items.First(item => item.Id == resolution.Id), null)
                : (null, ControlTargets.ErrorMessage("recent item", target, resolution));
        }

        public RecentClosedItem? Find(Guid? itemId)
        {
            return itemId is Guid id ? items.FirstOrDefault(item => item.Id == id) : items.FirstOrDefault();
        }

        public void Remove(RecentClosedItem item)
        {
            if (items.RemoveAll(existing => existing.Id == item.Id) > 0)
            {
                Changed?.Invoke();
            }
        }

        public void Clear()
        {
            items.Clear();
            Changed?.Invoke();
        }

        /// <summary>Takes what the file holds, as agterm's list is always the one on disk.</summary>
        public void Load(IEnumerable<RecentClosedItem> loaded)
        {
            List<RecentClosedItem> kept = loaded.Take(Limit).ToList();
            items.Clear();
            items.AddRange(kept);
        }
    }
}
