namespace AgShell.Core
{
    /// <summary>agterm's QuitPrompt.message: what quitting closes, and in live without ending the shells.</summary>
    public static class QuitPrompt
    {
        public static string Message(int windows, int sessions, bool live)
        {
            if (live)
            {
                return (windows == 1, sessions == 1) switch
                {
                    (true, true) => Strings.T("Закроется 1 окно и 1 сессия."),
                    (true, false) => Strings.T("Закроется 1 окно и сессий: {1}.", windows, sessions),
                    (false, true) => Strings.T("Закроется окон: {0} и 1 сессия.", windows, sessions),
                    (false, false) => Strings.T("Закроется окон: {0} и сессий: {1}.", windows, sessions),
                };
            }

            return (windows == 1, sessions == 1) switch
            {
                (true, true) => Strings.T("Закроется 1 окно и 1 сессия, все работающие шеллы завершатся."),
                (true, false) => Strings.T("Закроется 1 окно и сессий: {1}, все работающие шеллы завершатся.", windows, sessions),
                (false, true) => Strings.T("Закроется окон: {0} и 1 сессия, все работающие шеллы завершатся.", windows, sessions),
                (false, false) => Strings.T("Закроется окон: {0} и сессий: {1}, все работающие шеллы завершатся.", windows, sessions),
            };
        }
    }
}
