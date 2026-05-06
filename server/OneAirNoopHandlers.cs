// OneAir — handlers vides pour les messages protocol que le client envoie
// régulièrement mais que le serveur n'a pas besoin de traiter.
//
// Sans handler enregistré, ces messages remontent dans
// WorldClient.OnMessageUnhandled → OneAirUnhandledLogger.LogNetMessage et
// polluent en permanence le panel "Actions non gérées" alors que le serveur
// fonctionne très bien sans y répondre. Un [MessageHandler] vide les
// court-circuite avant le fallback.
//
// Les handlers ici NE répondent PAS — ce sont des messages d'info /
// keepalive / requêtes de fonctionnalités non implémentées (alliance,
// mariage, anomalies, Haapi). Si une feature voisine en a besoin un jour,
// le handler approprié peut prendre le relais en remplaçant ce stub.

using Giny.Core.Network.Messages;
using Giny.Protocol.Messages;
using Giny.World.Network;

namespace Giny.World.Handlers.OneAir
{
    static class OneAirNoopHandlers
    {
        // Anti-cheat / Ankama Shield — la clé n'est pas utilisée hors prod Ankama.
        [MessageHandler]
        public static void HandleHaapiApiKeyRequestMessage(HaapiApiKeyRequestMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleHaapiShopApiKeyRequestMessage(HaapiShopApiKeyRequestMessage message, WorldClient client) { }

        // Mariage — pas implémenté côté OneAir.
        [MessageHandler]
        public static void HandleSpouseGetInformationsMessage(SpouseGetInformationsMessage message, WorldClient client) { }

        // Alliances — pas implémenté côté OneAir.
        [MessageHandler]
        public static void HandleAllianceGetPlayerApplicationMessage(AllianceGetPlayerApplicationMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleAllianceRanksRequestMessage(AllianceRanksRequestMessage message, WorldClient client) { }

        // Anomalies (Tot 6.0+) — pas de spawn sur OneAir.
        [MessageHandler]
        public static void HandleAnomalySubareaInformationRequestMessage(AnomalySubareaInformationRequestMessage message, WorldClient client) { }

        // Keepalive / sync séquence — le serveur Giny est stateless là-dessus,
        // pas besoin d'écho.
        [MessageHandler]
        public static void HandleSequenceNumberMessage(SequenceNumberMessage message, WorldClient client) { }

        [MessageHandler]
        public static void HandleClientKeyMessage(ClientKeyMessage message, WorldClient client) { }
    }
}
