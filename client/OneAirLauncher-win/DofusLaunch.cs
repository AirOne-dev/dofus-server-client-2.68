// Lance le vrai Dofus.exe (renommé dofus-real.exe), après avoir :
// 1. patché config.xml avec host/port saisis,
// 2. écrit credentials.json (lu par le SWF Giny patché en BUILD_TYPE=DEBUG),
// 3. spawné zaap-server.exe en arrière-plan.
//
// Sur Windows on n'a pas execv : on Process.Start le vrai exe puis on
// quitte le launcher pour libérer le focus.

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

        // 1. config.xml
        if (File.Exists(ConfigXml))
        {
            try { ConfigPatcher.Patch(ConfigXml, serverName, host, port); }
            catch (Exception ex) { lines.Add($"  config.xml patch FAIL: {ex.Message}"); }
        }
        else
        {
            lines.Add("  config.xml introuvable — skip patch");
        }

        // 2. credentials.json (lu par ZaapConnectionHelper.connect en DEBUG)
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

        // 3. zaap-server.exe
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

        // Petite pause pour laisser zaap-server s'attacher au port
        System.Threading.Thread.Sleep(500);

        // 4. exec (équivalent : on spawn et on quitte)
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
        Process.Start(psi);

        // On quitte le launcher pour libérer le focus.
        Environment.Exit(0);
    }

    private static void KillExistingZaap()
    {
        // Tue les zaap-server zombies pour libérer 4242/4243 avant respawn.
        try
        {
            foreach (var p in Process.GetProcessesByName("zaap-server"))
            {
                try { p.Kill(true); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    private static void StartZaap(string login, string password, string hash,
                                  int instanceId, string authAddr)
    {
        if (!File.Exists(ZaapServerBinary)) return;

        Directory.CreateDirectory(LogDir);
        var zaapLog = Path.Combine(LogDir, "zaap-server.log");

        // game-token == password (cf. Giny.Zaap.HandleAuthGetGameToken).
        var psi = new ProcessStartInfo
        {
            FileName = ZaapServerBinary,
            WorkingDirectory = LauncherDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add($"--port={ZaapPort}");
        psi.ArgumentList.Add($"--http-port={ZaapHttpPort}");
        psi.ArgumentList.Add($"--hash={hash}");
        psi.ArgumentList.Add($"--instance-id={instanceId}");
        psi.ArgumentList.Add($"--login={login}");
        psi.ArgumentList.Add($"--game-token={password}");
        psi.ArgumentList.Add($"--auth-addr={authAddr}");

        var p = Process.Start(psi);
        if (p == null) return;

        // Redirige stdout/err en append vers le log
        var sw = new StreamWriter(new FileStream(zaapLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        sw.AutoFlush = true;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) sw.WriteLine(e.Data); };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) sw.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }
}
