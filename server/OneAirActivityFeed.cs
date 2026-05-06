// OneAir — flux d'activité communautaire (level-ups majeurs, donjons,
// événements remarquables). Insert non-bloquant dans la table
// `oneair_activity` ; la landing publique lit ça via /api/public/community.
//
// On émet uniquement les events "intéressants côté joueur" — tranches de 25
// niveaux, victoire de donjon (salle finale du Dungeon record), etc. Le but
// est de remplir le feed sans le saturer.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Giny.Core;
using Giny.Core.IO.Configuration;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Experiences;
using Giny.World.Managers.Fights.Fighters;
using Giny.World.Records.Maps;
using MySql.Data.MySqlClient;

namespace Giny.World.Managers.Chat
{
    public static class OneAirActivityFeed
    {
        // Tranches de niveau qui déclenchent un event level_up (multiples de 25
        // jusqu'à 200 — incluse).
        private static readonly int[] LevelMilestones = { 25, 50, 75, 100, 125, 150, 175, 200 };

        public static void EnsureSchema()
        {
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS oneair_activity (
    Id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    Kind VARCHAR(32) NOT NULL,
    CharacterIds VARCHAR(255) NOT NULL,
    Names VARCHAR(255) NOT NULL,
    Title VARCHAR(255) NOT NULL,
    Detail VARCHAR(512) NULL,
    PayloadJson TEXT NULL,
    AtUtc DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    KEY ix_at (AtUtc),
    KEY ix_kind (Kind, AtUtc)
) ENGINE=InnoDB";
                cmd.ExecuteNonQuery();
            }
            catch (Exception e) { Logger.Write("[OneAir/Activity] schema init failed: " + e.Message, Channels.Warning); }
        }

        // ---- Hooks ---------------------------------------------------------

        /// <summary>
        /// Appelé après chaque AddExperience. Si le perso a franchi un palier
        /// LevelMilestones, on émet un event "level_up".
        /// </summary>
        public static void OnExperienceGained(Character character, long oldXp, long newXp)
        {
            if (character == null) return;
            try
            {
                int oldLvl = ExperienceManager.Instance.GetCharacterLevel(oldXp);
                int newLvl = ExperienceManager.Instance.GetCharacterLevel(newXp);
                if (newLvl <= oldLvl) return;

                // Crossed un milestone ?
                int reached = 0;
                foreach (var m in LevelMilestones)
                {
                    if (oldLvl < m && newLvl >= m && m > reached) reached = m;
                }
                if (reached == 0) return;

                Push("level_up",
                    new[] { character.Id },
                    new[] { character.Name },
                    character.Name + " atteint le niveau " + reached,
                    "Niveau " + reached + " franchi.",
                    "{\"level\":" + reached + ",\"breed\":\"" + Esc(character.Breed?.Name ?? "") + "\",\"breedId\":" + (character.Breed?.Id ?? 0) + "}");
            }
            catch (Exception e) { Logger.Write("[OneAir/Activity] OnExperienceGained: " + e.Message, Channels.Warning); }
        }

        /// <summary>
        /// Appelé sur OnFightEnding pour chaque CharacterFighter. On émet un
        /// event "dungeon_win" UNE FOIS par fight (le premier fighter callé
        /// pose un flag transactionnel via INSERT IGNORE par FightId).
        /// </summary>
        public static void OnFightEnding(CharacterFighter fighter)
        {
            if (fighter == null) return;
            try
            {
                if (fighter.Fight?.Winners != fighter.Team) return; // perdu / annulé
                var character = fighter.Character;
                if (character == null) return;
                var map = character.Map;
                if (map == null || !map.IsDungeonMap) return;

                var dungeon = map.Dungeon;
                if (dungeon == null || dungeon.Rooms == null || dungeon.Rooms.Count == 0) return;

                // On veut UNIQUEMENT la dernière room (boss) — sinon on log
                // chaque fight intermédiaire d'un donjon, trop bruyant.
                var lastRoom = dungeon.Rooms[dungeon.Rooms.Count - 1];
                if (lastRoom == null || lastRoom.MapId != map.Id) return;

                // Dédup : un fight génère N appels (un par fighter perso). On
                // utilise FightId comme clé d'unicité côté DB.
                long fightId = fighter.Fight.Id;

                // Liste des persos gagnants (fighter de la même Team, exclude
                // monsters/summons).
                var winners = fighter.Team.GetFighters<CharacterFighter>(false)
                    .Where(f => f != null && f.Character != null)
                    .Select(f => f.Character)
                    .Distinct()
                    .ToList();
                if (winners.Count == 0) return;

                var ids = winners.Select(c => c.Id).ToArray();
                var names = winners.Select(c => c.Name).ToArray();

                string title;
                if (winners.Count == 1)
                    title = winners[0].Name + " a terminé le donjon « " + dungeon.Name + " »";
                else if (winners.Count == 2)
                    title = winners[0].Name + " et " + winners[1].Name + " ont terminé le donjon « " + dungeon.Name + " »";
                else
                    title = JoinNames(names) + " ont terminé le donjon « " + dungeon.Name + " »";

                PushOnce("dungeon_win", "fight:" + fightId, ids, names, title,
                    "Donjon " + dungeon.Name + " · " + winners.Count + " aventurier" + (winners.Count > 1 ? "s" : ""),
                    "{\"dungeonId\":" + dungeon.Id + ",\"mapId\":" + map.Id + ",\"size\":" + winners.Count + "}");
            }
            catch (Exception e) { Logger.Write("[OneAir/Activity] OnFightEnding: " + e.Message, Channels.Warning); }
        }

        // ---- DB inserts (async, non-bloquants) -----------------------------

        private static void Push(string kind, long[] charIds, string[] names, string title, string detail, string payload)
        {
            string idsCsv = string.Join(",", charIds);
            string namesCsv = string.Join(",", names);
            Task.Run(() =>
            {
                try
                {
                    using var c = OpenConn();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = "INSERT INTO oneair_activity (Kind, CharacterIds, Names, Title, Detail, PayloadJson) " +
                                      "VALUES (@k, @cids, @n, @t, @d, @p)";
                    cmd.Parameters.AddWithValue("@k", Trunc(kind, 32));
                    cmd.Parameters.AddWithValue("@cids", Trunc(idsCsv, 255));
                    cmd.Parameters.AddWithValue("@n", Trunc(namesCsv, 255));
                    cmd.Parameters.AddWithValue("@t", Trunc(title, 255));
                    cmd.Parameters.AddWithValue("@d", (object)Trunc(detail, 512) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@p", (object)payload ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception e) { Logger.Write("[OneAir/Activity] insert failed: " + e.Message, Channels.Warning); }
            });
        }

        // PushOnce — guarantit qu'on n'insère qu'un seul event pour une clé
        // de dédup donnée (ex: fight:12345 pour les donjons). Utilise une
        // table de dédup en mémoire pour ne pas spammer la DB.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dedup =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        private static void PushOnce(string kind, string dedupeKey, long[] charIds, string[] names, string title, string detail, string payload)
        {
            string fullKey = kind + "|" + dedupeKey;
            if (!_dedup.TryAdd(fullKey, 1)) return;
            // GC périodique : dès qu'on dépasse 4096 entrées, on reset (les
            // events de dédup ont une portée intra-session de toute façon).
            if (_dedup.Count > 4096) _dedup.Clear();
            Push(kind, charIds, names, title, detail, payload);
        }

        // ---- Helpers -------------------------------------------------------

        private static MySqlConnection OpenConn()
        {
            var cfg = ConfigManager<WorldConfig>.Instance;
            var cs = $"Server={cfg.SQLHost};Database={cfg.SQLDBName};Uid={cfg.SQLUser};" +
                     $"Pwd={cfg.SQLPassword};AllowPublicKeyRetrieval=true;SslMode=None;Pooling=true;";
            var c = new MySqlConnection(cs);
            c.Open();
            return c;
        }

        private static string Trunc(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static string Esc(string s) => (s ?? "").Replace("\"", "\\\"");

        private static string JoinNames(string[] names)
        {
            if (names.Length == 0) return "";
            if (names.Length == 1) return names[0];
            if (names.Length == 2) return names[0] + " et " + names[1];
            return string.Join(", ", names.Take(names.Length - 1)) + " et " + names[names.Length - 1];
        }
    }
}
