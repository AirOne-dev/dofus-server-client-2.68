using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Giny.Core;
using Giny.Core.IO.Configuration;
using Giny.Protocol.Custom.Enums;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.Protocol.Types;
using Giny.World.Managers.Effects;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Experiences;
using Giny.World.Managers.Items;
using Giny.World.Managers.Items.Collections;
using Giny.World.Network;
using Giny.World.Records.Items;
using MySql.Data.MySqlClient;

namespace Giny.World.Managers.Chat
{
    public static class OneAirActionPoller
    {
        private const int IntervalMs = 1500;
        private static volatile bool _running = false;

        public static void Start()
        {
            if (_running) return;
            _running = true;
            Task.Run(LoopAsync);
            Logger.Write("[OneAir] Action poller started", Channels.Info);
        }

        public static void Stop() { _running = false; }

        private static async Task LoopAsync()
        {
            try { EnsureSchema(); }
            catch (Exception e) { Logger.Write("[OneAir] schema init failed: " + e.Message, Channels.Warning); }

            while (_running)
            {
                try { Tick(); }
                catch (Exception e) { Logger.Write("[OneAir] poller tick failed: " + e.Message, Channels.Warning); }
                await Task.Delay(IntervalMs);
            }
        }

        private static MySqlConnection OpenConnection()
        {
            var cfg = ConfigManager<WorldConfig>.Instance;
            var cs = $"Server={cfg.SQLHost};Database={cfg.SQLDBName};Uid={cfg.SQLUser};" +
                     $"Pwd={cfg.SQLPassword};AllowPublicKeyRetrieval=true;SslMode=None;Pooling=true;";
            var c = new MySqlConnection(cs);
            c.Open();
            return c;
        }

        private static void EnsureSchema()
        {
            using var c = OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS actions (
    Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    Type VARCHAR(48) NOT NULL,
    Payload TEXT,
    ProcessedAt DATETIME NULL,
    Result TEXT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    KEY ix_unprocessed (ProcessedAt, Id)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS online_clients (
    CharacterId BIGINT NOT NULL PRIMARY KEY,
    AccountId INT NULL,
    Name VARCHAR(255) NULL,
    Level INT NULL,
    MapId BIGINT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB";
            cmd.ExecuteNonQuery();
        }

        private static void RefreshOnlineTable(MySqlConnection conn)
        {
            var clients = WorldServer.Instance.GetOnlineClients().ToArray();

            // DELETE + INSERT dans une transaction : TRUNCATE est DDL et
            // committerait implicitement, exposant la table vide.
            using var tx = conn.BeginTransaction();
            try
            {
                using (var del = conn.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM online_clients";
                    del.ExecuteNonQuery();
                }

                if (clients.Length > 0)
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO online_clients (CharacterId, AccountId, Name, Level, MapId) VALUES (@cid, @aid, @n, @lvl, @m)";
                    var pCid = ins.Parameters.Add("@cid", MySqlDbType.Int64);
                    var pAid = ins.Parameters.Add("@aid", MySqlDbType.Int32);
                    var pName = ins.Parameters.Add("@n", MySqlDbType.VarChar);
                    var pLvl = ins.Parameters.Add("@lvl", MySqlDbType.Int32);
                    var pMap = ins.Parameters.Add("@m", MySqlDbType.Int64);

                    foreach (var c in clients)
                    {
                        try
                        {
                            pCid.Value = c.Character.Id;
                            pAid.Value = c.Character.Record.AccountId;
                            pName.Value = c.Character.Name;
                            pLvl.Value = ExperienceManager.Instance.GetCharacterLevel(c.Character.Record.Experience);
                            pMap.Value = c.Character.Map?.Id ?? 0;
                            ins.ExecuteNonQuery();
                        }
                        catch { }
                    }
                }

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        private static void Tick()
        {
            using var conn = OpenConnection();

            try { RefreshOnlineTable(conn); } catch { }
            try { OneAirEventManager.CheckExpired(); } catch { }

            var pending = new List<(long Id, string Type, string Payload)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT Id, Type, COALESCE(Payload,'') FROM actions " +
                                  "WHERE ProcessedAt IS NULL ORDER BY Id LIMIT 50";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    pending.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
            }

            foreach (var (id, type, payload) in pending)
            {
                string result = "ok";
                try { Dispatch(type, payload); }
                catch (Exception e)
                {
                    result = "err: " + e.Message;
                    Logger.Write($"[OneAir] action {type}#{id} failed: {e.Message}", Channels.Warning);
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE actions SET ProcessedAt = NOW(), Result = @r WHERE Id = @id";
                cmd.Parameters.AddWithValue("@r", result.Length > 250 ? result.Substring(0, 250) : result);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        private static void Dispatch(string type, string payload)
        {
            switch (type)
            {
                case "broadcast":         Broadcast(payload); break;
                case "kick":              Kick(payload); break;
                case "reload_inventory":  ReloadInventory(payload); break;
                case "send_pm":           SendPm(payload); break;
                case "teleport":          Teleport(payload); break;
                case "set_kamas":         SetKamas(payload); break;
                case "give_kamas":        GiveKamas(payload); break;
                case "set_level":         SetLevel(payload); break;
                case "give_xp":           GiveXp(payload); break;
                case "give_item":         GiveItem(payload); break;
                case "heal":              Heal(payload); break;
                case "save_now":          SaveNow(); break;
                case "reload_items":      ItemsManager.Instance.Reload(); break;
                case "shutdown":          Shutdown(payload); break;
                case "dump_inventory":    DumpInventory(payload); break;
                case "item_set_qty":      ItemSetQty(payload); break;
                case "item_set_pos":      ItemSetPos(payload); break;
                case "item_delete":       ItemDelete(payload); break;
                case "item_eff_add":      ItemEffAdd(payload); break;
                case "item_eff_set":      ItemEffSet(payload); break;
                case "item_eff_del":      ItemEffDel(payload); break;
                case "learn_spell":       LearnSpell(payload); break;
                case "forget_spell":      ForgetSpell(payload); break;
                case "set_spell_level":   SetSpellLevel(payload); break;
                case "reset_spells":      ResetSpells(payload); break;
                case "dump_spells":       DumpSpells(payload); break;
                case "set_look":          SetLook(payload); break;
                case "set_breed":         SetBreed(payload); break;
                case "set_sex":           SetSex(payload); break;
                case "set_head":          SetHead(payload); break;
                case "delete_character":  DeleteCharacter(payload); break;
                case "reset_character":   ResetCharacter(payload); break;
                case "bulk_give_kamas":   BulkGiveKamas(payload); break;
                case "bulk_give_xp":      BulkGiveXp(payload); break;
                case "bulk_give_item":    BulkGiveItem(payload); break;
                case "bulk_heal":         BulkHeal(); break;
                case "event_set":         EventSet(payload); break;
                case "event_clear":       EventClear(payload); break;
                default:
                    Logger.Write("[OneAir] unknown action type: " + type, Channels.Warning);
                    break;
            }
        }

        private static void EnsureInventoryTable()
        {
            using var c = OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS inventory_dumps (
    CharacterId BIGINT NOT NULL PRIMARY KEY,
    Json LONGTEXT,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB";
            cmd.ExecuteNonQuery();
        }

        private static void DumpInventory(string payload)
        {
            if (!long.TryParse(payload, out var cid))
                throw new Exception("charId invalide");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("personnage hors ligne");
            EnsureInventoryTable();

            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            var items = c.Character.Inventory.GetItems();
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append(",");
                var it = items[i];
                var name = it.Record == null ? "?" : it.Record.Name;
                name = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
                int type = it.Record == null ? 0 : (int)it.Record.TypeId;
                int level = it.Record == null ? 0 : (int)it.Record.Level;
                int usable = (it.Record != null && it.Record.Usable) ? 1 : 0;
                sb.Append("{\"uid\":").Append(it.UId)
                  .Append(",\"gid\":").Append(it.GId)
                  .Append(",\"qty\":").Append(it.Quantity)
                  .Append(",\"pos\":").Append((int)it.Position)
                  .Append(",\"eq\":").Append(it.IsEquiped() ? 1 : 0)
                  .Append(",\"type\":").Append(type)
                  .Append(",\"level\":").Append(level)
                  .Append(",\"usable\":").Append(usable)
                  .Append(",\"name\":\"").Append(name).Append("\"")
                  .Append(",\"effects\":[");
                int j = 0;
                foreach (var eff in it.Effects)
                {
                    if (j > 0) sb.Append(",");
                    int val = 0;
                    if (eff is EffectInteger) val = ((EffectInteger)eff).Value;
                    else if (eff is EffectDice) val = ((EffectDice)eff).Min;
                    sb.Append("{\"id\":").Append(eff.EffectId)
                      .Append(",\"value\":").Append(val).Append("}");
                    j++;
                }
                sb.Append("]}");
            }
            sb.Append("]");

            using var conn = OpenConnection();
            using var up = conn.CreateCommand();
            up.CommandText = "REPLACE INTO inventory_dumps (CharacterId, Json) VALUES (@cid, @json)";
            up.Parameters.AddWithValue("@cid", cid);
            up.Parameters.AddWithValue("@json", sb.ToString());
            up.ExecuteNonQuery();
        }

        // payload: <charId>|<uid>|<qty>
        private static void ItemSetQty(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 3) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!int.TryParse(p[1], out var uid)) throw new Exception("uid");
            if (!int.TryParse(p[2], out var qty)) throw new Exception("qty");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            var item = c.Character.Inventory.GetItem(uid);
            if (item == null) throw new Exception("uid introuvable");
            int delta = qty - item.Quantity;
            if (delta == 0) return;
            item.Quantity = qty;
            c.Send(new ObjectModifiedMessage(item.GetObjectItem()));
            DumpInventory(cid.ToString());
        }

        // payload: <charId>|<uid>|<position>
        private static void ItemSetPos(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 3) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!int.TryParse(p[1], out var uid)) throw new Exception("uid");
            if (!byte.TryParse(p[2], out var pos)) throw new Exception("pos");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            c.Character.Inventory.SetItemPosition(uid, (CharacterInventoryPositionEnum)pos, 1);
            DumpInventory(cid.ToString());
        }

        private static void ItemDelete(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 2) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!int.TryParse(p[1], out var uid)) throw new Exception("uid");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            var item = c.Character.Inventory.GetItem(uid);
            if (item == null) throw new Exception("uid introuvable");
            c.Character.Inventory.RemoveItem(item, item.Quantity);
            DumpInventory(cid.ToString());
        }

        // payload: <charId>|<uid>|<effectId>|<value>
        private static void ItemEffAdd(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 4) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!int.TryParse(p[1], out var uid)) throw new Exception("uid");
            if (!short.TryParse(p[2], out var effectId)) throw new Exception("effectId");
            if (!int.TryParse(p[3], out var value)) throw new Exception("value");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            var item = c.Character.Inventory.GetItem(uid);
            if (item == null) throw new Exception("uid introuvable");
            item.Effects.Add(new EffectInteger() { EffectId = effectId, Value = value });
            c.Send(new ObjectModifiedMessage(item.GetObjectItem()));
            if (item.IsEquiped()) c.Character.RefreshStats();
            DumpInventory(cid.ToString());
        }

        // payload: <charId>|<uid>|<index>|<value>
        private static void ItemEffSet(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 4) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!int.TryParse(p[1], out var uid)) throw new Exception("uid");
            if (!int.TryParse(p[2], out var index)) throw new Exception("index");
            if (!int.TryParse(p[3], out var value)) throw new Exception("value");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            var item = c.Character.Inventory.GetItem(uid);
            if (item == null) throw new Exception("uid introuvable");
            var effects = item.Effects.ToList();
            if (index < 0 || index >= effects.Count) throw new Exception("index hors limites");
            var eff = effects[index];
            if (eff is EffectInteger) ((EffectInteger)eff).Value = value;
            else if (eff is EffectDice) ((EffectDice)eff).Min = value;
            else throw new Exception("type d'effet non supporté");
            c.Send(new ObjectModifiedMessage(item.GetObjectItem()));
            if (item.IsEquiped()) c.Character.RefreshStats();
            DumpInventory(cid.ToString());
        }

        // payload: <charId>|<spellId>[|<level>]
        private static void LearnSpell(string payload)
        {
            var p = payload.Split('|');
            if (p.Length < 2) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!short.TryParse(p[1], out var spellId)) throw new Exception("spellId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            c.Character.LearnSpell(spellId, true);
            DumpSpells(cid.ToString());
        }

        private static void ForgetSpell(string payload)
        {
            var p = payload.Split('|');
            if (p.Length < 2) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!short.TryParse(p[1], out var spellId)) throw new Exception("spellId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            try { c.Character.ForgetSpell(spellId); } catch { }
            DumpSpells(cid.ToString());
        }

        // No-op : Giny dérive le grade depuis Character.Level via SpellLevels[].MinPlayerLevel
        // (pas de grade indépendant à set).
        private static void SetSpellLevel(string payload)
        {
            DumpSpells(payload.Split('|')[0]);
        }

        private static void ResetSpells(string payload)
        {
            if (!long.TryParse(payload, out var cid)) throw new Exception("charId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            try
            {
                c.Character.Record.Spells.Clear();
                c.Character.RefreshSpells();
            }
            catch (Exception e) { Logger.Write("[OneAir] reset_spells: " + e.Message, Channels.Warning); }
            DumpSpells(cid.ToString());
        }

        private static void EnsureSpellsTable()
        {
            using var c = OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS spell_dumps (
    CharacterId BIGINT NOT NULL PRIMARY KEY,
    Json LONGTEXT,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB";
            cmd.ExecuteNonQuery();
        }

        private static void DumpSpells(string payload)
        {
            if (!long.TryParse(payload, out var cid)) throw new Exception("charId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            EnsureSpellsTable();

            var sb = new System.Text.StringBuilder();
            sb.Append("[");
            int i = 0;
            foreach (var sp in c.Character.Record.Spells)
            {
                if (i++ > 0) sb.Append(",");
                int grade = 1;
                try { grade = sp.GetGrade(c.Character); } catch { }
                sb.Append("{\"id\":").Append(sp.SpellId)
                  .Append(",\"grade\":").Append(grade).Append("}");
            }
            sb.Append("]");

            using var conn = OpenConnection();
            using var up = conn.CreateCommand();
            up.CommandText = "REPLACE INTO spell_dumps (CharacterId, Json) VALUES (@cid, @json)";
            up.Parameters.AddWithValue("@cid", cid);
            up.Parameters.AddWithValue("@json", sb.ToString());
            up.ExecuteNonQuery();
        }

        // payload: <charId>|<lookString>  (look brut Dofus, ex: {200|0|24,1,2,3,4})
        private static void SetLook(string payload)
        {
            var i = payload.IndexOf('|');
            if (i <= 0) throw new Exception("payload");
            if (!long.TryParse(payload.Substring(0, i), out var cid)) throw new Exception("charId");
            var look = payload.Substring(i + 1);
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            try
            {
                var parsed = Giny.World.Managers.Entities.Look.EntityLookManager.Instance.Parse(look);
                c.Character.Record.Look = parsed;
                c.Character.RefreshLookOnMap();
            }
            catch (Exception e) { Logger.Write("[OneAir] set_look: " + e.Message, Channels.Warning); }
        }

        // payload: <charId>|<breedId>
        private static void SetBreed(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 2) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!byte.TryParse(p[1], out var breedId)) throw new Exception("breedId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            c.Character.Record.BreedId = breedId;
            c.Character.RefreshLookOnMap();
        }

        // payload: <charId>|0|1  (0 = M, 1 = F)
        private static void SetSex(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 2) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            try
            {
                var t = c.Character.Record.GetType().GetProperty("Sex");
                if (t != null) t.SetValue(c.Character.Record, p[1] == "1");
            }
            catch (Exception e) { Logger.Write("[OneAir] set_sex: " + e.Message, Channels.Warning); }
            c.Character.RefreshLookOnMap();
        }

        private static void DeleteCharacter(string payload)
        {
            if (!long.TryParse(payload, out var cid)) throw new Exception("charId");
            var c = ClientByCharId(cid);
            if (c != null) { try { c.Disconnect(); } catch { } }
            // Pas de cascade ON DELETE côté Giny → cleanup manuel.
            using var conn = OpenConnection();
            using var t = conn.BeginTransaction();
            try
            {
                using (var cmd = conn.CreateCommand()) {
                    cmd.Transaction = t;
                    cmd.CommandText = "DELETE FROM character_items WHERE CharacterId = @c; " +
                                      "DELETE FROM characters WHERE Id = @c;";
                    cmd.Parameters.AddWithValue("@c", cid);
                    cmd.ExecuteNonQuery();
                }
                // world_characters vit dans la DB auth (mapping account ↔ character).
                using (var cmd = conn.CreateCommand()) {
                    cmd.Transaction = t;
                    var auth = ConfigManager<WorldConfig>.Instance.SQLDBName.Replace("world", "auth");
                    cmd.CommandText = $"DELETE FROM {auth}.world_characters WHERE CharacterId = @c";
                    cmd.Parameters.AddWithValue("@c", cid);
                    try { cmd.ExecuteNonQuery(); } catch { }
                }
                t.Commit();
            }
            catch { t.Rollback(); throw; }
        }

        // Reset partiel : niveau 1, kamas 0, sorts vidés. Inventaire préservé.
        private static void ResetCharacter(string payload)
        {
            if (!long.TryParse(payload, out var cid)) throw new Exception("charId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne (reset nécessite que le perso soit online)");
            try
            {
                c.Character.Record.Kamas = 0;
                c.Character.SetExperience(0);
                c.Character.Record.Spells.Clear();
                c.Character.Stats.Life.Loss = 0;
                c.Character.RefreshStats();
                c.Character.RefreshSpells();
                c.Character.Reply("Votre personnage a été reset par un administrateur.");
            }
            catch (Exception e) { Logger.Write("[OneAir] reset_character: " + e.Message, Channels.Warning); }
        }

        private static void BulkGiveKamas(string payload)
        {
            if (!long.TryParse(payload, out var amount)) throw new Exception("amount");
            int n = 0;
            foreach (var c in WorldServer.Instance.GetOnlineClients())
            {
                try { c.Character.AddKamas(amount); c.Character.OnKamasGained(amount); n++; } catch { }
            }
            Broadcast($"💰 {amount:N0} kamas distribués à tout le monde par un administrateur ({n} joueurs).");
        }

        private static void BulkGiveXp(string payload)
        {
            if (!long.TryParse(payload, out var xp)) throw new Exception("xp");
            int n = 0;
            foreach (var c in WorldServer.Instance.GetOnlineClients())
            {
                try { c.Character.AddExperience(xp); n++; } catch { }
            }
            Broadcast($"⭐ {xp:N0} XP distribuée à tout le monde ({n} joueurs).");
        }

        // payload: <gid>|<qty>
        private static void BulkGiveItem(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 2) throw new Exception("payload");
            if (!short.TryParse(p[0], out var gid)) throw new Exception("gid");
            if (!int.TryParse(p[1], out var qty)) throw new Exception("qty");
            int n = 0;
            string itemName = "?";
            foreach (var c in WorldServer.Instance.GetOnlineClients())
            {
                try {
                    c.Character.Inventory.AddItem(gid, qty, true);
                    c.Character.NotifyItemGained(gid, qty);
                    var rec = ItemRecord.GetItem(gid);
                    if (rec != null) itemName = rec.Name;
                    n++;
                } catch { }
            }
            Broadcast($"🎁 « {itemName} » × {qty} donné à tout le monde ({n} joueurs).");
        }

        // payload: <type>|<multiplier>|<durationSeconds>
        // type ∈ {"xp","kamas","drop"} ; durationSeconds = 0 → permanent
        private static void EventSet(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 3) throw new Exception("payload");
            var type = p[0];
            if (!double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mul))
                throw new Exception("multiplier");
            if (!int.TryParse(p[2], out var dur)) throw new Exception("duration");
            OneAirEventManager.SetEvent(type, mul, dur);

            string label = type == "xp" ? "XP" : type == "kamas" ? "Kamas" : "Drop";
            string until = dur > 0 ? $" pendant {FormatDuration(dur)}" : " (permanent)";
            Broadcast($"🎉 Événement actif : <b>{label} × {mul}</b>{until} !");
        }

        private static void EventClear(string payload)
        {
            OneAirEventManager.ClearEvent(string.IsNullOrEmpty(payload) ? "all" : payload);
            Broadcast("ℹ️ Événement(s) terminé(s).");
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds < 60) return seconds + "s";
            if (seconds < 3600) return (seconds / 60) + "min";
            if (seconds < 86400) return (seconds / 3600) + "h";
            return (seconds / 86400) + "j";
        }

        private static void BulkHeal()
        {
            int n = 0;
            foreach (var c in WorldServer.Instance.GetOnlineClients())
            {
                try { c.Character.Stats.Life.Loss = 0; c.Character.RefreshStats(); n++; } catch { }
            }
            Broadcast($"❤ Soin général : {n} joueurs soignés.");
        }

        // payload: <charId>|<cosmeticId>
        private static void SetHead(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 2) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!short.TryParse(p[1], out var head)) throw new Exception("headId");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            c.Character.Record.CosmeticId = head;
            c.Character.RefreshLookOnMap();
        }

        // payload: <charId>|<uid>|<index>
        private static void ItemEffDel(string payload)
        {
            var p = payload.Split('|');
            if (p.Length != 3) throw new Exception("payload");
            if (!long.TryParse(p[0], out var cid)) throw new Exception("charId");
            if (!int.TryParse(p[1], out var uid)) throw new Exception("uid");
            if (!int.TryParse(p[2], out var index)) throw new Exception("index");
            var c = ClientByCharId(cid);
            if (c == null) throw new Exception("hors ligne");
            var item = c.Character.Inventory.GetItem(uid);
            if (item == null) throw new Exception("uid introuvable");
            var effects = item.Effects.ToList();
            if (index < 0 || index >= effects.Count) throw new Exception("index hors limites");
            item.Effects.Remove(effects[index]);
            c.Send(new ObjectModifiedMessage(item.GetObjectItem()));
            if (item.IsEquiped()) c.Character.RefreshStats();
            DumpInventory(cid.ToString());
        }

        private static WorldClient ClientByCharId(long id)
            => WorldServer.Instance.GetOnlineClients().FirstOrDefault(c => c.Character.Id == id);

        private static WorldClient ClientByName(string name)
            => WorldServer.Instance.GetOnlineClients().FirstOrDefault(
                c => string.Equals(c.Character.Name, name, StringComparison.OrdinalIgnoreCase));

        // "key1=val1|key2=val2"
        private static Dictionary<string, string> ParseKv(string payload)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(payload)) return d;
            foreach (var p in payload.Split('|'))
            {
                var i = p.IndexOf('=');
                if (i <= 0) continue;
                d[p.Substring(0, i)] = p.Substring(i + 1);
            }
            return d;
        }

        private static void Broadcast(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            foreach (var target in WorldServer.Instance.GetOnlineClients())
                target.Character.DisplayNotification("Serveur : " + message);
        }

        // payload: "<id>" ou "<id>|reason"
        private static void Kick(string payload)
        {
            var parts = payload.Split('|');
            if (!long.TryParse(parts[0], out var cid)) return;
            var c = ClientByCharId(cid);
            if (c == null) return;
            try { c.Character.Reply("Vous avez été déconnecté par un administrateur."); } catch { }
            try { c.Disconnect(); } catch (Exception e) { Logger.Write("[OneAir] kick: " + e.Message, Channels.Warning); }
        }

        private static void ReloadInventory(string payload)
        {
            if (!long.TryParse(payload, out var cid)) return;
            var c = ClientByCharId(cid);
            if (c == null) return;
            try { c.Character.Inventory.Refresh(); }
            catch (Exception e) { Logger.Write("[OneAir] reload_inventory: " + e.Message, Channels.Warning); }
        }

        // payload: "<charId>|<message>"
        private static void SendPm(string payload)
        {
            var i = payload.IndexOf('|');
            if (i <= 0) return;
            if (!long.TryParse(payload.Substring(0, i), out var cid)) return;
            var msg = payload.Substring(i + 1);
            var c = ClientByCharId(cid);
            if (c == null) return;
            c.Character.Reply("[Admin] " + msg);
        }

        // payload: "<charId>|<mapId>" or "<charId>|<mapId>|<cellId>"
        private static void Teleport(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length < 2) return;
            if (!long.TryParse(parts[0], out var cid)) return;
            if (!int.TryParse(parts[1], out var mapId)) return;
            short cellId = -1;
            if (parts.Length >= 3) short.TryParse(parts[2], out cellId);
            var c = ClientByCharId(cid);
            if (c == null) return;
            if (cellId >= 0) c.Character.Teleport(mapId, cellId);
            else c.Character.Teleport(mapId);
        }

        // payload: "<charId>|<amount>"
        private static void SetKamas(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length != 2) return;
            if (!long.TryParse(parts[0], out var cid)) return;
            if (!long.TryParse(parts[1], out var amount)) return;
            var c = ClientByCharId(cid);
            if (c == null) return;
            var current = c.Character.Record.Kamas;
            var delta = amount - current;
            if (delta > 0) c.Character.AddKamas(delta);
            else if (delta < 0) c.Character.AddKamas(delta);
            c.Character.OnKamasGained(delta);
        }

        private static void GiveKamas(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length != 2) return;
            if (!long.TryParse(parts[0], out var cid)) return;
            if (!long.TryParse(parts[1], out var amount)) return;
            var c = ClientByCharId(cid);
            if (c == null) return;
            c.Character.AddKamas(amount);
            c.Character.OnKamasGained(amount);
        }

        // payload: "<charId>|<level>"
        private static void SetLevel(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length != 2) return;
            if (!long.TryParse(parts[0], out var cid)) return;
            if (!short.TryParse(parts[1], out var level)) return;
            level = Math.Clamp(level, (short)1, (short)200);
            var c = ClientByCharId(cid);
            if (c == null) return;
            c.Character.SetExperience(ExperienceManager.Instance.GetCharacterXPForLevel(level));
        }

        // payload: "<charId>|<xp>"
        private static void GiveXp(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length != 2) return;
            if (!long.TryParse(parts[0], out var cid)) return;
            if (!long.TryParse(parts[1], out var xp)) return;
            var c = ClientByCharId(cid);
            if (c == null) return;
            c.Character.AddExperience(xp);
        }

        // payload: "<charId>|<gid>|<qty>"
        private static void GiveItem(string payload)
        {
            var parts = payload.Split('|');
            if (parts.Length < 3) return;
            if (!long.TryParse(parts[0], out var cid)) return;
            if (!short.TryParse(parts[1], out var gid)) return;
            if (!int.TryParse(parts[2], out var qty)) return;
            var c = ClientByCharId(cid);
            if (c == null) return;
            c.Character.Inventory.AddItem(gid, qty, true);
            c.Character.NotifyItemGained(gid, qty);
        }

        private static void Heal(string payload)
        {
            if (!long.TryParse(payload, out var cid)) return;
            var c = ClientByCharId(cid);
            if (c == null) return;
            try
            {
                c.Character.Stats.Life.Loss = 0;
                c.Character.RefreshStats();
            }
            catch (Exception e) { Logger.Write("[OneAir] heal: " + e.Message, Channels.Warning); }
        }

        private static void SaveNow()
        {
            try { Giny.World.Managers.WorldSaveManager.Instance.PerformSave(); }
            catch (Exception e) { Logger.Write("[OneAir] save_now: " + e.Message, Channels.Warning); }
        }

        // payload: "<seconds>|<message>"
        private static void Shutdown(string payload)
        {
            var parts = payload.Split('|');
            if (!int.TryParse(parts[0], out var seconds)) seconds = 60;
            var msg = parts.Length > 1 ? parts[1] : "Le serveur va redémarrer.";
            seconds = Math.Clamp(seconds, 5, 3600);

            Task.Run(async () =>
            {
                int[] alerts = { seconds, 60, 30, 15, 10, 5, 3, 2, 1 };
                int remaining = seconds;
                int last = remaining;
                Broadcast($"{msg} Arrêt dans {remaining}s.");
                while (remaining > 0)
                {
                    int next = alerts.Where(a => a < remaining).DefaultIfEmpty(0).Max();
                    int sleep = remaining - next;
                    await Task.Delay(sleep * 1000);
                    remaining = next;
                    if (remaining > 0)
                        Broadcast($"{msg} Arrêt dans {remaining}s.");
                }
                Broadcast("Serveur en arrêt…");
                try { SaveNow(); } catch { }
                await Task.Delay(2000);
                Environment.Exit(0);
            });
        }
    }
}
