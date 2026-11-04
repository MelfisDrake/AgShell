namespace AgShell.Core.Tests
{
    public class QuitPromptTests
    {
        [Fact]
        public void TheMessageCountsWindowsAndSessionsAndEndsShellsOutsideLive()
        {
            Assert.Equal("Закроется 1 окно и 1 сессия.", QuitPrompt.Message(1, 1, live: true));
            Assert.Equal("Закроется 1 окно и сессий: 3.", QuitPrompt.Message(1, 3, live: true));
            Assert.Equal("Закроется окон: 2 и 1 сессия.", QuitPrompt.Message(2, 1, live: true));
            Assert.Equal("Закроется окон: 2 и сессий: 0, все работающие шеллы завершатся.", QuitPrompt.Message(2, 0, live: false));
            Assert.Equal("Закроется 1 окно и 1 сессия, все работающие шеллы завершатся.", QuitPrompt.Message(1, 1, live: false));
        }
    }
}
