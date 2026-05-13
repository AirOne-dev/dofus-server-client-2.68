// Handlers d'utilisation d'items qui manquent côté vanilla Giny.
//
// ItemsManager.Initialize scanne l'assembly pour les méthodes annotées
// [ItemUsageHandler] : pas de patch ItemsManager nécessaire pour les ajouter.
//
// Dispatch dans ItemsManager.UseItem :
//   1) par GId → (Character, CharacterItemRecord)
//   2) par ItemType → (Character, CharacterItemRecord)
//   3) par EffectEnum (1er effet de l'item) → (Character, EffectInteger)
//
// Vanilla retournait dès le 1er effet matché : un pain Pain Gouin
// (AddHealth + AddPermanentVitality) soignait les PV mais perdait le bonus
// vita perma. DispatchEffects ci-dessous boucle sur tous les effets et
// accumule ; ItemsManager.UseItem est trampoliné dessus via sed (Dockerfile).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Giny.Core;
using Giny.Protocol.Enums;
using Giny.World.Managers.Effects;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Records.Items;

namespace Giny.World.Managers.Items
{
    static class OneAirItemUses
    {
        // Pain à PV (Pain d'Incarnam, Carasau, Pain de Seigle…).
        [ItemUsageHandler(EffectsEnum.Effect_AddHealth)]
        public static bool EatHealItem(Character character, EffectInteger effect)
        {
            if (character == null || character.IsDead() || character.Fighting)
                return false;

            int missing = character.Stats.MaxLifePoints - character.Stats.LifePoints;
            if (missing <= 0) return false;

            int gain = effect.Value < missing ? effect.Value : missing;
            character.Stats.Life.Current = character.Stats.LifePoints + gain;
            return true;
        }

        // Pain à énergie (Michette, Fougasse, Mantou, Borodinski…).
        // Max énergie = level × 100 ; perdue à la mort au phénix.
        [ItemUsageHandler(EffectsEnum.Effect_RestoreEnergyPoints)]
        public static bool EatEnergyItem(Character character, EffectInteger effect)
        {
            if (character == null || character.IsDead() || character.Fighting)
                return false;

            int max = character.Stats.MaxEnergyPoints;
            int cur = character.Stats.Energy;
            int missing = max - cur;
            if (missing <= 0) return false;

            int gain = effect.Value < missing ? effect.Value : missing;
            character.Stats.Energy = (short)(cur + gain);
            return true;
        }

        // Trampoline appelé depuis ItemsManager.UseItem : boucle sur tous les
        // effets de l'item, invoque chaque handler enregistré et accumule.
        // Renvoie true si au moins un handler a renvoyé true → caller consomme
        // 1 unité du stack. Si AUCUN effet n'a de handler, on log via
        // OneAirUnhandledLogger (categorie item_use, "no_effect_handler") et
        // on renvoie false (pas de consommation, warning client).
        public static bool DispatchEffects(
            Character character,
            CharacterItemRecord item,
            Dictionary<ItemUsageHandlerAttribute, MethodInfo> handlers)
        {
            bool anyHandled = false;
            bool anyMissing = false;

            foreach (var effect in item.Effects.OfType<Effect>())
            {
                var f = handlers.FirstOrDefault(x => x.Key.Effect == effect.EffectEnum);
                if (f.Value != null)
                {
                    try
                    {
                        if ((bool)f.Value.Invoke(null, new object[] { character, effect }))
                            anyHandled = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Write("Item usage (" + item.Record.Id + ") : " + ex, Channels.Warning);
                        Giny.World.Managers.Web.OneAirUnhandledLogger.LogItemUseError(character, item, ex);
                    }
                }
                else
                {
                    anyMissing = true;
                }
            }

            if (anyHandled) return true;

            if (anyMissing)
            {
                Giny.World.Managers.Web.OneAirUnhandledLogger.LogItemUse(character, item, "no_effect_handler");
                character.ReplyWarning("No method found to handle item usage");
            }
            return false;
        }
    }
}
