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
