namespace AgShell.Core
{
    public sealed class Workspace
    {
        internal readonly List<Session> SessionList = new List<Session>();

        public Workspace(Guid id, string name)
        {
            Id = id;
            Name = name;
        }

        public Guid Id { get; }

        public string Name { get; internal set; }

        public bool Collapsed { get; internal set; }

        public IReadOnlyList<Session> Sessions => SessionList;
    }
}
