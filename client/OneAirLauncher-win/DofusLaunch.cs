// Le launcher remplace `Dofus.exe` dans le bundle ; le binaire AIR captive
// original est renommé `dofus-real.exe`. AIR ne valide pas le nom de l'exe
// au runtime tant qu'on a retiré META-INF/AIR/hash et signatures.xml du
// bundle (cf. build-app-windows.sh).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace OneAirLauncher;

public static class DofusLaunch
{
    public const int ZaapPort = 4242;
    public const int ZaapHttpPort = 4243;

    public static string LauncherDir => AppContext.BaseDirectory;

    public static string RealDofusBinary => Path.Combine(LauncherDir, "dofus-real.exe");
    public static string ZaapServerBinary => Path.Combine(LauncherDir, "zaap-server.exe");
    public static string ConfigXml => Path.Combine(LauncherDir, "config.xml");
    public static string CredentialsJson => Path.Combine(LauncherDir, "credentials.json");

    public static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "OneAir", "Logs");

    public static void Launch(string host, int port, string serverName,
                              string login, string password)
    {
        Directory.CreateDirectory(LogDir);
        var launcherLog = Path.Combine(LogDir, "launcher.log");

        var lines = new List<string>
        {
            $"[{DateTime.Now:O}] launch host={host} port={port} login={login}",
            $"  launcher dir = {LauncherDir}",
            $"  dofus-real   = {RealDofusBinary} (exists={File.Exists(RealDofusBinary)})",
            $"  zaap-server  = {ZaapServerBinary} (exists={File.Exists(ZaapServerBinary)})",
        };

        if (File.Exists(ConfigXml))
        {
            try { ConfigPatcher.Patch(ConfigXml, serverName, host, port); }
            catch (Exception ex) { lines.Add($"  config.xml patch FAIL: {ex.Message}"); }
        }
        else
        {
            lines.Add("  config.xml introuvable — skip patch");
        }

        // credentials.json est lu par ZaapConnectionHelper.connect (BUILD_TYPE=DEBUG).
        var instanceId = Random.Shared.Next(1, 1_000_000);
        var hash = Guid.NewGuid().ToString().ToLowerInvariant();
        var creds = new Dictionary<string, object>
        {
            ["port"] = ZaapPort,
            ["name"] = "dofus",
            ["release"] = "main",
            ["instanceId"] = instanceId,
            ["hash"] = hash,
        };
        try
        {
            File.WriteAllText(CredentialsJson, JsonSerializer.Serialize(creds));
            lines.Add($"  credentials.json written hash={hash}");
        }
        catch (Exception ex)
        {
            lines.Add($"  credentials.json FAIL: {ex.Message}");
        }

        try
        {
            KillExistingZaap();
            StartZaap(login, password, hash, instanceId, $"{host}:{port}");
            lines.Add("  zaap-server spawned");
        }
        catch (Exception ex)
        {
            lines.Add($"  zaap-server FAIL: {ex.Message}");
        }

        File.AppendAllLines(launcherLog, lines);

        // Laisse à zaap-server le temps de s'attacher au port.
        System.Threading.Thread.Sleep(500);

        if (!File.Exists(RealDofusBinary))
        {
            throw new FileNotFoundException(
                $"dofus-real.exe introuvable à côté du launcher : {RealDofusBinary}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = RealDofusBinary,
            WorkingDirectory = LauncherDir,
            UseShellExecute = false,
        };
        using (Process.Start(psi)) { }

        // Pas d'execv sous Windows : on spawn dofus-real puis on quitte le
        // launcher pour libérer le focus sur la nouvelle fenêtre.
        Environment.Exit(0);
    }

    private static void KillExistingZaap()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("zaap-server"))
            {
                try { p.Kill(true); } catch { }
            }
        }
        catch { }
    }

    private static void StartZaap(string login, string password, string hash,
                                  int instanceId, string authAddr)
    {
        if (!File.Exists(ZaapServerBinary)) return;

        Directory.CreateDirectory(LogDir);
        var zaapLog = Path.Combine(LogDir, "zaap-server.log");

        // On ne redirige PAS stdout/stderr : Environment.Exit(0) sur le
        // launcher ferme les pipes anonymes hérités → broken pipe côté zaap.
        // D'où --log-file, qui fait gérer l'append au zaap-server lui-même.
        // game-token == password (Giny renvoie le password en clair).
        var psi = new ProcessStartInfo
        {
            FileName = ZaapServerBinary,
            WorkingDirectory = LauncherDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add($"--port={ZaapPort}");
        psi.ArgumentList.Add($"--http-port={ZaapHttpPort}");
        psi.ArgumentList.Add($"--hash={hash}");
        psi.ArgumentList.Add($"--instance-id={instanceId}");
        psi.ArgumentList.Add($"--login={login}");
        psi.ArgumentList.Add($"--game-token={password}");
        psi.ArgumentList.Add($"--auth-addr={authAddr}");
        psi.ArgumentList.Add($"--log-file={zaapLog}");

        using var p = Process.Start(psi);
    }
}
