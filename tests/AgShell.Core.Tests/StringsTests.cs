using System.Text.RegularExpressions;

namespace AgShell.Core.Tests
{
    public class StringsTests
    {
        [Fact]
        public void TheSystemChoiceSpeaksRussianOnlyToARussianWindows()
        {
            AppSettings settings = new AppSettings();
            Assert.Equal("ru", settings.InterfaceLanguage("ru"));
            Assert.Equal("en", settings.InterfaceLanguage("de"));

            settings.Language = "en";
            Assert.Equal("en", settings.InterfaceLanguage("ru"));
            settings.Language = "ru";
            Assert.Equal("ru", settings.InterfaceLanguage("en"));
            settings.Language = "fr";
            Assert.Equal("system", settings.EffectiveLanguage);
        }

        [Fact]
        public void EveryRussianTextInTheAppGoesThroughTheTable()
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "agshell.slnx")))
            {
                root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("agshell.slnx not found above the tests");
            }

            Regex literal = new Regex("(Strings\\.T\\(\\s*)?\"((?:[^\"\\\\]|\\\\.)*)\"");
            Regex cyrillic = new Regex("[А-Яа-яЁё]");
            List<string> problems = new List<string>();
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && Path.GetFileName(path) != "Strings.cs"))
            {
                string[] lines = File.ReadAllLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (Match match in literal.Matches(line))
                    {
                        if (!cyrillic.IsMatch(match.Groups[2].Value))
                        {
                            continue;
                        }

                        string text = Regex.Unescape(match.Groups[2].Value);

                        string where = Path.GetFileName(file) + ":" + (index + 1);
                        if (!match.Groups[1].Success)
                        {
                            problems.Add(where + " not translated: " + text);
                        }
                        else if (!Strings.Translated.Contains(text))
                        {
                            problems.Add(where + " no English: " + text);
                        }
                    }
                }
            }

            Assert.Empty(problems);
        }
    }
}
