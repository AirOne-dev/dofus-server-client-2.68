// Reprise de donjon où le joueur l'a quitté.
//
// Quand un joueur entre dans une salle de donjon, on persiste sa progression
// (CharacterId, DungeonId, LastRoomMapId) dans `dungeon_progress`. Quand il
// re-clique sur le PNJ d'entrée, on lui ouvre le vrai dialog Dofus natif (via
// NpcDialogCreationMessage + NpcDialogQuestionMessage) avec les replyIds
// vanilla pour ce PNJ : "Entrer", "Reprendre <nom> où vous l'avez quittée."
// et "Sortir". Les replyIds sont whitelistés côté client par template NPC ;
// la table est extraite offline depuis les d2o du client (voir
// OneAirDungeonResumeData.cs et tools/build_dungeon_dialog_table.py).
//
// La progression est purgée :
//   * quand le joueur entre sur la map de sortie du donjon (ExitMapId)
//   * quand le joueur réussit le dernier combat (nettoyage explicite)
//   * quand le joueur quitte la zone donjon sans passer par la sortie
//     (déco / recall / autre TP hors donjon → on garde la progression).
//
// La progression EST conservée :
//   * quand le joueur est défait dans le donjon et téléporté à l'entrée
//     (cf. OneAirDeathManager.TryRespawnAtDungeonEntrance), pour qu'il puisse
//     reprendre depuis la salle où il était.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Giny.Core;
using Giny.Core.IO.Configuration;
using Giny.ORM;
using Giny.Protocol.Custom.Enums;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Entities.Npcs;
using Giny.World.Managers.Generic;
using Giny.World.Managers.Maps.Npcs;
using Giny.World.Records.Maps;
using Giny.World.Records.Npcs;
using MySql.Data.MySqlClient;

namespace Giny.World.Managers.Chat
{
    public static class OneAirDungeonResume
    {
        private static volatile bool _schemaReady = false;
        private static readonly object _schemaLock = new object();

        // In-memory cache: characterId → (dungeonId → lastRoomMapId).
        // Hydraté à la demande depuis MySQL ; les writes traversent toujours la BDD.
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, long>> _progress = new();

        public static void EnsureSchema()
        {
            if (_schemaReady) return;
            lock (_schemaLock)
            {
                if (_schemaReady) return;
                try
                {
                    using var c = OpenConn();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS dungeon_progress (
    CharacterId BIGINT NOT NULL,
    DungeonId BIGINT NOT NULL,
    LastRoomMapId BIGINT NOT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    PRIMARY KEY (CharacterId, DungeonId)
) ENGINE=InnoDB;";
                    cmd.ExecuteNonQuery();
                    _schemaReady = true;
                    Logger.Write("[OneAir] DungeonResume schema ready", Channels.Info);
                }
                catch (Exception e)
                {
                    Logger.Write("[OneAir] DungeonResume schema init failed: " + e.Message, Channels.Warning);
                }
            }
        }

        // Overrides manuels — donjons que la regex de matching auto rate à cause
        // d'un phrasing exotique dans l'i18n du client. Données extraites du même
        // d2o que la table auto-générée (cf. OneAirDungeonResumeData.cs), juste
        // câblées à la main ici. Si un dungeonId est présent à la fois dans
        // ByEntrance et ManualOverrides, l'override gagne.
        //
        // 2 donjons restent hors couverture car leur NPC d'entrée vanilla n'a
        // aucune dialogReplies dans son d2o (donc impossible d'envoyer un dialog
        // Dofus natif) :
        //   * id=24 Gelaxième Dimension (acces achievement-only)
        //   * id=31 Antre du Kralamoure Géant (Kokulte Géant n'a pas de dialog)
        private static readonly OneAirDungeonResumeEntry[] ManualOverrides = new[]
        {
            new OneAirDungeonResumeEntry { DungeonId = 4L,  DungeonName = "Centre du Labyrinthe du Minotoror", EntranceMapId = 34473220L,  ExitMapId = 34476294L,  NpcTemplateId = 783,  NpcName = "Lorkos",         QuestionMessageId = 3212,  EnterReplyId = 438125, ResumeReplyId = 331467, ExitReplyId = 331477 },
            new OneAirDungeonResumeEntry { DungeonId = 8L,  DungeonName = "Grange du Tournesol Affamé",        EntranceMapId = 192937992L, ExitMapId = 192937992L, NpcTemplateId = 780,  NpcName = "Mawy Ingalsse",  QuestionMessageId = 3178,  EnterReplyId = 386714, ResumeReplyId = 0,      ExitReplyId = 366337 },
            new OneAirDungeonResumeEntry { DungeonId = 25L, DungeonName = "Grotte Hesque",                     EntranceMapId = 161295L,    ExitMapId = 161295L,    NpcTemplateId = 941,  NpcName = "Tina Montini",   QuestionMessageId = 4190,  EnterReplyId = 378799, ResumeReplyId = 0,      ExitReplyId = 767068 },
            new OneAirDungeonResumeEntry { DungeonId = 39L, DungeonName = "Cale de l'Arche d'Otomaï",          EntranceMapId = 22546944L,  ExitMapId = 22546944L,  NpcTemplateId = 942,  NpcName = "Capitaine Flams",QuestionMessageId = 4195,  EnterReplyId = 28043,  ResumeReplyId = 29584,  ExitReplyId = 767079 },
            new OneAirDungeonResumeEntry { DungeonId = 42L, DungeonName = "Garde-manger du Rat Blanc",         EntranceMapId = 216402698L, ExitMapId = 218632194L, NpcTemplateId = 6612, NpcName = "Rat Blanc",      QuestionMessageId = 50024, EnterReplyId = 0,      ResumeReplyId = 0,      ExitReplyId = 925429 },
            new OneAirDungeonResumeEntry { DungeonId = 53L, DungeonName = "Salle du Minotot",                  EntranceMapId = 34473476L,  ExitMapId = 34476294L,  NpcTemplateId = 783,  NpcName = "Lorkos",         QuestionMessageId = 3213,  EnterReplyId = 438125, ResumeReplyId = 331468, ExitReplyId = 21321 },
            new OneAirDungeonResumeEntry { DungeonId = 58L, DungeonName = "Tanière Givrefoux",                 EntranceMapId = 59510784L,  ExitMapId = 60031488L,  NpcTemplateId = 1385, NpcName = "Givrihaltès",    QuestionMessageId = 8293,  EnterReplyId = 20456,  ResumeReplyId = 26974,  ExitReplyId = 20464 },
        };

        // Table merge (ByEntrance + ManualOverrides) calculée une fois.
        private static readonly Lazy<Dictionary<long, List<OneAirDungeonResumeEntry>>> _byEntranceMerged
            = new Lazy<Dictionary<long, List<OneAirDungeonResumeEntry>>>(BuildMerged);

        private static Dictionary<long, List<OneAirDungeonResumeEntry>> BuildMerged()
        {
            var manualDungeonIds = new HashSet<long>(ManualOverrides.Select(e => e.DungeonId));
            var merged = new Dictionary<long, List<OneAirDungeonResumeEntry>>();
            // 1. Auto, en sautant les dungeonIds couverts par un override (l'override gagne).
            foreach (var kv in OneAirDungeonResumeData.ByEntrance)
            {
                var filtered = kv.Value.Where(e => !manualDungeonIds.Contains(e.DungeonId)).ToList();
                if (filtered.Count > 0) merged[kv.Key] = filtered;
            }
            // 2. Manuels.
            foreach (var e in ManualOverrides)
            {
                if (!merged.TryGetValue(e.EntranceMapId, out var list))
                    merged[e.EntranceMapId] = list = new List<OneAirDungeonResumeEntry>();
                list.Add(e);
            }
            return merged;
        }

        public static List<OneAirDungeonResumeEntry> GetEntriesForMap(long mapId)
        {
            _byEntranceMerged.Value.TryGetValue(mapId, out var list);
            return list ?? new List<OneAirDungeonResumeEntry>();
        }

        // Sous-ensemble des entrées de la map qui sont gérées par un template
        // NPC particulier. Permet de regrouper les dialogues pour les NPCs qui
        // donnent accès à plusieurs donjons (ex: Bibiblop → Clos des Blops +
        // Antre du Blop Multicolore Royal).
        public static List<OneAirDungeonResumeEntry> GetEntriesForNpc(long mapId, short npcTemplateId)
        {
            return GetEntriesForMap(mapId).Where(e => e.NpcTemplateId == npcTemplateId).ToList();
        }

        // Hook appelé depuis NpcTalkDialog.DialogQuestion(). Retourne la liste des
        // replyId additionnels à afficher quand le joueur a une progression
        // sauvegardée pour un (ou plusieurs) donjon(s) accessible(s) depuis ce
        // NPC. Le client filtre par d2o.dialogReplies du template, donc le
        // replyId doit être valide pour ce NPC ; c'est garanti par construction
        // (on ne fournit que des replyIds extraits du d2o de ce template).
        public static List<int> GetExtraRepliesForNpcTalk(Character character, Npc npc)
        {
            var extras = new List<int>();
            try
            {
                if (character == null || npc?.SpawnRecord == null || npc.Template == null) return extras;
                long mapId = npc.SpawnRecord.MapId;
                foreach (var entry in GetEntriesForNpc(mapId, (short)npc.Template.Id))
                {
                    if (entry.ResumeReplyId <= 0) continue;
                    var saved = GetSavedRoom(character.Id, entry.DungeonId);
                    if (!saved.HasValue) continue;
                    var dungeon = DungeonRecord.GetDungeonRecords().FirstOrDefault(d => d.Id == entry.DungeonId);
                    long firstRoom = (dungeon?.Rooms != null && dungeon.Rooms.Count > 0) ? dungeon.Rooms[0].MapId : 0;
                    // Ignore si la "progression" est juste sur l'entrée ou la 1ère salle.
                    if (saved.Value == mapId || saved.Value == firstRoom) continue;
                    extras.Add(entry.ResumeReplyId);
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] GetExtraRepliesForNpcTalk failed: " + e.Message, Channels.Warning);
            }
            return extras;
        }

        // Hook appelé depuis NpcTalkDialog.Reply() AVANT le routage vanilla.
        // Retourne true si la reply correspond à un Reprendre <donjon> et qu'on
        // a téléporté le joueur ; le caller ferme alors le dialog.
        public static bool TryHandleExtraReply(Character character, Npc npc, int replyId)
        {
            try
            {
                if (character == null || npc?.SpawnRecord == null || npc.Template == null) return false;
                long mapId = npc.SpawnRecord.MapId;
                foreach (var entry in GetEntriesForNpc(mapId, (short)npc.Template.Id))
                {
                    if (replyId != entry.ResumeReplyId || entry.ResumeReplyId <= 0) continue;
                    var saved = GetSavedRoom(character.Id, entry.DungeonId);
                    if (!saved.HasValue) return false;
                    if (MapRecord.GetMap(saved.Value) == null) return false;
                    character.Teleport(saved.Value);
                    return true;
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] TryHandleExtraReply failed: " + e.Message, Channels.Warning);
            }
            return false;
        }

        // Hook appelé depuis Character.OnEnterMap. Sauvegarde la progression
        // dès qu'on entre dans une salle de donjon. On ne purge JAMAIS sur la
        // map de sortie : pour 89 donjons sur 124 (Crypte de Kardorim, Antre
        // du Dragon Cochon, etc.), entrance == exit, donc respawner à
        // l'entrée après une défaite déclencherait la purge et casserait la
        // reprise. La progression est de toute façon écrasée par le prochain
        // SaveProgress quand le joueur clique "Entrer" et arrive en salle 1.
        public static void OnEnterMap(Character character)
        {
            try
            {
                if (character == null || character.Map == null) return;
                long mapId = character.Map.Id;
                var dungeon = DungeonRecord.GetDungeonByMapId(mapId);
                if (dungeon == null) return;
                if (dungeon.EntranceMapId == mapId) return;
                if (dungeon.Rooms != null && dungeon.Rooms.Any(r => r.MapId == mapId))
                {
                    SaveProgress(character.Id, dungeon.Id, mapId);
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] DungeonResume.OnEnterMap failed: " + e.Message, Channels.Warning);
            }
        }

        public static long? GetSavedRoom(long characterId, long dungeonId)
        {
            if (_progress.TryGetValue(characterId, out var dict) && dict.TryGetValue(dungeonId, out var room))
                return room;
            // Pas en cache — relit depuis MySQL (fallback si le serveur a redémarré).
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT LastRoomMapId FROM dungeon_progress WHERE CharacterId=@c AND DungeonId=@d LIMIT 1";
                cmd.Parameters.AddWithValue("@c", characterId);
                cmd.Parameters.AddWithValue("@d", dungeonId);
                var r = cmd.ExecuteScalar();
                if (r == null || r == DBNull.Value) return null;
                long roomFromDb = Convert.ToInt64(r);
                var dictNew = _progress.GetOrAdd(characterId, _ => new ConcurrentDictionary<long, long>());
                dictNew[dungeonId] = roomFromDb;
                return roomFromDb;
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] GetSavedRoom failed: " + e.Message, Channels.Warning);
                return null;
            }
        }

        public static void SaveProgress(long characterId, long dungeonId, long roomMapId)
        {
            var dict = _progress.GetOrAdd(characterId, _ => new ConcurrentDictionary<long, long>());
            // Skip si déjà connue à cette valeur — évite un round-trip SQL par pas dans le donjon.
            if (dict.TryGetValue(dungeonId, out var existing) && existing == roomMapId) return;
            dict[dungeonId] = roomMapId;
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
INSERT INTO dungeon_progress (CharacterId, DungeonId, LastRoomMapId)
VALUES (@c, @d, @r)
ON DUPLICATE KEY UPDATE LastRoomMapId=@r, UpdatedAt=CURRENT_TIMESTAMP";
                cmd.Parameters.AddWithValue("@c", characterId);
                cmd.Parameters.AddWithValue("@d", dungeonId);
                cmd.Parameters.AddWithValue("@r", roomMapId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] SaveProgress failed: " + e.Message, Channels.Warning);
            }
        }

        public static void ClearProgress(long characterId, long dungeonId)
        {
            if (_progress.TryGetValue(characterId, out var dict))
            {
                dict.TryRemove(dungeonId, out _);
            }
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM dungeon_progress WHERE CharacterId=@c AND DungeonId=@d";
                cmd.Parameters.AddWithValue("@c", characterId);
                cmd.Parameters.AddWithValue("@d", dungeonId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] ClearProgress failed: " + e.Message, Channels.Warning);
            }
        }

        // Spawn les bons NPCs vanilla aux entrées de donjon couvertes par la
        // table de dialog. Pour chaque (entranceMapId, npcTemplateId) unique dans
        // la table, on vérifie que ce template est présent sur la map et le spawn
        // si absent. Les fallback Gardien (template 78) éventuellement déposés par
        // OneAirDungeons.EnsureDungeonGardiens sur ces maps sont removés en
        // premier (ils n'ont pas les bons replyIds dans leur d2o).
        public static void EnsureEntranceNpcs()
        {
            // Distinct (mapId, npcTpl) pairs : un seul NPC par template par map,
            // même si plusieurs donjons partagent la même paire (Bibiblop).
            var pairs = new HashSet<(long mapId, short tpl)>();
            foreach (var kv in _byEntranceMerged.Value)
                foreach (var e in kv.Value)
                    pairs.Add((kv.Key, e.NpcTemplateId));

            Logger.Write($"[OneAir] DungeonResume entrance NPCs : scanning ({pairs.Count} npc/map pairs, {_byEntranceMerged.Value.Count} maps)", Channels.Info);
            int created = 0, replaced = 0, kept = 0, failed = 0, skippedNoTemplate = 0;

            // Première passe : drop tout fallback Gardien (template 78) sur les
            // maps couvertes par la table. On ne fait ça qu'une fois par map.
            var mapsTouched = new HashSet<long>();
            foreach (var (mapId, _) in pairs)
            {
                if (!mapsTouched.Add(mapId)) continue;
                var map = MapRecord.GetMap(mapId);
                if (map == null) continue;
                foreach (var fallback in map.Instance.GetEntities<Npc>()
                             .Where(n => n.Template != null && n.Template.Id == OneAirDungeons.GardienTemplateId)
                             .ToList())
                {
                    try { NpcsManager.Instance.RemoveNpc(fallback.SpawnRecord.Id); replaced++; }
                    catch (Exception e) { Logger.Write("[OneAir] Resume fallback remove fail: " + e.Message, Channels.Warning); }
                }
            }

            // Seconde passe : spawn chaque template manquant.
            foreach (var (mapId, tpl) in pairs)
            {
                try
                {
                    var map = MapRecord.GetMap(mapId);
                    if (map == null) continue;
                    var existing = map.Instance.GetEntities<Npc>();
                    if (existing.Any(n => n.Template != null && n.Template.Id == tpl))
                    {
                        kept++;
                        continue;
                    }
                    if (NpcRecord.GetNpcRecord(tpl) == null) { skippedNoTemplate++; continue; }
                    short cell = map.RandomWalkableCell().Id;
                    NpcsManager.Instance.AddNpc((int)mapId, cell, DirectionsEnum.DIRECTION_SOUTH_WEST, tpl);
                    created++;
                }
                catch (Exception e)
                {
                    failed++;
                    Logger.Write($"[OneAir] Resume NPC spawn failed mapId={mapId} tpl={tpl}: {e.Message}", Channels.Warning);
                }
            }
            Logger.Write($"[OneAir] DungeonResume entrance NPCs : {created} spawned, {replaced} fallback removed, {kept} kept, {failed} failed, {skippedNoTemplate} skip-no-template", Channels.Info);
        }

        // Pour chaque NPC d'entrée qu'on a spawné mais qui n'a pas de TALK action
        // vanilla, seed npc_actions + npc_replies pour qu'il propose un dialog
        // fonctionnel (Donner la clef et entrer / Sortir). La reply "Reprendre"
        // est ajoutée dynamiquement par GetExtraRepliesForNpcTalk au moment du
        // dialog — pas seedée car conditionnelle à la progression. Idempotent :
        // skip si une TALK existe déjà sur ce spawn (les NPCs vanilla type
        // Rotabla, Mawy, etc. l'ont déjà dans le dump initial).
        public static void EnsureEntranceDialogs()
        {
            int seededActions = 0, seededReplies = 0, kept = 0, failed = 0;
            foreach (var kv in _byEntranceMerged.Value)
            {
                long mapId = kv.Key;
                var map = MapRecord.GetMap(mapId);
                if (map == null) continue;

                // Group entries by NPC template (un NPC peut handler plusieurs donjons).
                var byTemplate = kv.Value.GroupBy(e => e.NpcTemplateId);
                foreach (var grp in byTemplate)
                {
                    short tpl = grp.Key;
                    var npc = map.Instance.GetEntity<Npc>(n => n.Template != null && n.Template.Id == tpl);
                    if (npc?.SpawnRecord == null) continue;
                    var spawnId = npc.SpawnRecord.Id;

                    // Skip si TALK déjà présent (vanilla seed).
                    if (NpcActionRecord.GetNpcActions(spawnId).Any(a => a.Action == NpcActionsEnum.TALK))
                    {
                        kept++;
                        continue;
                    }

                    // Pick le questionMsgId de la 1ère entrée (toutes les entrées
                    // partagent forcément le même template + la même map, donc le
                    // même messageId convient pour le dialog d'entrée commun).
                    int msgId = grp.First().QuestionMessageId;
                    if (msgId <= 0) continue;

                    try
                    {
                        // Seed TALK action.
                        var action = new NpcActionRecord
                        {
                            Id = NpcActionRecord.PopNextId(),
                            NpcSpawnId = spawnId,
                            Action = NpcActionsEnum.TALK,
                            Param1 = msgId.ToString(),
                            Param2 = "",
                            Param3 = "",
                            Criteria = "",
                        };
                        action.AddNow();
                        npc.SpawnRecord.Actions.Add(action);
                        seededActions++;

                        // Seed enter + exit replies pour chaque donjon servi par ce NPC.
                        foreach (var entry in grp)
                        {
                            long firstRoom = 0;
                            var dungeon = DungeonRecord.GetDungeonRecords().FirstOrDefault(d => d.Id == entry.DungeonId);
                            if (dungeon?.Rooms != null && dungeon.Rooms.Count > 0) firstRoom = dungeon.Rooms[0].MapId;

                            if (entry.EnterReplyId > 0 && firstRoom > 0)
                            {
                                var firstRoomMap = MapRecord.GetMap(firstRoom);
                                short enterCell = firstRoomMap != null ? firstRoomMap.RandomWalkableCell().Id : (short)0;
                                var enterReply = new NpcReplyRecord
                                {
                                    Id = NpcReplyRecord.PopNextId(),
                                    ReplyId = entry.EnterReplyId,
                                    NpcSpawnId = spawnId,
                                    MessageId = msgId,
                                    ActionIdentifier = GenericActionEnum.Teleport,
                                    Param1 = firstRoom.ToString(),
                                    Param2 = enterCell.ToString(),
                                    Param3 = "",
                                    Criteria = "",
                                };
                                enterReply.AddNow();
                                seededReplies++;
                            }
                            if (entry.ExitReplyId > 0)
                            {
                                var exitReply = new NpcReplyRecord
                                {
                                    Id = NpcReplyRecord.PopNextId(),
                                    ReplyId = entry.ExitReplyId,
                                    NpcSpawnId = spawnId,
                                    MessageId = msgId,
                                    ActionIdentifier = GenericActionEnum.None,
                                    Param1 = "",
                                    Param2 = "",
                                    Param3 = "",
                                    Criteria = "",
                                };
                                exitReply.AddNow();
                                seededReplies++;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Logger.Write($"[OneAir] DungeonResume dialog seed failed mapId={mapId} tpl={tpl}: {e.Message}", Channels.Warning);
                    }
                }
            }
            Logger.Write($"[OneAir] DungeonResume entrance dialogs : {seededActions} TALK actions seeded, {seededReplies} replies seeded, {kept} kept (vanilla), {failed} failed", Channels.Info);
        }

        private static MySqlConnection OpenConn()
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
