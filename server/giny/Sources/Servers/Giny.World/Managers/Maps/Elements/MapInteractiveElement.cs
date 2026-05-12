using Giny.Protocol.Messages;
using Giny.Protocol.Types;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Maps.Instances;
using Giny.World.Records.Maps;

namespace Giny.World.Managers.Maps.Elements
{
    public class MapInteractiveElement : MapElement
    {
        public MapInteractiveElement(MapInstance mapInstance, InteractiveElementRecord record) : base(record, mapInstance)
        {

        }

        protected InteractiveElementSkill[] GetInteractiveElementSkill()
        {
            return new InteractiveElementSkill[]
            {
                new InteractiveElementSkill((int)Record.Skill.SkillId, 0)
            };
        }

        public void Update()
        {
            foreach (var character in MapInstance.GetEntities<Character>())
            {
                character.Client.Send(new InteractiveElementUpdatedMessage(GetInteractiveElement(character)));
            }
        }
        protected virtual bool IsSkillEnabled(Character character, SkillRecord record)
        {
            return character.SkillsAllowed.Contains(Record.Skill.Record);
        }
        public InteractiveElement GetInteractiveElement(Character character)
        {
            if (Record.Skill != null && Record.Skill.Type == Giny.Protocol.Custom.Enums.InteractiveTypeEnum.POINT_OUT_AN_EXIT282) return new InteractiveElement((int)Record.Identifier, (int)Record.Skill.Type, new InteractiveElementSkill[0], GetInteractiveElementSkill(), true); InteractiveElementSkill[] skills = GetInteractiveElementSkill();

            if (IsSkillEnabled(character, Record.Skill.Record))
            {
                return new InteractiveElement((int)Record.Identifier, (int)Record.Skill.Type, skills, new InteractiveElementSkill[0], true);
            }
            else
            {
                return new InteractiveElement((int)Record.Identifier, (int)Record.Skill.Type, new InteractiveElementSkill[0], skills, true);
            }
        }
    }
}
