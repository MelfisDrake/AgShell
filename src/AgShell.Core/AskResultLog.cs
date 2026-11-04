using AgShell.Protocol;

namespace AgShell.Core
{
    /// <summary>
    /// agterm's AskRegistry: the finished questions' results, one list for the whole app, so an answer stays readable after
    /// its window is gone. Each result keeps the window that owned it; a live window answers only its own.
    /// </summary>
    public sealed class AskResultLog
    {
        public const int RetainedResults = 32;

        private readonly List<(string Id, ControlAskResult Result, object Owner)> finished = new List<(string, ControlAskResult, object)>();

        public void Retain(string id, ControlAskResult result, object owner)
        {
            finished.Add((id, result, owner));
            if (finished.Count > RetainedResults)
            {
                finished.RemoveAt(0);
            }
        }

        /// <summary>The result of a question, with the window that owned it; null for an id never seen or long forgotten.</summary>
        public (ControlAskResult Result, object Owner)? Find(string id)
        {
            foreach ((string Id, ControlAskResult Result, object Owner) entry in Enumerable.Reverse(finished))
            {
                if (entry.Id == id)
                {
                    return (entry.Result, entry.Owner);
                }
            }

            return null;
        }
    }
}
