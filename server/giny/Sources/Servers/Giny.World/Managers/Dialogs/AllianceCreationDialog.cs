using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Entities.Characters;

namespace Giny.World.Managers.Dialogs
{
    // OneAir : mirror de GuildCreationDialog pour la création d'alliance.
    public class AllianceCreationDialog : Dialog
    {
        public override DialogTypeEnum DialogType => DialogTypeEnum.DIALOG_ALLIANCE_CREATE;

        public AllianceCreationDialog(Character character) : base(character)
        {
        }

        public override void Open()
        {
            Character.Client.Send(new AllianceCreationStartedMessage());
        }
    }
}
