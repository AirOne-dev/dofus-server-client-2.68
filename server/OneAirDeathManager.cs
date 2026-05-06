// OneAir — gestion de la mort d'un personnage à la fin d'un combat.
//
// Vanilla Giny ne fait aucun follow-up : à la fin du combat, le PV/MP/AP
// retournent à leur valeur pré-combat (FighterStats est un clone éphémère
// de Character.Stats, jamais répercuté en arrière). Résultat : le joueur a
// toujours full PV après n'importe quel combat, et la mort n'a aucune
// conséquence — alors qu'on attend (style Dofus 2.x) :
//   * persistence des PV consommés pendant le combat
//   * sur défaite avec PV<=0 : perte d'énergie (1000 par mort)
//   * énergie > 0 : tp vers l'entrée du donjon courant (s'il y en a une)
//     ou le spawn point
//   * énergie <= 0 : passage en mode fantôme (look gravestone) + tp à un
//     Phoenix (le spawn par défaut sert de Phoenix V1)
//
// Hook installé via sed sur CharacterFighter.OnFightEnding (Patch 22) qui
// appelle OneAirDeathManager.OnFightEnding(this).

using System;
using System.Collections.Generic;
using Giny.Core;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Entities.Look;
using Giny.World.Managers.Fights;
using Giny.World.Managers.Fights.Fighters;
using Giny.World.Records.Maps;

namespace Giny.World.Managers.Chat
{
    public static class OneAirDeathManager
    {
        public const short DeathEnergyLoss = 1000;

        // Map de Phoenix par défaut (V1) : Astrub Imaginarium = SpawnMapId.
        // À terme, on peut router selon la zone de la map où le joueur est mort.
        public const long PhoenixMapId = 154010883;

        // Override du SpawnPoint() de RejoinMap pour les morts récentes.
        // Indexé par characterId, consommé une fois par GetDeathSpawnPoint.
        private static readonly Dictionary<long, long> _pendingDeathSpawn = new Dictionary<long, long>();
        private static readonly object _pendingLock = new object();

        public static long GetDeathSpawnPoint(Character character)
        {
            try
            {
                lock (_pendingLock)
                {
                    if (_pendingDeathSpawn.TryGetValue(character.Id, out var mapId))
                    {
                        _pendingDeathSpawn.Remove(character.Id);
                        return mapId;
                    }
                }
            }
            catch { }
            return character.Record.SpawnPointMapId;
        }

        public static void SetPendingDeathSpawn(long charId, long mapId)
        {
            lock (_pendingLock) { _pendingDeathSpawn[charId] = mapId; }
        }

        public static void OnFightEnding(CharacterFighter fighter)
        {
            try
            {
                if (fighter == null) return;
                var character = fighter.Character;
                if (character == null) return;

                bool isDead = !fighter.Alive || fighter.Stats.LifePoints <= 0;
                bool isLoser = fighter.Fight != null && fighter.Fight.Winners != fighter.Team;

                // 1) Persistance des PV : on copie les PV de fin de combat
                //    sur le character. Au minimum 1 PV pour les survivants.
                int hpAfter = isDead ? 1 : Math.Max(1, fighter.Stats.LifePoints);
                int maxHp = character.Stats.MaxLifePoints;
                if (hpAfter > maxHp) hpAfter = maxHp;
                try { character.Stats.Life.Current = hpAfter; }
                catch (Exception e) { Logger.Write("[OneAir] persist HP failed: " + e.Message, Channels.Warning); }

                // 2) Si défaite ET KO : applique les conséquences mort
                if (isDead && isLoser)
                {
                    HandleDeath(character);
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] OneAirDeathManager.OnFightEnding failed: " + e.Message, Channels.Warning);
            }
        }

        private static void HandleDeath(Character character)
        {
            try
            {
                // Perte d'énergie
                short newEnergy = (short)Math.Max(0, character.Stats.Energy - DeathEnergyLoss);
                character.Stats.Energy = newEnergy;

                bool isGhost = newEnergy <= 0;

                long destMap = ResolveDeathDestination(character, isGhost);

                if (isGhost)
                {
                    try
                    {
                        if (character.Breed != null)
                        {
                            character.Record.ContextualLook = EntityLookManager.Instance.CreateLookFromBones(character.Breed.GraveBonesId);
                        }
                        character.ReplyError("Tu n'as plus d'énergie. Tu deviens un fantôme.");
                    }
                    catch (Exception e) { Logger.Write("[OneAir] ghost look failed: " + e.Message, Channels.Warning); }
                }
                else
                {
                    character.ReplyWarning("Tu as été vaincu. Énergie restante : " + newEnergy + " / " + character.Stats.MaxEnergyPoints);
                }

                // Stocke le destination override : RejoinMap → SpawnPoint
                // (patché par sed) consultera GetDeathSpawnPoint et tp ici.
                SetPendingDeathSpawn(character.Id, destMap);

                // Push stats au client (énergie + PV).
                character.RefreshStats();
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] HandleDeath failed: " + e.Message, Channels.Warning);
            }
        }

        /// <summary>
        /// Map de réapparition après mort.
        ///   - Fantôme → toujours Phoenix.
        ///   - Mort en donjon (intérieur de Rooms) → entrée du donjon.
        ///   - Sinon → SpawnPointMapId du joueur, fallback sur Phoenix.
        /// </summary>
        private static long ResolveDeathDestination(Character character, bool isGhost)
        {
            if (isGhost) return PhoenixMapId;

            try
            {
                var dungeon = DungeonRecord.GetDungeonByMapId(character.Map?.Id ?? 0);
                if (dungeon != null && dungeon.EntranceMapId > 0)
                {
                    return dungeon.EntranceMapId;
                }
            }
            catch { }

            if (character.Record.SpawnPointMapId > 0) return character.Record.SpawnPointMapId;
            return PhoenixMapId;
        }
    }
}
