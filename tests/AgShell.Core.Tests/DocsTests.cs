using System.Text.RegularExpressions;

namespace AgShell.Core.Tests
{
    public class DocsTests
    {
        /// <summary>The English and Russian copies of a document keep the same headings, code blocks and tables.</summary>
        [Theory]
        [InlineData("README.md", "README.ru.md")]
        [InlineData("docs/agshellctl.md", "docs/agshellctl.ru.md")]
        public void TranslatedDocumentsKeepTheSameShape(string english, string russian)
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "agshell.slnx")))
            {
                root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("agshell.slnx not found above the tests");
            }

            Assert.Equal(Shape(File.ReadAllText(Path.Combine(root, english))), Shape(File.ReadAllText(Path.Combine(root, russian))));
        }

        private static (int Headings, int CodeBlocks, int Tables, int TableRows) Shape(string text)
        {
            return (
                Regex.Matches(text, "^#{1,6} ", RegexOptions.Multiline).Count,
                Regex.Matches(text, "^```", RegexOptions.Multiline).Count / 2,
                Regex.Matches(text, "^\\| --- ", RegexOptions.Multiline).Count,
                Regex.Matches(text, "^\\|", RegexOptions.Multiline).Count);
        }
    }
}
