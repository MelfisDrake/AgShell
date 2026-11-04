using AgShell.Core;
using Xunit;

namespace AgShell.Core.Tests
{
    public class OpenPathResolverTests
    {
        [Fact]
        public void AFolderIsItselfAFileItsFolderAndAMissingPathNothing()
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            string file = Path.Combine(folder, "a.txt");
            File.WriteAllText(file, "x");
            try
            {
                Assert.Equal(folder, OpenPathResolver.Directory(folder, "C:\\"));
                Assert.Equal(folder, OpenPathResolver.Directory("a.txt", folder));
                Assert.Equal(folder, OpenPathResolver.Directory("\"" + file + "\"", "C:\\"));
                Assert.Null(OpenPathResolver.Directory(Path.Combine(folder, "missing"), "C:\\"));
                Assert.Equal("C:\\", OpenPathResolver.Directory("C:\"", folder));
                Assert.Equal("C:\\", OpenPathResolver.Directory("C:\" ", folder));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
