// Persistance dans %APPDATA%\OneAir\settings.json.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OneAirLauncher;

public sealed class OneAirAccount
{
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SettingsData
{
    public string Host { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "5555";
    public bool SaveLogin { get; set; } = true;
    public string LastAccount { get; set; } = "";
    public List<OneAirAccount> Accounts { get; set; } = new();
}

public static class Settings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneAir");
    private static readonly string Path_ = Path.Combine(Dir, "settings.json");

    private static SettingsData _data = Load();

    public static SettingsData Data => _data;

    public static SettingsData Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var json = File.ReadAllText(Path_);
                var data = JsonSerializer.Deserialize<SettingsData>(json);
                if (data != null) return data;
            }
        }
        catch { }
        return new SettingsData();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path_, JsonSerializer.Serialize(_data, opts));
        }
        catch { }
    }

    public static void UpsertAccount(string login, string password)
    {
        if (string.IsNullOrWhiteSpace(login)) return;
        var existing = _data.Accounts.Find(a => a.Login == login);
        if (existing != null) existing.Password = password;
        else _data.Accounts.Add(new OneAirAccount { Login = login, Password = password });
        _data.LastAccount = login;
        Save();
    }

    public static void RemoveAccount(string login)
    {
        _data.Accounts.RemoveAll(a => a.Login == login);
        if (_data.LastAccount == login)
            _data.LastAccount = _data.Accounts.Count > 0 ? _data.Accounts[0].Login : "";
        Save();
    }
}
