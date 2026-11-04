using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace AgShell.Ctl
{
    [TomlSourceGenerationOptions(DuplicateKeyHandling = TomlDuplicateKeyHandling.Error)]
    [TomlSerializable(typeof(TomlTable))]
    internal partial class CodexTomlContext : TomlSerializerContext
    {
    }
}
