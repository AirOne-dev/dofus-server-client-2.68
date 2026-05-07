// ItemEffectsManager.Initialize scanne l'assembly et enregistre toute méthode
// annotée [ItemEffect] : pas de patch sur ItemEffects.cs nécessaire.
using Giny.Protocol.Custom.Enums;
using Giny.Protocol.Enums;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Stats;

namespace Giny.World.Managers.Items
{
    static class OneAirItemEffects
    {
        // CharacteristicEnum.WEIGHT est lu par StatsFormulas.GetPodsMax
        // (1000 + Force*5 + WEIGHT).
        [ItemEffect(EffectsEnum.Effect_IncreaseWeight)]
        public static void IncreaseWeight(Character character, int delta)
        {
            character.Record.Stats
                .GetCharacteristic<DetailedCharacteristic>(CharacteristicEnum.WEIGHT)
                .Objects += (short)delta;
        }

        // No-op : la logique d'apparence passe par Inventory.WrapItem/UnwrapItem.
        [ItemEffect(EffectsEnum.Effect_Apparence_Wrapper)]
        public static void ApparenceWrapper(Character character, int delta)
        {
        }

        // No-op à l'équip : déclenché uniquement en combat via SpellEffectHandler.
        [ItemEffect(EffectsEnum.Effect_CastSpell_1175)]
        public static void CastSpell1175(Character character, int delta)
        {
        }
    }
}
