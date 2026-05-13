using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Alliances;
using Giny.World.Managers.Entities.Characters;

namespace Giny.World.Managers.Dialogs.DialogBox
{
    // OneAir : invite d'une guilde dans une alliance. Source = chef d'alliance,
    // Target = chef de la guilde invitée.
    public class AllianceInvitationRequest : RequestBox
    {
        private const byte STATE_SENT = 1;
        private const byte STATE_CANCELED = 2;
        private const byte STATE_OK = 3;

        public AllianceInvitationRequest(Character source, Character target) : base(source, target)
        {
        }

        protected override void OnOpen()
        {
            Source.Client.Send(new AllianceInvitationStateRecruterMessage(Target.Name, STATE_SENT));
            Target.Client.Send(new AllianceInvitationStateRecrutedMessage(STATE_SENT));
            Target.Client.Send(new AllianceInvitedMessage(Source.Name, Source.Alliance.GetAllianceInformations()));
            base.OnOpen();
        }

        protected override void OnAccept()
        {
            // Au moment de l'accept, la guilde du target rejoint l'alliance du source.
            if (Source.Alliance != null && Target.HasGuild && Target.Guild.Record.AllianceId == 0)
            {
                Source.Alliance.AddGuild(Target.Guild, founder: false);
                // OnCharacterJoinedGuild fait office d'OnAllianceJoined pour tous les membres connectés de la guilde.
                foreach (var member in Target.Guild.GetOnlineMembers())
                {
                    OneAirAllianceManager.Instance.OnCharacterJoinedGuild(member);
                }
            }

            Source.Client.Send(new AllianceInvitationStateRecruterMessage(Target.Name, STATE_OK));
            Target.Client.Send(new AllianceInvitationStateRecrutedMessage(STATE_OK));
            base.OnAccept();
        }

        protected override void OnDeny()
        {
            Source.TextInformation(TextInformationTypeEnum.TEXT_INFORMATION_MESSAGE, 246, Target.Name);
            Source.Client.Send(new AllianceInvitationStateRecruterMessage(Target.Name, STATE_CANCELED));
            Target.Client.Send(new AllianceInvitationStateRecrutedMessage(STATE_CANCELED));
            base.OnDeny();
        }

        protected override void OnCancel()
        {
            Source.Client.Send(new AllianceInvitationStateRecruterMessage(Target.Name, STATE_CANCELED));
            Target.Client.Send(new AllianceInvitationStateRecrutedMessage(STATE_CANCELED));
            base.OnCancel();
        }
    }
}
