namespace AgShell.Core.Tests
{
    public class CustomCommandUsageTests
    {
        [Fact]
        public void MostUsedRanksByCountWithTiesInKeymapOrder()
        {
            CustomCommandUsage usage = new CustomCommandUsage();
            usage.Record("b");
            usage.Record("c");
            usage.Record("c");
            usage.Record("a");
            usage.Record("gone");
            usage.Record("gone");
            usage.Record("gone");

            Assert.Equal(new[] { "c", "a", "b" }, usage.MostUsed(new[] { "a", "b", "c", "d" }, 5));
            Assert.Equal(new[] { "c" }, usage.MostUsed(new[] { "a", "b", "c", "d" }, 1));
        }

        [Fact]
        public void TheStoreKeepsCountsAndReadsABrokenFileAsEmpty()
        {
            string path = Path.Combine(Path.GetTempPath(), "agshell-usage-" + Guid.NewGuid() + ".json");
            try
            {
                CustomCommandUsageStore store = new CustomCommandUsageStore(path);
                CustomCommandUsage usage = store.Load();
                usage.Record("build");
                store.Save(usage);
                Assert.Equal(1, store.Load().Counts["build"]);

                File.WriteAllText(path, "{ not json");
                Assert.Empty(store.Load().Counts);

                File.WriteAllText(path, "{\"version\":1,\"counts\":null}");
                CustomCommandUsage recovered = store.Load();
                recovered.Record("build");
                Assert.Equal(new[] { "build" }, recovered.MostUsed(new[] { "build" }, 5));

                File.WriteAllText(path, "{\"version\": 2, \"counts\": {\"build\": 3}}");
                Assert.Empty(store.Load().Counts);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
