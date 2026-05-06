// OneAir — gestion de la mort d'un personnage à la fin d'un combat.
//
// Vanilla Giny ne fait aucun follow-up : à la fin du combat, le PV/MP/AP
// retournent à leur valeur pré-combat (FighterStats est un clone éphémère
// de Character.Stats, jamais répercuté en arrière). Résultat : le joueur
// a toujours full PV après n'importe quel combat. On corrige uniquement
// ce point — on persiste les PV de fin de combat sur le character (1 PV
// minimum pour les KO). Pas de perte d'énergie, pas de tp Phoenix, pas
// de transformation visuelle : ces mécaniques étaient câblées sur des
// systèmes Dofus (PlayerLifeStatus, Phoenix actifs) que Giny ne supporte
// pas correctement, et provoquaient des soft-locks.
//
// Hook installé via sed sur CharacterFighter.OnFightEnding (Patch 22) qui
// appelle OneAirDeathManager.OnFightEnding(this).

using System;
using Giny.Core;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Fights.Fighters;

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
    }
}
