// Persiste les PV de fin de combat sur le character. Vanilla Giny clone
// Character.Stats dans FighterStats au début du combat et ne le répercute
// jamais en retour, donc le joueur a toujours full PV après combat.
// On garde 1 PV minimum pour les KO (pas de Phoenix : les mécaniques
// PlayerLifeStatus de Dofus ne sont pas implémentées côté Giny et
// provoquaient des soft-locks).
using System;
using Giny.Core;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Fights.Fighters;
using Giny.World.Records.Maps;

namespace Giny.World.Managers.Chat
{
    public static class OneAirDeathManager
    {
        public static void OnFightEnding(CharacterFighter fighter)
        {
            try
            {
                if (fighter == null) return;
                var character = fighter.Character;
                if (character == null) return;

                bool isDead = !fighter.Alive || fighter.Stats.LifePoints <= 0;

                int hpAfter = isDead ? 1 : Math.Max(1, fighter.Stats.LifePoints);
                int maxHp = character.Stats.MaxLifePoints;
                if (hpAfter > maxHp) hpAfter = maxHp;
                try { character.Stats.Life.Current = hpAfter; }
                catch (Exception e) { Logger.Write("[OneAir] persist HP failed: " + e.Message, Channels.Warning); }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] OneAirDeathManager.OnFightEnding failed: " + e.Message, Channels.Warning);
            }
        }

        // Si fightMapId appartient à un donjon, tp le character à l'entrée du
        // donjon (au zaap si présent, sinon spawn standard). Returns true si
        // teleport effectué — le caller doit alors SKIP le SpawnPoint() vanilla.
        public static bool TryRespawnAtDungeonEntrance(Character character, long fightMapId)
        {
            try
            {
                if (character == null || fightMapId <= 0) return false;
                var dungeon = DungeonRecord.GetDungeonByMapId(fightMapId);
                if (dungeon == null || dungeon.EntranceMapId <= 0) return false;
                var entrance = MapRecord.GetMap(dungeon.EntranceMapId);
                if (entrance == null) return false;

                if (entrance.HasZaap())
                    character.TeleportToZaap(entrance);
                else
                    character.Teleport(entrance);
                return true;
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] TryRespawnAtDungeonEntrance failed: " + e.Message, Channels.Warning);
                return false;
            }
        }
    }
}
