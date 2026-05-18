using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Effects;
using Giny.World.Managers.Fights.Buffs;
using Giny.World.Managers.Fights.Cast;
using Giny.World.Managers.Fights.Fighters;
using Giny.World.Records.Maps;
using Giny.World.Records.Spells;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Giny.World.Managers.Fights.Effects.Buffs
{
    [SpellEffectHandler(EffectsEnum.Effect_AddState)]
    public class AddState : SpellEffectHandler
    {
        public AddState(EffectDice effect, SpellCastHandler castHandler) :
            base(effect, castHandler)
        {

        }

        protected override void Apply(IEnumerable<Fighter> targets)
        {
            SpellStateRecord stateRecord = SpellStateRecord.GetSpellStateRecord(Effect.Value);

            foreach (var target in targets)
            {
                HandleSpecificUniqueState(target, stateRecord);
                AddStateBuff(target, stateRecord, Effect.DispellableEnum);  
            }
        }

        private void HandleSpecificUniqueState(Fighter target, SpellStateRecord stateRecord)
        {
            // Dernière rune Hupper
            if (stateRecord.Id == 701 || stateRecord.Id == 702 || stateRecord.Id == 703 || stateRecord.Id == 704)
            {
                target.removeStateBuffFromID(target, 701);
                target.removeStateBuffFromID(target, 702);
                target.removeStateBuffFromID(target, 703);
                target.removeStateBuffFromID(target, 704);
            }

            // Tromperie Ecaflip
            if (stateRecord.Id == 579 || stateRecord.Id == 580 || stateRecord.Id == 581 || stateRecord.Id == 582)
            {
                target.removeStateBuffFromID(target, 579);
                target.removeStateBuffFromID(target, 580);
                target.removeStateBuffFromID(target, 581);
                target.removeStateBuffFromID(target, 582);
            }

            // Alchi-Rhetorique Lapino Eniripsa
            if (stateRecord.Id == 4175 || stateRecord.Id == 4176 || stateRecord.Id == 4177 || stateRecord.Id == 4178)
            {
                target.removeStateBuffFromID(target, 4175);
                target.removeStateBuffFromID(target, 4176);
                target.removeStateBuffFromID(target, 4177);
                target.removeStateBuffFromID(target, 4178);
            }
        }
    }
}
