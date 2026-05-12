// Logger diagnostic non-throwing + non-bloquant : tous les LogXxx() wrappent
// leur corps dans un try/catch et enqueuent dans une ConcurrentQueue ; un
// task background flushe vers unhandled_log par batch toutes les 2s.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Giny.Core;
using Giny.Core.IO.Configuration;
using Giny.Core.Network.Messages;
using Giny.World.Managers.Effects;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Generic;
using Giny.World.Managers.Maps.Elements;
using Giny.World.Network;
using Giny.World.Records.Items;
using MySql.Data.MySqlClient;

namespace Giny.World.Managers.Chat
{
    public static class OneAirUnhandledLogger
    {
        // Liste fermée et stable (filtrage côté admin web).
        public const string CatItemUse        = "item_use";
        public const string CatItemUseError   = "item_use_error";
        public const string CatItemEffect     = "item_effect";
        public const string CatSpellEffect    = "spell_effect";
        public const string CatGenericAction  = "generic_action";
        public const string CatInteractive    = "interactive";
        public const string CatInteractiveErr = "interactive_err";
        public const string CatNpcAction      = "npc_action";
        public const string CatExchangeReq    = "exchange_request";
        public const string CatNetMessage     = "net_message";
        public const string CatNetError       = "net_error";
        public const string CatPaddock        = "paddock";
        public const string CatMount          = "mount";
        public const string CatTaxCollector   = "taxcollector";
        public const string CatHouse          = "house";
        public const string CatFriend         = "friend";
        public const string CatGuild          = "guild";
        public const string CatPet            = "pet";
        public const string CatJob            = "job";
        public const string CatGeneric        = "generic";

        private struct Entry
        {
            public DateTime AtUtc;
            public long? CharacterId;
            public string CharacterName;
            public string Category;
            public string Detail;
            public string PayloadJson;
        }

        private const int MaxQueue = 4096;
        private const int FlushIntervalMs = 2000;
        private const int BatchSize = 200;
        private const int RetentionDays = 30;
        private const int MaxRowsPerCategory = 500;

        private static readonly ConcurrentQueue<Entry> _queue = new ConcurrentQueue<Entry>();
        private static volatile bool _started = false;
        private static long _droppedCount = 0;

        // Dédup 30s : un effet appliqué à chaque tick combat saturerait la table.
        private static readonly ConcurrentDictionary<string, DateTime> _recentKeys =
            new ConcurrentDictionary<string, DateTime>();
        private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(30);

        public static void Start()
        {
            if (_started) return;
            _started = true;
            try { EnsureSchema(); }
            catch (Exception e) { Logger.Write("[OneAir/UnhandledLogger] schema init failed: " + e.Message, Channels.Warning); }
            Task.Run(FlushLoopAsync);
            Logger.Write("[OneAir/UnhandledLogger] started", Channels.Info);
        }

        public static void EnsureSchema()
        {
            using var c = OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS unhandled_log (
    Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    AtUtc DATETIME NOT NULL,
    CharacterId BIGINT NULL,
    CharacterName VARCHAR(64) NULL,
    Category VARCHAR(48) NOT NULL,
    Detail VARCHAR(255) NOT NULL,
    PayloadJson MEDIUMTEXT NULL,
    KEY ix_at (AtUtc),
    KEY ix_cat (Category, AtUtc),
    KEY ix_char (CharacterId, AtUtc)
) ENGINE=InnoDB";
            cmd.ExecuteNonQuery();
        }

        public static void LogItemUse(Character c, CharacterItemRecord item, string reason)
        {
            try
            {
                var detail = "GId=" + (item?.GId ?? 0) + " Type=" + (item?.Record?.TypeEnum) + " " + reason;
                var payload = ItemPayload(item);
                Enqueue(c, CatItemUse, detail, payload);
            }
            catch { }
        }

        public static void LogItemUseError(Character c, CharacterItemRecord item, Exception ex)
        {
            try
            {
                var detail = "GId=" + (item?.GId ?? 0) + " ex=" + ShortType(ex);
                var payload = ItemPayload(item) + "\nException: " + ex;
                Enqueue(c, CatItemUseError, detail, payload);
            }
            catch { }
        }

        public static void LogItemEffect(Character c, EffectInteger effect)
        {
            try
            {
                var detail = "EffectEnum=" + effect?.EffectEnum + " value=" + effect?.Value;
                Enqueue(c, CatItemEffect, detail, EffectPayload(effect));
            }
            catch { }
        }

        public static void LogSpellEffect(Character c, Effect effect, int spellId, int spellLevel)
        {
            try
            {
                var detail = "EffectEnum=" + effect?.EffectEnum + " spellId=" + spellId + " level=" + spellLevel;
                Enqueue(c, CatSpellEffect, detail, EffectPayload(effect) + "\nSpell=" + spellId + " lvl " + spellLevel);
            }
            catch { }
        }

        public static void LogGenericAction(Character c, IGenericAction parameter)
        {
            try
            {
                var detail = "ActionId=" + parameter?.ActionIdentifier + " Param1=" + parameter?.Param1 + " Param2=" + parameter?.Param2;
                Enqueue(c, CatGenericAction, detail, "GenericAction " + parameter);
            }
            catch { }
        }

        public static void LogInteractive(Character c, MapInteractiveElement element)
        {
            try
            {
                if (element?.Record == null) { Enqueue(c, CatInteractive, "element=null", null); return; }
                var detail = "ElementId=" + element.Record.Identifier + " BonesId=" + element.Record.BonesId
                           + " SkillId=" + element.Record.Skill?.SkillId;
                var payload = "MapId=" + (c?.Map?.Id) + "\nCellId=" + element.Record.CellId
                           + "\nElement=" + element + "\nSkill=" + element.Record.Skill?.Record;
                Enqueue(c, CatInteractive, detail, payload);
            }
            catch { }
        }

        public static void LogInteractiveError(Character c, int elemId, int skillInstanceUid)
        {
            try
            {
                var detail = "elemId=" + elemId + " skillUid=" + skillInstanceUid;
                Enqueue(c, CatInteractiveErr, detail, "MapId=" + c?.Map?.Id);
            }
            catch { }
        }

        public static void LogNpcAction(Character c, long npcId, string actionType, long npcRecordId)
        {
            try
            {
                var detail = "npcId=" + npcId + " spawn=" + npcRecordId + " action=" + actionType;
                Enqueue(c, CatNpcAction, detail, "MapId=" + c?.Map?.Id);
            }
            catch { }
        }

        public static void LogExchangeRequest(Character c, int exchangeType, long targetId)
        {
            try
            {
                var detail = "type=" + exchangeType + " target=" + targetId;
                Enqueue(c, CatExchangeReq, detail, "MapId=" + c?.Map?.Id);
            }
            catch { }
        }

        public static void LogNetMessage(WorldClient client, NetworkMessage message)
        {
            try
            {
                var msgName = message?.GetType()?.Name ?? "<null>";
                var detail = "id=" + message?.MessageId + " " + msgName;
                var payload = "Message=" + message;
                EnqueueClient(client, CatNetMessage, detail, payload);
            }
            catch { }
        }

        public static void LogNetError(WorldClient client, NetworkMessage message, Exception ex)
        {
            try
            {
                var msgName = message?.GetType()?.Name ?? "<null>";
                var detail = "id=" + message?.MessageId + " " + msgName + " ex=" + ShortType(ex);
                var payload = "Message=" + message + "\nException: " + ex;
                EnqueueClient(client, CatNetError, detail, payload);
            }
            catch { }
        }

        public static void Log(Character c, string category, string detail, string payload = null)
        {
            try { Enqueue(c, category ?? CatGeneric, detail ?? "", payload); }
            catch { }
        }

        private static void Enqueue(Character c, string category, string detail, string payload)
        {
            string dedupeKey = (c?.Id.ToString() ?? "?") + "|" + category + "|" + detail;
            var now = DateTime.UtcNow;
            if (_recentKeys.TryGetValue(dedupeKey, out var lastSeen) && (now - lastSeen) < DedupeWindow)
                return;
            _recentKeys[dedupeKey] = now;
            if (_recentKeys.Count > 8192)
            {
                foreach (var kv in _recentKeys)
                    if ((now - kv.Value) > DedupeWindow)
                        _recentKeys.TryRemove(kv.Key, out _);
            }

            if (_queue.Count >= MaxQueue) { Interlocked.Increment(ref _droppedCount); return; }

            _queue.Enqueue(new Entry
            {
                AtUtc         = now,
                CharacterId   = c?.Id,
                CharacterName = SafeTrunc(c?.Name, 64),
                Category      = SafeTrunc(category, 48) ?? "generic",
                Detail        = SafeTrunc(detail, 255) ?? "",
                PayloadJson   = SafeTrunc(payload, 60000),
            });
        }

        private static void EnqueueClient(WorldClient client, string category, string detail, string payload)
        {
            Character c = null;
            try { c = client?.Character; } catch { }
            Enqueue(c, category, detail, payload);
        }

        private static async Task FlushLoopAsync()
        {
            try { PurgeOld(); } catch { }

            int sincePurge = 0;
            while (true)
            {
                try { Flush(); }
                catch (Exception e) { Logger.Write("[OneAir/UnhandledLogger] flush failed: " + e.Message, Channels.Warning); }

                sincePurge += FlushIntervalMs;
                if (sincePurge > 60 * 60 * 1000)
                {
                    sincePurge = 0;
                    try { PurgeOld(); } catch { }
                }

                await Task.Delay(FlushIntervalMs);
            }
        }

        private static void Flush()
        {
            if (_queue.IsEmpty) return;
            var batch = new List<Entry>(BatchSize);
            while (batch.Count < BatchSize && _queue.TryDequeue(out var e)) batch.Add(e);
            if (batch.Count == 0) return;

            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO unhandled_log " +
                    "(AtUtc, CharacterId, CharacterName, Category, Detail, PayloadJson) " +
                    "VALUES (@at, @cid, @cname, @cat, @det, @pl)";
                var pAt = cmd.Parameters.Add("@at", MySqlDbType.DateTime);
                var pCid = cmd.Parameters.Add("@cid", MySqlDbType.Int64);
                var pCName = cmd.Parameters.Add("@cname", MySqlDbType.VarChar);
                var pCat = cmd.Parameters.Add("@cat", MySqlDbType.VarChar);
                var pDet = cmd.Parameters.Add("@det", MySqlDbType.VarChar);
                var pPl = cmd.Parameters.Add("@pl", MySqlDbType.MediumText);

                foreach (var e in batch)
                {
                    pAt.Value = e.AtUtc;
                    pCid.Value = (object)e.CharacterId ?? DBNull.Value;
                    pCName.Value = (object)e.CharacterName ?? DBNull.Value;
                    pCat.Value = e.Category;
                    pDet.Value = e.Detail;
                    pPl.Value = (object)e.PayloadJson ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();

            if (_droppedCount > 0)
            {
                long dropped = Interlocked.Exchange(ref _droppedCount, 0);
                Logger.Write("[OneAir/UnhandledLogger] queue saturée — " + dropped + " entrées droppées", Channels.Warning);
            }
        }

        private static void PurgeOld()
        {
            using var conn = OpenConnection();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM unhandled_log WHERE AtUtc < (UTC_TIMESTAMP() - INTERVAL @d DAY)";
                cmd.Parameters.AddWithValue("@d", RetentionDays);
                cmd.ExecuteNonQuery();
            }
            // Cap par catégorie : garde les N plus récents par cat.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
DELETE l FROM unhandled_log l
JOIN (
    SELECT Category, MIN(Id) AS min_id FROM (
        SELECT Id, Category,
               ROW_NUMBER() OVER (PARTITION BY Category ORDER BY Id DESC) AS rn
        FROM unhandled_log
    ) t WHERE rn = @cap GROUP BY Category
) keep ON l.Category = keep.Category AND l.Id < keep.min_id";
                cmd.Parameters.AddWithValue("@cap", MaxRowsPerCategory);
                cmd.ExecuteNonQuery();
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

        private static string SafeTrunc(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static string ShortType(Exception ex)
        {
            if (ex == null) return "null";
            var t = ex.GetType().Name;
            return t + ": " + (ex.Message ?? "");
        }

        private static string ItemPayload(CharacterItemRecord item)
        {
            if (item == null) return null;
            var sb = new StringBuilder();
            sb.Append("Item GId=").Append(item.GId).Append('\n');
            sb.Append("UId=").Append(item.UId).Append('\n');
            sb.Append("Quantity=").Append(item.Quantity).Append('\n');
            sb.Append("Position=").Append(item.PositionEnum).Append('\n');
            if (item.Record != null)
            {
                sb.Append("Name=").Append(item.Record.Name).Append('\n');
                sb.Append("Type=").Append(item.Record.TypeEnum).Append('\n');
                sb.Append("Level=").Append(item.Record.Level).Append('\n');
                if (item.Record.Effects != null)
                {
                    sb.Append("BaseEffects=");
                    foreach (var e in item.Record.Effects)
                        sb.Append(e?.EffectEnum).Append(',');
                    sb.Append('\n');
                }
            }
            if (item.Effects != null)
            {
                sb.Append("ItemEffects=");
                foreach (var e in item.Effects.OfType<Effect>())
                    sb.Append(e.EffectEnum).Append(',');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string EffectPayload(Effect e)
        {
            if (e == null) return null;
            return "Effect " + e.GetType().Name + " EffectEnum=" + e.EffectEnum + " details=" + e;
        }
    }
}
