using System;
using System.Collections.Generic;
using Giny.Core;
using Giny.Core.IO.Configuration;
using MySql.Data.MySqlClient;

namespace Giny.World.Managers.Events
{
    public static class OneAirEventManager
    {
        // Lus à chaque grant XP/Kamas par les hooks patchés dans Character.cs.
        public static double XpMultiplier    = 1.0;
        public static double KamasMultiplier = 1.0;
        public static double DropMultiplier  = 1.0;

        // null = permanent.
        public static DateTime? XpExpiresAt    = null;
        public static DateTime? KamasExpiresAt = null;
        public static DateTime? DropExpiresAt  = null;

        public static void Initialize()
        {
            try
            {
                EnsureSchema();
                Reload();
                Logger.Write($"[OneAir] EventManager initialized (xp×{XpMultiplier}, kamas×{KamasMultiplier}, drop×{DropMultiplier})", Channels.Info);
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] EventManager init failed: " + e.Message, Channels.Warning);
            }
        }

        private static MySqlConnection OpenConn()
        {
            var cfg = ConfigManager<WorldConfig>.Instance;
            var cs = $"Server={cfg.SQLHost};Database={cfg.SQLDBName};Uid={cfg.SQLUser};" +
                     $"Pwd={cfg.SQLPassword};AllowPublicKeyRetrieval=true;SslMode=None;";
            var c = new MySqlConnection(cs);
            c.Open();
            return c;
        }

        private static void EnsureSchema()
        {
            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS events (
    Type VARCHAR(32) NOT NULL PRIMARY KEY,
    Multiplier DOUBLE NOT NULL DEFAULT 1.0,
    ExpiresAt DATETIME NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UpdatedBy VARCHAR(64) NULL
) ENGINE=InnoDB";
            cmd.ExecuteNonQuery();
        }

        public static void Reload()
        {
            XpMultiplier = 1.0; KamasMultiplier = 1.0; DropMultiplier = 1.0;
            XpExpiresAt = KamasExpiresAt = DropExpiresAt = null;

            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Type, Multiplier, ExpiresAt FROM events";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var type = r.GetString(0);
                var mul = r.GetDouble(1);
                DateTime? exp = r.IsDBNull(2) ? null : r.GetDateTime(2);
                if (exp.HasValue && exp.Value <= DateTime.UtcNow) continue;
                Apply(type, mul, exp);
            }
        }

        private static void Apply(string type, double mul, DateTime? exp)
        {
            switch (type)
            {
                case "xp":    XpMultiplier = mul;    XpExpiresAt = exp; break;
                case "kamas": KamasMultiplier = mul; KamasExpiresAt = exp; break;
                case "drop":  DropMultiplier = mul;  DropExpiresAt = exp; break;
            }
        }

        // type ∈ {"xp","kamas","drop"} ; durationSeconds = 0 → permanent.
        public static void SetEvent(string type, double multiplier, int durationSeconds)
        {
            if (type != "xp" && type != "kamas" && type != "drop")
                throw new ArgumentException("type invalide: " + type);
            if (multiplier <= 0) multiplier = 1.0;

            DateTime? exp = durationSeconds > 0 ? DateTime.UtcNow.AddSeconds(durationSeconds) : null;

            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"REPLACE INTO events (Type, Multiplier, ExpiresAt) VALUES (@t, @m, @e)";
            cmd.Parameters.AddWithValue("@t", type);
            cmd.Parameters.AddWithValue("@m", multiplier);
            cmd.Parameters.AddWithValue("@e", (object)exp ?? DBNull.Value);
            cmd.ExecuteNonQuery();

            Apply(type, multiplier, exp);
            Logger.Write($"[OneAir] Event {type} × {multiplier} {(exp.HasValue ? "until " + exp.Value : "(permanent)")}", Channels.Info);
            BroadcastStatusToAll();
        }

        public static void ClearEvent(string type)
        {
            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            if (type == "all")
            {
                cmd.CommandText = "DELETE FROM events";
            }
            else
            {
                cmd.CommandText = "DELETE FROM events WHERE Type = @t";
                cmd.Parameters.AddWithValue("@t", type);
            }
            cmd.ExecuteNonQuery();
            Reload();
            Logger.Write($"[OneAir] Event(s) cleared: {type}", Channels.Info);
            BroadcastStatusToAll();
        }

        // __ONEAIR_EVENTS__xp:10|kamas:5|drop:1[|xp_exp:<unix>|...]
        public static string BuildStatusPayload()
        {
            string Fmt(double m) => m.ToString("0.##",
                System.Globalization.CultureInfo.InvariantCulture);
            var parts = new System.Collections.Generic.List<string>();
            parts.Add("xp:" + Fmt(XpMultiplier));
            parts.Add("kamas:" + Fmt(KamasMultiplier));
            parts.Add("drop:" + Fmt(DropMultiplier));
            if (XpExpiresAt.HasValue)
                parts.Add("xp_exp:" + new DateTimeOffset(XpExpiresAt.Value, TimeSpan.Zero).ToUnixTimeSeconds());
            if (KamasExpiresAt.HasValue)
                parts.Add("kamas_exp:" + new DateTimeOffset(KamasExpiresAt.Value, TimeSpan.Zero).ToUnixTimeSeconds());
            if (DropExpiresAt.HasValue)
                parts.Add("drop_exp:" + new DateTimeOffset(DropExpiresAt.Value, TimeSpan.Zero).ToUnixTimeSeconds());
            return "__ONEAIR_EVENTS__" + string.Join("|", parts);
        }

        // Le SWF custom intercepte le préfixe __ONEAIR_EVENTS__ et ne l'affiche pas en chat.
        public static void BroadcastStatusToAll()
        {
            try
            {
                var payload = BuildStatusPayload();
                foreach (var c in Giny.World.Network.WorldServer.Instance.GetOnlineClients())
                {
                    try { c.Character.Reply(payload); } catch { }
                }
            }
            catch (Exception e) { Logger.Write("[OneAir] BroadcastStatus failed: " + e.Message, Channels.Warning); }
        }

        public static void SendStatusTo(Giny.World.Network.WorldClient client)
        {
            try { client.Character.Reply(BuildStatusPayload()); } catch { }
        }

        public static void CheckExpired()
        {
            var now = DateTime.UtcNow;
            var expired = new List<string>();
            if (XpExpiresAt.HasValue    && XpExpiresAt.Value    <= now) expired.Add("xp");
            if (KamasExpiresAt.HasValue && KamasExpiresAt.Value <= now) expired.Add("kamas");
            if (DropExpiresAt.HasValue  && DropExpiresAt.Value  <= now) expired.Add("drop");
            foreach (var t in expired)
            {
                try { ClearEvent(t); } catch { }
            }
        }
    }
}
