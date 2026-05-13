// Respawn d'un character défait à l'entrée du donjon où le combat s'est
// déroulé, plutôt qu'au phoenix vanilla (les mécaniques PlayerLifeStatus de
// Dofus ne sont pas implémentées dans Giny et provoquent des soft-locks).
//
// La progression du donjon (cf. OneAirDungeonResume) est conservée — le
// joueur peut donc cliquer le PNJ d'entrée et choisir "Reprendre".
using System;
using Giny.Core;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Records.Maps;

namespace Giny.World.Managers.Dungeons
{
    public static class OneAirDungeonRespawn
    {
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
