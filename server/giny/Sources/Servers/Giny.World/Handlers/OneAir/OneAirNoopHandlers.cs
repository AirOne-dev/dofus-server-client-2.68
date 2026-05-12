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
        public static void HandleAllianceGetPlayerApplicationMessage(AllianceGetPlayerApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceRanksRequestMessage(AllianceRanksRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAnomalySubareaInformationRequestMessage(AnomalySubareaInformationRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleSequenceNumberMessage(SequenceNumberMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleClientKeyMessage(ClientKeyMessage message, WorldClient client) { }
    }
}
