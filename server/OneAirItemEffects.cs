// OneAir — handlers d'effets d'items que Giny vanilla n'avait pas.
//
// Le scan d'attributs [ItemEffect] dans ItemEffectsManager.Initialize parcourt
// toutes les classes de l'assembly et enregistre les méthodes annotées —
// donc il suffit de poser ces handlers ici pour qu'ils soient pris en compte
// au boot, sans patcher ItemEffects.cs.
//
// Ces effets sont apparus dans le panel "Actions non gérées" et n'avaient
// aucune logique côté serveur, ce qui faisait que les items concernés
// (cosmétiques, dofus, items à pods) ne fonctionnaient pas correctement.

using Giny.Protocol.Custom.Enums;
using Giny.Protocol.Enums;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Stats;

namespace Giny.World.Managers.Items
{
    static class OneAirItemEffects
    {
        // Pods (capacité d'inventaire). CharacteristicEnum.WEIGHT est utilisé
        // par StatsFormulas.GetPodsMax (1000 + Force*5 + WEIGHT). Les ceintures
        // de mule, dofus pourpre, etc. portent cet effet (Effect_IncreaseWeight = 158).
        [ItemEffect(EffectsEnum.Effect_IncreaseWeight)]
        public static void IncreaseWeight(Character character, int delta)
        {
            character.Record.Stats
                .GetCharacteristic<DetailedCharacteristic>(CharacteristicEnum.WEIGHT)
                .Objects += (short)delta;
        }

        // Apparence_Wrapper (1176) : référence à un item d'apparence (skin /
        // wrap d'équipement). La logique d'application réelle est gérée par
        // Inventory.cs (cf. WrapItem/UnwrapItem), donc côté equip on s'en fiche.
        // Handler silencieux pour ne plus polluer "Actions non gérées".
        [ItemEffect(EffectsEnum.Effect_Apparence_Wrapper)]
        public static void ApparenceWrapper(Character character, int delta)
        {
            // no-op — purement cosmétique côté inventaire
        }

        // CastSpell_1175 (1175) : trigger de sort intégré à un item (passive
        // qui caste en combat). Hors fight (équip / déséquip) il n'y a rien
        // à faire. La gestion in-fight passe par les SpellEffectHandler.
        [ItemEffect(EffectsEnum.Effect_CastSpell_1175)]
        public static void CastSpell1175(Character character, int delta)
        {
            // no-op — déclenché uniquement en combat via SpellEffectHandler
        }
    }
}
