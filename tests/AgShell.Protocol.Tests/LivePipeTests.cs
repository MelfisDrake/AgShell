namespace AgShell.Protocol.Tests
{
    public class LivePipeTests
    {
        private const string State = @"D:\test-state";

        [Fact]
        public void APanesPipeIsNamedByItsIdUnderTheStatesPrefix()
        {
            string pipe = ControlEndpoint.LivePanePipe(State, "6C55D514-E8EF-4A11-A15E-21B4ECC1E2DF");

            Assert.Equal(ControlEndpoint.PipeName(State) + "-live-6c55d514e8ef4a11a15e21b4ecc1e2df", pipe);
            Assert.StartsWith(ControlEndpoint.LivePipePrefix(State), pipe);
        }

        [Fact]
        public void TheBuildFollowsTheDaemonsFiles()
        {
            string folder = Path.Combine(Path.GetTempPath(), "agshell-live-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string program = Path.Combine(folder, "agshell-live.exe");
                File.WriteAllText(program, "one");
                string first = ControlEndpoint.LiveBuildOf(folder);
                File.WriteAllText(Path.Combine(folder, "unrelated.txt"), "ignored");
                Assert.Equal(first, ControlEndpoint.LiveBuildOf(folder));

                File.SetLastWriteTimeUtc(program, DateTime.UtcNow.AddMinutes(1));
                string touched = ControlEndpoint.LiveBuildOf(folder);

                Assert.Matches("^[0-9a-f]{12}$", first);
                Assert.NotEqual(first, touched);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
