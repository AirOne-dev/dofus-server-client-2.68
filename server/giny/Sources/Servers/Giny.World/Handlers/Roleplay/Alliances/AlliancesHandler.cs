using Giny.Core.Network.Messages;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Alliances;
using Giny.World.Managers.Dialogs.DialogBox;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Network;
using Giny.World.Records.Alliances;
using System.Linq;

namespace Giny.World.Handlers.Roleplay.Alliances
{
    // OneAir : handlers Alliance complets. Le pattern suit GuildsHandler.cs.
    class AlliancesHandler
    {
        // -----------------------------------------------------------------
        // Création
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleAllianceCreationValidMessage(AllianceCreationValidMessage message, WorldClient client)
        {
            var emblem = new AllianceEmblemRecord(
                message.allianceEmblem.symbolShape,
                message.allianceEmblem.symbolColor,
                message.allianceEmblem.backgroundShape,
                message.allianceEmblem.backgroundColor);

            byte result = OneAirAllianceManager.Instance.CreateAlliance(client.Character, message.allianceName, message.allianceTag, emblem);
            client.Character.OnAllianceCreate(result);
        }

        // -----------------------------------------------------------------
        // Lists
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleAllianceSummaryRequest(AllianceSummaryRequestMessage message, WorldClient client)
        {
            client.Send(OneAirAllianceManager.Instance.BuildSummaryMessage());
        }

        [MessageHandler]
        public static void HandleAllianceFactsRequest(AllianceFactsRequestMessage message, WorldClient client)
        {
            var alliance = OneAirAllianceManager.Instance.GetAlliance(message.allianceId);
            if (alliance == null)
            {
                client.Send(new AllianceFactsErrorMessage(message.allianceId));
                return;
            }
            client.Send(alliance.BuildAllianceFactsMessage());
        }

        [MessageHandler]
        public static void HandleAllianceInsiderInfoRequest(AllianceInsiderInfoRequestMessage message, WorldClient client)
        {
            if (client.Character.Alliance == null) return;
            client.Send(client.Character.Alliance.BuildInsiderInfoMessage());
        }

        [MessageHandler]
        public static void HandleAllianceGetRecruitmentInformation(AllianceGetRecruitmentInformationMessage message, WorldClient client)
        {
            if (client.Character.Alliance == null) return;
            client.Send(new AllianceRecruitmentInformationMessage(client.Character.Alliance.BuildRecruitmentInformation()));
        }

        [MessageHandler]
        public static void HandleAllianceRanksRequest(AllianceRanksRequestMessage message, WorldClient client)
        {
            // OneAir : on n'a que deux rangs (fondateur / membre) ; on renvoie une liste vide,
            // le client retombe sur ses textes par défaut.
            client.Send(new AllianceRanksMessage(new Giny.Protocol.Types.RankInformation[0]));
        }

        // -----------------------------------------------------------------
        // Invitation
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleAllianceInvitation(AllianceInvitationMessage message, WorldClient client)
        {
            // Seul un membre d'alliance (chef de guilde fondatrice ou membre) peut inviter une autre guilde.
            if (client.Character.Alliance == null)
            {
                client.Character.ReplyError("Vous n'êtes pas dans une alliance.");
                return;
            }

            if (!OneAirAllianceManager.IsGuildBoss(client.Character))
            {
                client.Character.ReplyError("Seul le chef de votre guilde peut inviter une autre guilde dans l'alliance.");
                return;
            }

            if (!client.Character.Alliance.CanAddGuild())
            {
                client.Character.ReplyError("L'alliance a atteint le nombre maximum de guildes.");
                return;
            }

            var target = WorldServer.Instance.GetOnlineClient(x => x.Character.Id == message.targetId);
            if (target == null)
            {
                client.Character.TextInformation(TextInformationTypeEnum.TEXT_INFORMATION_ERROR, 208);
                return;
            }

            if (!target.Character.HasGuild)
            {
                client.Character.ReplyError(target.Character.Name + " n'est pas dans une guilde.");
                return;
            }
            if (target.Character.Guild.Record.AllianceId != 0)
            {
                client.Character.ReplyError("Cette guilde est déjà dans une alliance.");
                return;
            }
            if (!OneAirAllianceManager.IsGuildBoss(target.Character))
            {
                client.Character.ReplyError(target.Character.Name + " n'est pas le chef de sa guilde.");
                return;
            }
            if (target.Character.Busy)
            {
                client.Character.TextInformation(TextInformationTypeEnum.TEXT_INFORMATION_ERROR, 209);
                return;
            }

            target.Character.OpenRequestBox(new AllianceInvitationRequest(client.Character, target.Character));
        }

        [MessageHandler]
        public static void HandleAllianceInvitationAnswer(AllianceInvitationAnswerMessage message, WorldClient client)
        {
            if (client.Character.HasRequestBoxOpen<AllianceInvitationRequest>())
            {
                if (message.accept) client.Character.RequestBox.Accept();
                else client.Character.RequestBox.Deny();
            }
        }

        // -----------------------------------------------------------------
        // Kick / leave
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleAllianceKickRequest(AllianceKickRequestMessage message, WorldClient client)
        {
            if (client.Character.Alliance == null) return;
            var alliance = client.Character.Alliance;

            // OneAir : message.kickedId est un characterId. On retrouve sa guilde puis on l'éjecte.
            var record = Giny.World.Records.Characters.CharacterRecord.GetCharacterRecord(message.kickedId);
            if (record == null) return;

            var targetGuild = Giny.World.Managers.Guilds.GuildsManager.Instance.GetGuild(record.GuildId);
            if (targetGuild == null) return;
            if (!alliance.IsGuildInAlliance(targetGuild.Id)) return;

            bool selfLeave = client.Character.Record.GuildId == targetGuild.Id;
            bool isMyGuildBoss = OneAirAllianceManager.IsGuildBoss(client.Character);
            bool isFounderGuild = alliance.Record.Guilds.FirstOrDefault(x => x.GuildId == client.Character.Record.GuildId)?.IsFounder == true;

            // - Le chef d'une guilde peut quitter (kick lui-même)
            // - Le chef de la guilde fondatrice peut kicker n'importe quelle autre guilde
            if (!selfLeave && !isFounderGuild)
            {
                client.Character.ReplyError("Seule la guilde fondatrice peut éjecter une autre guilde.");
                return;
            }
            if (selfLeave && !isMyGuildBoss)
            {
                client.Character.ReplyError("Seul le chef de votre guilde peut la sortir de l'alliance.");
                return;
            }

            alliance.RemoveGuild(targetGuild, kicked: !selfLeave);
        }

        // -----------------------------------------------------------------
        // Motd / bulletin
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleAllianceMotdSetRequest(AllianceMotdSetRequestMessage message, WorldClient client)
        {
            if (client.Character.Alliance == null) return;
            if (!OneAirAllianceManager.IsGuildBoss(client.Character))
            {
                client.Character.ReplyError("Seuls les chefs de guilde peuvent modifier le message de l'alliance.");
                return;
            }
            client.Character.Alliance.SetMotd(client.Character, message.content);
        }

        // -----------------------------------------------------------------
        // No-ops (KOH partial support — pas implémenté côté gameplay)
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleAllianceGetPlayerApplication(AllianceGetPlayerApplicationMessage message, WorldClient client)
        {
            client.Send(new AlliancePlayerNoApplicationInformationMessage());
        }

        [MessageHandler]
        public static void HandleAllianceListApplicationRequest(AllianceListApplicationRequestMessage message, WorldClient client)
        {
            client.Send(new AllianceListApplicationAnswerMessage(new Giny.Protocol.Types.SocialApplicationInformation[0], 0, 0, 0));
        }

        [MessageHandler]
        public static void HandleAllianceMemberStartWarning(AllianceMemberStartWarningOnConnectionMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceMemberStopWarning(AllianceMemberStopWarningOnConnectionMessage message, WorldClient client) { }
    }
}
