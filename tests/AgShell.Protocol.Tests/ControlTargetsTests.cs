namespace AgShell.Protocol.Tests
{
    public class ControlTargetsTests
    {
        private static readonly Guid First = Guid.Parse("9f3c1111-0000-0000-0000-000000000001");
        private static readonly Guid Second = Guid.Parse("9f3c2222-0000-0000-0000-000000000002");
        private static readonly Guid[] Candidates = { First, Second };

        [Fact]
        public void ActiveResolvesToTheActiveId()
        {
            Assert.Equal(Second, ControlTargets.Resolve("active", Candidates, Second).Id);
            Assert.Equal(TargetMatch.NotFound, ControlTargets.Resolve("active", Candidates, null).Match);
        }

        [Fact]
        public void UniquePrefixAndFullIdResolveCaseInsensitively()
        {
            Assert.Equal(First, ControlTargets.Resolve("9F3C1", Candidates, null).Id);
            Assert.Equal(Second, ControlTargets.Resolve(Second.ToString().ToUpperInvariant(), Candidates, null).Id);
        }

        [Fact]
        public void AmbiguousAndMissingTargetsExplainThemselves()
        {
            TargetResolution ambiguous = ControlTargets.Resolve("9f3c", Candidates, null);
            Assert.Equal(TargetMatch.Ambiguous, ambiguous.Match);
            Assert.Equal("ambiguous session prefix '9f3c' → 9F3C1111, 9F3C2222", ControlTargets.ErrorMessage("session", "9f3c", ambiguous));

            TargetResolution missing = ControlTargets.Resolve("dead", Candidates, null);
            Assert.Equal("no such workspace: dead", ControlTargets.ErrorMessage("workspace", "dead", missing));
            Assert.Equal(TargetMatch.NotFound, ControlTargets.Resolve("", Candidates, First).Match);
        }
    }
}
