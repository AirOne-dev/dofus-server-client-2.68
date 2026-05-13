// Stubs vides pour court-circuiter OneAirUnhandledLogger.LogNetMessage sur les
// messages d'info / keepalive / features non implémentées.
using Giny.Core.Network.Messages;
using Giny.Protocol.Messages;
using Giny.World.Network;

namespace Giny.World.Handlers.OneAir
{
    static class OneAirNoopHandlers
    {
        [MessageHandler]
        public static void HandleHaapiApiKeyRequestMessage(HaapiApiKeyRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleHaapiShopApiKeyRequestMessage(HaapiShopApiKeyRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleSpouseGetInformationsMessage(SpouseGetInformationsMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAnomalySubareaInformationRequestMessage(AnomalySubareaInformationRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleSequenceNumberMessage(SequenceNumberMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleClientKeyMessage(ClientKeyMessage message, WorldClient client) { }

        // (UpdateRecruitmentInformationMessage est géré dans GuildsHandler.cs)

        // Coffre de guilde (l'UI s'abonne aux changements quand on ouvre l'onglet) :
        // pas de feature OneAir.
        [MessageHandler]
        public static void HandleStartListenGuildChestStructure(StartListenGuildChestStructureMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleStopListenGuildChestStructure(StopListenGuildChestStructureMessage message, WorldClient client) { }

        // Guild applications : pas de support OneAir, l'UI tape "aucune candidature".
        [MessageHandler]
        public static void HandleGuildListApplicationRequest(GuildListApplicationRequestMessage message, WorldClient client)
        {
            client.Send(new GuildListApplicationAnswerMessage(new Giny.Protocol.Types.SocialApplicationInformation[0], 0, 0, 0));
        }

        [MessageHandler]
        public static void HandleGuildIsThereAnyApplication(GuildIsThereAnyApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleGuildSubmitApplication(GuildSubmitApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleGuildDeleteApplicationRequest(GuildDeleteApplicationRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleGuildApplicationAnswer(GuildApplicationAnswerMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleGuildUpdateApplication(GuildUpdateApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleGuildApplicationListen(GuildApplicationListenMessage message, WorldClient client) { }

        // Alliance applications : idem
        [MessageHandler]
        public static void HandleAllianceSubmitApplication(AllianceSubmitApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceDeleteApplicationRequest(AllianceDeleteApplicationRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceUpdateApplication(AllianceUpdateApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceApplicationListen(AllianceApplicationListenMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceIsThereAnyApplication(AllianceIsThereAnyApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceApplicationAnswer(AllianceApplicationAnswerMessage message, WorldClient client) { }
    }
}
