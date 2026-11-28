using System.Globalization;
using System.IO;

internal static class GameplayDefaults
{
    internal const uint Enabled = 0x000BFFF7u;

    internal static bool TryRead(string path, out uint flags)
    {
        flags = Enabled;
        uint stored;
        if (string.IsNullOrEmpty(path) || !File.Exists(path) ||
            !uint.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out stored))
            return false;
        flags = stored;
        return true;
    }

    internal static uint Read(string path)
    {
        uint flags;
        TryRead(path, out flags);
        return flags;
    }
}
