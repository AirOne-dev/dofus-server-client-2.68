// Patch <connection.host> dans config.xml — même logique que ConfigPatcher Swift.

using System.IO;
using System.Text.RegularExpressions;

namespace OneAirLauncher;

public static class ConfigPatcher
{
    public static void Patch(string configPath, string name, string host, int port)
    {
        if (!File.Exists(configPath)) return;
        var text = File.ReadAllText(configPath);

        // Backup .orig si pas déjà fait (préserve la version Cytrus pristine).
        var orig = configPath + ".orig";
        if (!File.Exists(orig)) File.WriteAllText(orig, text);

        text = Regex.Replace(text,
            "<entry key=\"connection\\.host\">[^<]*</entry>",
            $"<entry key=\"connection.host\">{name}:{host}:{port}</entry>");
        text = Regex.Replace(text,
            "<entry key=\"connection\\.host\\.signature\">[^<]*</entry>",
            "<entry key=\"connection.host.signature\"></entry>");

        File.WriteAllText(configPath, text);
    }
}
