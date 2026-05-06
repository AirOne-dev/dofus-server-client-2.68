// OneAir — hub donjons.
//
// Commande .dj : téléporte le joueur sur la map hub (224920584, partagée
// avec .shop) où 4 NPCs sont spawnés au boot, un par plage de niveau :
//   - Niveau 1-50    (template 23 - Charlotte)
//   - Niveau 50-100  (template 37 - Amine)
//   - Niveau 100-150 (template 38 - Rish Claymore)
//   - Niveau 150-200 (template 40 - Filgar Feel)
//
// Au clic sur l'un des NPCs (intercepté par TryHandleNpcAction, branché dans
// le sed Patch 18 du Dockerfile aux côtés du dispatcher havre-sac), le
// serveur envoie en chat la liste des donjons de cette plage avec leur ID
// et un suffixe ".djgo <id>" que le joueur peut taper pour se téléporter
// sur l'entrée du donjon.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Giny.Core;
using Giny.Core.IO.Configuration;
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
    public static class OneAirDungeons
    {
        public const long HubMapId = 224921608; // Plan Astral - Imaginarium (sub 1032), distinct du .shop (224920584)

        // Template du Gardien fallback spawné sur les maps d'entrée de donjons
        // pour lesquels on n'a pas trouvé de NPC matching par nom.
        public const short GardienTemplateId = 78; // Gardien du Kanojedo

        // Templates de Maîtres pour les 4 plages — distincts visuellement et
        // thématiques. Cellules alignées en ligne sur la map hub
        // (224921608) — confirmées walkables par l'admin.
        // (templateId, minLvl, maxLvl, label, defaultCell)
        public static readonly Tuple<short, short, short, string, short>[] NpcRanges = new[]
        {
            Tuple.Create((short)757,  (short)1,   (short)50,  "Donjons niveau 1-50",   (short)288),  // Maître Koalak
            Tuple.Create((short)2224, (short)51,  (short)100, "Donjons niveau 51-100", (short)302),  // Maître Nabur
            Tuple.Create((short)2471, (short)101, (short)150, "Donjons niveau 101-150",(short)317),  // Maître des arènes
            Tuple.Create((short)2655, (short)151, (short)200, "Donjons niveau 151-200",(short)331),  // Maître des zaaps
        };

        // Templates utilisés historiquement comme hub NPCs — on les nettoie
        // aux boots pour permettre le swap vers les nouveaux templates.
        private static readonly short[] LegacyHubTemplates = { 23, 37, 38, 40 };

        // MessageId réutilisé pour la question d'ouverture du dialog. Existe
        // dans NpcMessages.d2o (utilisé par d'autres NPCs vanilla).
        private const int HubDialogMessageId = 470;

        private static volatile bool _spawned;
        private static readonly object _lock = new object();

        public static void EnsureNpcs()
        {
            if (_spawned) return;
            lock (_lock)
            {
                if (_spawned) return;
                try
                {
                    // Cleanup : supprime les anciens NPCs donjons placés sur
                    // d'autres maps (rebases, changement de HubMapId, etc.)
                    CleanupStrayNpcs();

                    var map = MapRecord.GetMap(HubMapId);
                    if (map == null)
                    {
                        Logger.Write("[OneAir] Dungeon hub map " + HubMapId + " not found", Channels.Warning);
                        return;
                    }
                    int created = 0, moved = 0, skipped = 0;
                    foreach (var entry in NpcRanges)
                    {
                        short tpl = entry.Item1;
                        short defCell = entry.Item5;
                        var existingNpc = map.Instance.GetEntities<Npc>().FirstOrDefault(n => n.Template != null && n.Template.Id == tpl);
                        if (existingNpc != null)
                        {
                            // Relocalise/réoriente si cellule ou direction divergent.
                            bool needsRelocate = (existingNpc.SpawnRecord.CellId != defCell || existingNpc.SpawnRecord.Direction != DirectionsEnum.DIRECTION_SOUTH_WEST)
                                                 && map.IsCellWalkable(defCell);
                            if (needsRelocate)
                            {
                                try
                                {
                                    NpcsManager.Instance.MoveNpc(existingNpc.SpawnRecord.Id, (int)HubMapId, defCell, DirectionsEnum.DIRECTION_SOUTH_WEST);
                                    moved++;
                                }
                                catch (Exception e) { Logger.Write("[OneAir] dj npc " + tpl + " move fail: " + e.Message, Channels.Warning); }
                            }
                            else
                            {
                                skipped++;
                            }
                            continue;
                        }
                        try
                        {
                            short cell = map.IsCellWalkable(defCell) ? defCell : map.RandomWalkableCell().Id;
                            NpcsManager.Instance.AddNpc((int)HubMapId, cell, DirectionsEnum.DIRECTION_SOUTH_WEST, tpl);
                            created++;
                        }
                        catch (Exception e) { Logger.Write("[OneAir] dj npc " + tpl + " spawn fail: " + e.Message, Channels.Warning); }
                    }
                    _spawned = true;
                    Logger.Write($"[OneAir] Dungeon hub NPCs : {created} spawned, {moved} relocated, {skipped} present", Channels.Info);
                }
                catch (Exception e) { Logger.Write("[OneAir] OneAirDungeons.EnsureNpcs failed: " + e.Message, Channels.Warning); }
            }
        }

        /// <summary>
        /// Supprime via SQL :
        ///  - les NpcSpawnRecord du hub des templates LEGACY (23/37/38/40) à n'importe quel emplacement
        ///  - les NpcSpawnRecord des templates COURANTS sur des maps != hub
        /// </summary>
        private static void CleanupStrayNpcs()
        {
            try
            {
                var legacy = string.Join(",", LegacyHubTemplates.Select(x => x.ToString()));
                var current = string.Join(",", NpcRanges.Select(x => x.Item1.ToString()));
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"DELETE FROM npc_spawns WHERE TemplateId IN ({legacy}) OR (TemplateId IN ({current}) AND MapId != @hub)";
                cmd.Parameters.AddWithValue("@hub", HubMapId);
                int n = cmd.ExecuteNonQuery();
                if (n > 0) Logger.Write($"[OneAir] Cleaned up {n} stray/legacy dungeon NPC spawn(s)", Channels.Info);
            }
            catch (Exception e) { Logger.Write("[OneAir] CleanupStrayNpcs failed: " + e.Message, Channels.Warning); }
        }

        public static bool TryHandleNpcAction(Character character, double npcId, double npcMapId)
        {
            try
            {
                if (character?.Map == null) return false;
                long mapId = (long)npcMapId;

                // 1) Hub donjons : panneau custom (les dialogs natifs de Dofus
                //    sont per-template via d2o.dialogMessages, donc inutilisables
                //    pour ce qu'on veut faire — les replyIds ne s'affichent que
                //    pour des templates spécifiques. On envoie un payload
                //    __ONEAIR_DJ__ que le SWF intercepte et qui ouvre notre
                //    panneau cliquable (cohérent avec .ui/.itemui/.online).
                if (mapId == HubMapId && character.Map.Id == HubMapId)
                {
                    var npc = character.Map.Instance.GetEntity<Npc>((long)npcId);
                    if (npc != null && npc.Template != null)
                    {
                        foreach (var entry in NpcRanges)
                        {
                            if (npc.Template.Id == entry.Item1)
                            {
                                SendDungeonPanelPayload(character, entry.Item2, entry.Item3, entry.Item4);
                                return true;
                            }
                        }
                    }
                }

                // 2) Gardien d'entrée d'un donjon : tp dans la 1ère salle.
                //    On override vanilla pour TOUS les NPCs sur une map d'entrée
                //    SAUF si le NPC a déjà un npc_replies de Teleport vers la
                //    1ère salle (auquel cas vanilla gère un vrai dialog).
                var dungeon = DungeonRecord.GetDungeonRecords().FirstOrDefault(d => d.EntranceMapId == mapId);
                if (dungeon != null && character.Map.Id == mapId)
                {
                    var npc = character.Map.Instance.GetEntity<Npc>((long)npcId);
                    if (npc != null && npc.SpawnRecord != null)
                    {
                        long firstRoom = (dungeon.Rooms != null && dungeon.Rooms.Count > 0) ? dungeon.Rooms[0].MapId : 0;

                        bool vanillaCanTp = false;
                        if (firstRoom > 0)
                        {
                            try
                            {
                                vanillaCanTp = NpcReplyRecord.GetNpcReplies().Any(r =>
                                    r.NpcSpawnId == npc.SpawnRecord.Id
                                    && r.ActionIdentifier == GenericActionEnum.Teleport
                                    && long.TryParse(r.Param1, out long m) && m == firstRoom);
                            }
                            catch { }
                        }

                        if (!vanillaCanTp)
                        {
                            if (firstRoom > 0)
                            {
                                character.Teleport(firstRoom);
                                character.Reply("Bienvenue dans <b>" + dungeon.Name + "</b>.");
                            }
                            else
                            {
                                character.ReplyError("Aucune salle configurée pour ce donjon (" + dungeon.Id + ").");
                            }
                            return true;
                        }
                    }
                }
            }
            catch (Exception e) { Logger.Write("[OneAir] OneAirDungeons.TryHandleNpcAction failed: " + e.Message, Channels.Warning); }
            return false;
        }

        /// <summary>
        /// Nettoie les npc_actions/npc_replies qu'on aurait pu insérer dans
        /// une tentative précédente d'utiliser les dialogs natifs. Notre flow
        /// actuel utilise un panel SWF custom (TryHandleNpcAction → payload
        /// __ONEAIR_DJ__ intercepté côté client). Les rows DB pour les hub
        /// NPCs sont donc inutiles et empêcheraient l'interception.
        /// </summary>
        public static void CleanupHubDialogs()
        {
            try
            {
                var hub = MapRecord.GetMap(HubMapId);
                if (hub == null) return;
                var hubSpawnIds = hub.Instance.GetEntities<Npc>()
                    .Where(n => n.Template != null && NpcRanges.Any(r => r.Item1 == n.Template.Id))
                    .Select(n => n.SpawnRecord.Id).ToList();
                if (hubSpawnIds.Count == 0) return;

                using var c = OpenConn();
                var idsList = string.Join(",", hubSpawnIds);
                int n1 = 0, n2 = 0;
                using (var d = c.CreateCommand()) { d.CommandText = $"DELETE FROM npc_replies WHERE NpcSpawnId IN ({idsList})"; n1 = d.ExecuteNonQuery(); }
                using (var d = c.CreateCommand()) { d.CommandText = $"DELETE FROM npc_actions WHERE NpcSpawnId IN ({idsList})"; n2 = d.ExecuteNonQuery(); }
                if (n1 + n2 > 0)
                    Logger.Write($"[OneAir] Cleaned up {n1} hub replies + {n2} hub actions (legacy)", Channels.Info);
            }
            catch (Exception e) { Logger.Write("[OneAir] CleanupHubDialogs failed: " + e.Message, Channels.Warning); }
        }

        /// <summary>
        /// Pour chaque donjon ayant une EntranceMapId valide mais SANS aucun
        /// NPC déjà spawné, ajoute un Gardien (template 78). Idempotent :
        /// les boots suivants détectent le Gardien existant et skippent.
        /// </summary>
        public static void EnsureDungeonGardiens()
        {
            try
            {
                int created = 0, replaced = 0, skipped = 0, fallback = 0, failed = 0;
                foreach (var dungeon in DungeonRecord.GetDungeonRecords())
                {
                    if (dungeon.EntranceMapId == 0) continue;
                    var map = MapRecord.GetMap(dungeon.EntranceMapId);
                    if (map == null) continue;

                    // Détermine le template idéal pour ce donjon (matching nom).
                    short desired = ResolveGuardianTemplateId(dungeon.Name);
                    bool isFallback = desired == GardienTemplateId;

                    var existing = map.Instance.GetEntities<Npc>();
                    if (existing.Any(n => n.Template != null && n.Template.Id == desired))
                    {
                        skipped++;
                        continue;
                    }

                    // Si la map a un NPC vanilla DIFFERENT de notre fallback,
                    // on respecte (probablement le bon gardien officiel).
                    bool hasVanilla = existing.Any(n => n.Template != null && n.Template.Id != GardienTemplateId);
                    if (hasVanilla)
                    {
                        skipped++;
                        continue;
                    }

                    // Sinon, supprime l'ancien fallback (template 78) si présent
                    var stale = existing.FirstOrDefault(n => n.Template != null && n.Template.Id == GardienTemplateId);
                    if (stale != null)
                    {
                        try { NpcsManager.Instance.RemoveNpc(stale.SpawnRecord.Id); replaced++; }
                        catch (Exception e) { Logger.Write("[OneAir] remove stale gardien fail: " + e.Message, Channels.Warning); }
                    }

                    try
                    {
                        short cell = map.RandomWalkableCell().Id;
                        NpcsManager.Instance.AddNpc((int)dungeon.EntranceMapId, cell, DirectionsEnum.DIRECTION_SOUTH_WEST, desired);
                        created++;
                        if (isFallback) fallback++;
                    }
                    catch (Exception e) { failed++; Logger.Write($"[OneAir] gardien spawn dungeon {dungeon.Id} fail: {e.Message}", Channels.Warning); }
                }
                Logger.Write($"[OneAir] Dungeon Gardiens : {created} spawned ({fallback} via fallback), {replaced} replaced, {skipped} kept vanilla, {failed} failed", Channels.Info);
            }
            catch (Exception e) { Logger.Write("[OneAir] EnsureDungeonGardiens failed: " + e.Message, Channels.Warning); }
        }

        /// <summary>
        /// Cherche un templateId NPC matchant le nom du donjon. Ex :
        /// "Crypte de Kardorim" → cherche "Kardorim" → trouve l'NPC id=2936.
        /// Strip d'abord les préfixes type "Donjon de(s)", "Crypte de", etc.
        /// puis essaie le nom complet, puis chaque mot >3 chars.
        /// Fallback sur GardienTemplateId si rien ne matche.
        /// </summary>
        public static short ResolveGuardianTemplateId(string dungeonName)
        {
            if (string.IsNullOrEmpty(dungeonName)) return GardienTemplateId;
            try
            {
                using var c = OpenConn();
                foreach (var kw in ExtractKeywords(dungeonName))
                {
                    if (string.IsNullOrWhiteSpace(kw) || kw.Length < 4) continue;
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "SELECT Id FROM npcs WHERE Name LIKE @like ORDER BY LENGTH(Name) ASC LIMIT 1";
                    cmd.Parameters.AddWithValue("@like", "%" + kw + "%");
                    var r = cmd.ExecuteScalar();
                    if (r != null && r != DBNull.Value)
                    {
                        return Convert.ToInt16(r);
                    }
                }
            }
            catch (Exception e) { Logger.Write("[OneAir] ResolveGuardianTemplateId failed: " + e.Message, Channels.Warning); }
            return GardienTemplateId;
        }

        private static IEnumerable<string> ExtractKeywords(string name)
        {
            var stripped = name.Trim();
            string[] prefixes = {
                "Donjon des ", "Donjon du ", "Donjon de la ", "Donjon de l'",
                "Donjon de ", "Donjon ",
                "Crypte de ", "Crypte ",
                "Antre de la ", "Antre des ", "Antre du ", "Antre de ",
                "Repaire du ", "Repaire de la ", "Repaire de ",
                "Tanière du ", "Tanière de ",
                "Cache de ",
                "Caverne de la ", "Caverne du ", "Caverne d'", "Caverne de ",
                "Bateau du ", "Bateau de l'", "Bateau de ",
                "Refuge sylvestre", "Refuge ",
                "Akadémie des ", "Maison ",
                "Grange du ",
                "Château du ", "Château de la ", "Château ",
                "Nid du ", "Nid de ",
                "Grotte ",
                "Clos des ", "Village ",
                "Pitons Rocheux des ",
                "Laboratoire de ",
                "Cale de l'", "Cale de ", "Cale des ",
                "Cour du ", "Cour de ",
                "Chapiteau des ", "Chapiteau de ",
                "Cimetière des ",
                "Domaine ",
                "Théâtre de ",
                "Garde-manger du ",
                "Sousouricière du ",
                "Atelier du ",
                "Vallée de la ", "Vallée des ", "Vallée de ", "Vallée du ",
                "Volière de la ",
                "Ring du ",
                "Mégalithe de ",
                "Bambusaie de ",
                "Miausolée du ",
                "Goulet du ",
                "Bibliothèque du ",
                "Centre du Labyrinthe du ",
                "Serre du ",
                "Tofulailler ",
                "Fonderie des ",
                "Stade ",
                "Croquanterie",
                "Fabrique de ",
                "Potager d'",
                "Ch", // Échappe Château etc déjà couvert
            };
            foreach (var p in prefixes)
            {
                if (stripped.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    stripped = stripped.Substring(p.Length).Trim();
                    break;
                }
            }
            // Strip un suffixe "Royal" / "Royale" qui reste générique
            if (stripped.EndsWith(" Royal", StringComparison.OrdinalIgnoreCase) || stripped.EndsWith(" Royale", StringComparison.OrdinalIgnoreCase))
            {
                stripped = stripped.Substring(0, stripped.LastIndexOf(' ')).Trim();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Tente le nom complet réduit (ex "Wa Wabbit", "Bouftou Royal")
            if (stripped.Length > 3)
            {
                seen.Add(stripped);
                yield return stripped;
            }
            var words = stripped.Split(new[] { ' ', '\'', '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
            // Tente le dernier mot (souvent le plus distinctif: "Kardorim", "Bworks")
            for (int i = words.Length - 1; i >= 0; i--)
            {
                var w = words[i];
                if (w.Length > 3 && seen.Add(w))
                {
                    yield return w;
                }
            }
        }

        /// <summary>
        /// Envoie au client un payload structuré que le SWF intercepte (marker
        /// __ONEAIR_DJ__) pour ouvrir un panel cliquable. Format :
        ///   __ONEAIR_DJ__<label>|<id>:<name>:<lvl>|<id>:<name>:<lvl>|...
        /// Les `:` et `|` dans les noms de donjons sont remplacés pour ne pas
        /// casser le parsing côté SWF.
        /// </summary>
        public static void SendDungeonPanelPayload(Character character, short minLvl, short maxLvl, string label)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("__ONEAIR_DJ__").Append(label);
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Id, Name, OptimalPlayerLevel FROM dungeons WHERE OptimalPlayerLevel >= @min AND OptimalPlayerLevel <= @max ORDER BY OptimalPlayerLevel, Name";
                cmd.Parameters.AddWithValue("@min", minLvl);
                cmd.Parameters.AddWithValue("@max", maxLvl);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    string name = (r.IsDBNull(1) ? "?" : r.GetString(1)).Replace('|', ' ').Replace(':', ' ');
                    int lvl = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    sb.Append('|').Append(id).Append(':').Append(name).Append(':').Append(lvl);
                }
                character.Reply(sb.ToString());
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] SendDungeonPanelPayload failed: " + e.Message, Channels.Warning);
                character.ReplyError("Erreur listing donjons : " + e.Message);
            }
        }

        public static void TpToDungeon(Character character, long dungeonId)
        {
            try
            {
                long mapId = 0;
                string name = "?";
                using (var c = OpenConn())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT Name, EntranceMapId FROM dungeons WHERE Id = @id";
                    cmd.Parameters.AddWithValue("@id", dungeonId);
                    using var r = cmd.ExecuteReader();
                    if (!r.Read())
                    {
                        character.ReplyError("Donjon " + dungeonId + " inconnu.");
                        return;
                    }
                    name = r.IsDBNull(0) ? "?" : r.GetString(0);
                    mapId = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                }
                if (mapId == 0)
                {
                    character.ReplyError("Map d'entrée manquante pour le donjon " + dungeonId + ".");
                    return;
                }
                character.Teleport(mapId);
                character.Reply("Téléporté à l'entrée de <b>" + name + "</b>.");
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] TpToDungeon failed: " + e.Message, Channels.Warning);
                character.ReplyError("Erreur tp donjon : " + e.Message);
            }
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
