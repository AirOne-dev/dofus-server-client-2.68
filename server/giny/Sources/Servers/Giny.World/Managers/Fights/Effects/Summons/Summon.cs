using Giny.Core.DesignPattern;
using Giny.Protocol.Enums;
using Giny.World.Managers.Effects;
using Giny.World.Managers.Fights.Buffs;
using Giny.World.Managers.Fights.Cast;
using Giny.World.Managers.Fights.Fighters;
using Giny.World.Records.Maps;
using Giny.World.Records.Monsters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Giny.World.Managers.Fights.Effects.Summons
{
    [SpellEffectHandler(EffectsEnum.Effect_Summon)]
    public class Summon : SpellEffectHandler
    {
        public Summon(EffectDice effect, SpellCastHandler castHandler) : base(effect, castHandler)
        {

        }


        protected override void Apply(IEnumerable<Fighter> targets)
        {
            MonsterRecord record = MonsterRecord.GetMonsterRecord((short)Effect.Min);

            if (record != null)
            {
                var summonCell = GetSummonCell();

                if (summonCell == null)
                {
                    summonCell = CastHandler.Cast.GetParents().First().BaseTargetCell;
                }
                if (summonCell != null)
                {
                    SummonedMonster summon = CreateSummon(record, (byte)Effect.Max, summonCell);
                    if (summon.Record.Id == 5834) // fix Pelle Animée qui ne doit pas infliger de dmg
                    {
                        summon.canDealPushBackDamages = false;
                    }

                    if (Source.CanSummon() || !summon.UseSummonSlot())
                    {
                        Source.Fight.AddSummon(Source, summon);
                    }
                }
            }


        }
    }
}
