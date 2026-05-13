using Giny.Core;
using Giny.Core.DesignPattern;
using Giny.Core.IO.Configuration;
using Giny.Core.Pool;
using Giny.ORM;
using Giny.Protocol.Messages;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Guilds;
using Giny.World.Network;
using Giny.World.Records.Alliances;
using Giny.World.Records.Characters;
using Giny.World.Records.Guilds;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Giny.World.Managers.Alliances
{
    // OneAir : socle alliances. Une alliance contient des guildes (cf. Alliance.cs).
    // L'init suit le pattern de GuildsManager (StartupInvoke + UniqueIdProvider).
    public class OneAirAllianceManager : Singleton<OneAirAllianceManager>
    {
        public const int MaxGuildsPerAlliance = 30;
        public const int MotdMaxLength = 255;
        public const byte CREATION_OK = 1;
        public const byte CREATION_ERR_NAME_INVALID = 2;
        public const byte CREATION_ERR_TAG_INVALID = 3;
        public const byte CREATION_ERR_ALREADY_IN_ALLIANCE = 4;
        public const byte CREATION_ERR_NAME_EXISTS = 5;
        public const byte CREATION_ERR_TAG_EXISTS = 6;
        public const byte CREATION_ERR_EMBLEM_EXISTS = 7;
        public const byte CREATION_ERR_NO_GUILD = 8;
        public const byte CREATION_ERR_NOT_GUILD_BOSS = 9;

        private readonly ConcurrentDictionary<long, Alliance> Alliances = new ConcurrentDictionary<long, Alliance>();
        private UniqueIdProvider UniqueIdProvider;

        // À jouer avant le LoadTables ORM (SecondPass), sinon le SELECT *
        // FROM guilds crasherait si la colonne AllianceId manquait sur un
        // serveur déjà déployé. On crée aussi les tables friends_book et
        // alliances, le DatabaseManager Giny ne fait pas de CREATE TABLE
        // automatique au boot (il demande interactivement un rebuild).
        [StartupInvoke("OneAir Alliances Schema Migration", StartupInvokePriority.Initial)]
        public static void EnsureSchema()
        {
            // 1. Migration ALTER TABLE guilds. Inclut les colonnes que le ORM
            //    Giny attend mais qui n'ont jamais été créées en DB (Ranks,
            //    Bulletin, GlobalActivities, ChestActivities → ajoutées au
            //    code source upstream sans migration).
            AddColumnIfMissing("guilds", "Ranks", "BLOB NULL");
            AddColumnIfMissing("guilds", "Bulletin", "BLOB NULL");
            AddColumnIfMissing("guilds", "GlobalActivities", "BLOB NULL");
            AddColumnIfMissing("guilds", "ChestActivities", "BLOB NULL");
            AddColumnIfMissing("guilds", "AllianceId", "BIGINT NOT NULL DEFAULT 0");
            AddColumnIfMissing("guilds", "Recruitment", "BLOB NULL");

            // 2. Tables custom OneAir. Schéma aligné sur ce que le DatabaseManager
            //    aurait généré pour OneAirFriendsBookRecord / AllianceRecord.
            try
            {
                using var c = OpenConnection();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS friends_book (
    AccountId INT NOT NULL PRIMARY KEY,
    FriendAccountIds BLOB NULL,
    IgnoredAccountIds BLOB NULL,
    WarnOnConnection TINYINT NOT NULL DEFAULT 0,
    WarnOnLevelGain TINYINT NOT NULL DEFAULT 0,
    StatusShared TINYINT NOT NULL DEFAULT 1
) ENGINE=InnoDB";
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir/Alliance] friends_book create failed: " + e.Message, Channels.Warning);
            }

            try
            {
                using var c = OpenConnection();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS alliances (
    Id BIGINT NOT NULL PRIMARY KEY,
    Name VARCHAR(255) NULL,
    Tag VARCHAR(255) NULL,
    Emblem BLOB NULL,
    CreationDate MEDIUMTEXT NULL,
    Motd BLOB NULL,
    Bulletin MEDIUMTEXT NULL,
    LeaderCharacterId BIGINT NOT NULL DEFAULT 0,
    Guilds BLOB NULL
) ENGINE=InnoDB";
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir/Alliance] alliances create failed: " + e.Message, Channels.Warning);
            }
        }

        [StartupInvoke("OneAir Alliances", StartupInvokePriority.SixthPath)]
        public void Initialize()
        {
            foreach (var record in AllianceRecord.GetAlliances())
                Alliances.TryAdd(record.Id, new Alliance(record));

            int lastId = Alliances.Count > 0 ? (int)Alliances.Keys.Max() : 0;
            UniqueIdProvider = new UniqueIdProvider(lastId);
        }

        // -----------------------------------------------------------------
        // Lifecycle / lookup
        // -----------------------------------------------------------------

        public Alliance GetAlliance(long id) => Alliances.TryGetValue(id, out var a) ? a : null;
        public IEnumerable<Alliance> GetAlliances() => Alliances.Values;

        public Alliance GetAllianceByGuildId(long guildId)
        {
            foreach (var a in Alliances.Values)
                if (a.IsGuildInAlliance(guildId)) return a;
            return null;
        }

        // Wrap WorldServer.GetOnlineClients to map vers Character pour une guilde.
        public IEnumerable<Character> GetOnlineCharactersOfGuild(Guild guild)
        {
            if (guild == null) yield break;
            foreach (var member in guild.Record.Members)
            {
                var character = guild.GetOnlineMember(member.CharacterId);
                if (character != null) yield return character;
            }
        }

        // -----------------------------------------------------------------
        // Creation / destruction
        // -----------------------------------------------------------------

        public byte CreateAlliance(Character founder, string name, string tag, AllianceEmblemRecord emblem)
        {
            if (!founder.HasGuild)
                return CREATION_ERR_NO_GUILD;

            if (!IsGuildBoss(founder))
                return CREATION_ERR_NOT_GUILD_BOSS;

            if (founder.Guild.Record.AllianceId != 0)
                return CREATION_ERR_ALREADY_IN_ALLIANCE;

            if (string.IsNullOrWhiteSpace(name) || name.Length < 3 || name.Length > 30)
                return CREATION_ERR_NAME_INVALID;

            if (string.IsNullOrWhiteSpace(tag) || tag.Length < 2 || tag.Length > 5)
                return CREATION_ERR_TAG_INVALID;

            if (AllianceRecord.NameExists(name)) return CREATION_ERR_NAME_EXISTS;
            if (AllianceRecord.TagExists(tag)) return CREATION_ERR_TAG_EXISTS;
            if (AllianceRecord.EmblemExists(emblem)) return CREATION_ERR_EMBLEM_EXISTS;

            var record = new AllianceRecord
            {
                Id = UniqueIdProvider.Pop(),
                Name = name,
                Tag = tag,
                Emblem = emblem,
                CreationDate = DateTime.Now,
                Motd = new AllianceMotdRecord(),
                Bulletin = "",
                LeaderCharacterId = founder.Id,
                Guilds = new List<AllianceGuildLinkRecord>(),
            };
            record.AddLater();

            var alliance = new Alliance(record);
            Alliances.TryAdd(record.Id, alliance);

            // La guilde fondatrice rejoint immédiatement.
            alliance.AddGuild(founder.Guild, founder: true);
            return CREATION_OK;
        }

        public void RemoveAlliance(Alliance alliance)
        {
            alliance.Record.RemoveLater();
            Alliances.TryRemove(alliance.Id, out _);
        }

        // -----------------------------------------------------------------
        // Hooks
        // -----------------------------------------------------------------

        public void OnCharacterConnected(Character character)
        {
            if (!character.HasGuild) return;
            var alliance = GetAllianceByGuildId(character.Guild.Id);
            if (alliance == null) return;

            character.Alliance = alliance;
            alliance.OnCharacterConnected(character);
        }

        public void OnCharacterDisconnected(Character character)
        {
            if (character.Alliance != null)
            {
                character.Alliance.OnCharacterDisconnected(character);
                character.Alliance = null;
            }
        }

        // Appelé quand une guilde est supprimée (plus de membres) ; elle
        // quitte automatiquement son alliance.
        public void OnGuildDestroyed(Guild guild)
        {
            if (guild.Record.AllianceId == 0) return;
            var alliance = GetAlliance(guild.Record.AllianceId);
            alliance?.RemoveGuild(guild, kicked: false);
        }

        // Appelé quand un personnage rejoint une guilde déjà en alliance :
        // on lui envoie son AllianceMembership et on l'enregistre online.
        public void OnCharacterJoinedGuild(Character character)
        {
            if (!character.HasGuild) return;
            var alliance = GetAllianceByGuildId(character.Guild.Id);
            if (alliance == null) return;

            character.Alliance = alliance;
            alliance.OnCharacterConnected(character);
            alliance.BroadcastMembersUpdate();
        }

        // Appelé quand un personnage quitte sa guilde : on retire son online status alliance.
        public void OnCharacterLeftGuild(Character character)
        {
            if (character.Alliance != null)
            {
                var alliance = character.Alliance;
                alliance.OnCharacterDisconnected(character);
                character.OnAllianceKick(alliance);
                alliance.BroadcastMembersUpdate();
            }
        }

        public static bool IsGuildBoss(Character character)
        {
            return character.HasGuild && character.GuildMember != null && character.GuildMember.Rank == Guild.BOSS_RANK_ID;
        }

        // -----------------------------------------------------------------
        // Summary / list
        // -----------------------------------------------------------------

        public AllianceSummaryMessage BuildSummaryMessage()
        {
            var sheets = Alliances.Values.Select(a => a.GetAllianceFactSheet()).ToArray();
            return new AllianceSummaryMessage(sheets, 0, (uint)sheets.Length, (uint)sheets.Length);
        }

        public AllianceListMessage BuildAllianceListMessage()
        {
            var sheets = Alliances.Values.Select(a => a.GetAllianceFactSheet()).ToArray();
            return new AllianceListMessage(sheets);
        }

        // -----------------------------------------------------------------
        // Misc
        // -----------------------------------------------------------------

        private static void AddColumnIfMissing(string table, string column, string sqlType)
        {
            try
            {
                using var c = OpenConnection();
                bool hasCol;
                using (var check = c.CreateCommand())
                {
                    check.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
                                        "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c";
                    check.Parameters.AddWithValue("@t", table);
                    check.Parameters.AddWithValue("@c", column);
                    hasCol = System.Convert.ToInt32(check.ExecuteScalar()) > 0;
                }
                if (hasCol) return;
                using var alter = c.CreateCommand();
                alter.CommandText = $"ALTER TABLE `{table}` ADD COLUMN `{column}` {sqlType}";
                alter.ExecuteNonQuery();
                Logger.Write($"[OneAir/Alliance] {table}.{column} column added", Channels.Info);
            }
            catch (Exception e)
            {
                Logger.Write($"[OneAir/Alliance] {table}.{column} migration failed: {e.Message}", Channels.Warning);
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
    }
}
