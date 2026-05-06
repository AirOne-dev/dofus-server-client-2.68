// OneAir — handlers pour tous les messages haven bag du protocole 2.68.
//
// Remplace le HavenBagHandler.cs vanilla (qui ne gérait que EnterHavenBag avec
// un teleport bête sans sortie possible). Toute la logique métier est dans
// OneAirHavenBagPatch — ces handlers ne font que router les messages.
//
// Posé via COPY dans le Dockerfile à l'emplacement original
// (Sources/Servers/Giny.World/Handlers/Roleplay/HavenBag/HavenBagHandler.cs)
// pour que le scan de réflexion de ProtocolMessageManager les enregistre.
using Giny.Core.Network.Messages;
using Giny.Protocol.Messages;
using Giny.World.Managers.Chat;
using Giny.World.Network;

namespace Giny.World.Handlers.Roleplay.HeavenBag
{
    class HeavenBagHandler
    {
        [MessageHandler]
        public static void HandleEnterHavenBagRequest(EnterHavenBagRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.EnterHavenBag(client.Character, message.havenBagOwner);
        }

        [MessageHandler]
        public static void HandleExitHavenBagRequest(ExitHavenBagRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.ExitHavenBag(client.Character);
        }

        [MessageHandler]
        public static void HandleEditHavenBagRequest(EditHavenBagRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.StartEdit(client.Character);
        }

        [MessageHandler]
        public static void HandleEditHavenBagCancelRequest(EditHavenBagCancelRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.CancelEdit(client.Character);
        }

        [MessageHandler]
        public static void HandleOpenHavenBagFurnitureSequenceRequest(OpenHavenBagFurnitureSequenceRequestMessage message, WorldClient client)
        {
            // Le SWF envoie ce msg AVANT la séquence de Save (cf. décompil
            // HavenbagFrame ligne 247-261). On nettoie les meubles pour
            // pouvoir les re-insérer via les paquets HavenBagFurnituresRequest
            // qui suivent.
            OneAirHavenBagPatch.OpenFurnitureSequence(client.Character);
        }

        [MessageHandler]
        public static void HandleCloseHavenBagFurnitureSequenceRequest(CloseHavenBagFurnitureSequenceRequestMessage message, WorldClient client)
        {
            // Fin de la séquence de save : on echo HavenBagFurnituresMessage
            // + EditHavenBagFinishedMessage pour sortir le SWF du mode édition.
            OneAirHavenBagPatch.FinishEdit(client.Character);
        }

        [MessageHandler]
        public static void HandleHavenBagFurnituresRequest(HavenBagFurnituresRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.SaveFurnitures(client.Character, message.cellIds, message.funitureIds, message.orientations);
        }

        [MessageHandler]
        public static void HandleChangeHavenBagRoomRequest(ChangeHavenBagRoomRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.ChangeRoom(client.Character, message.roomId);
        }

        [MessageHandler]
        public static void HandleChangeThemeRequest(ChangeThemeRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.ChangeTheme(client.Character, message.theme);
        }

        // ExchangeRequest non géré par Giny — on l'intercepte pour router le
        // bouton "coffre" du havre-sac vers BankExchange. Tout autre type est
        // ignoré silencieusement (vanilla ne le gérait pas non plus).
        [MessageHandler]
        public static void HandleExchangeRequest(ExchangeRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.TryHandleExchangeRequest(client.Character, message.exchangeType);
        }

        // Loterie quotidienne — le client envoie la même structure que la
        // réponse, on dispatch sur notre logique de cooldown.
        [MessageHandler]
        public static void HandleHavenBagDailyLotery(HavenBagDailyLoteryMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.HandleLoteryRequest(client.Character);
        }
    }
}
