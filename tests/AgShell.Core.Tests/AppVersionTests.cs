namespace AgShell.Core.Tests
{
    public class AppVersionTests
    {
        [Fact]
        public void TheVersionAndTheShortCommitComeFromTheInformationalVersion()
        {
            Assert.Equal(("0.2.0", "ad312f4"), AppVersion.Split("0.2.0+ad312f49111a6d678481dd6ad33180e7ff1f2366"));
            Assert.Equal(("0.2.0", (string?)null), AppVersion.Split("0.2.0"));
            Assert.Equal(("0.2.0", (string?)null), AppVersion.Split("0.2.0+"));
        }

        [Fact]
        public void TheDocumentationFollowsTheInterfaceLanguage()
        {
            Assert.EndsWith("/docs/agshellctl.ru.md", AppVersion.DocumentationUrl("ru"));
            Assert.EndsWith("/docs/agshellctl.md", AppVersion.DocumentationUrl("en"));
        }
    }
}
