// Toute la logique est dans OneAirHavenBagPatch ; ces handlers ne font que router.
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

        // Le SWF envoie Open avant la séquence Save : on nettoie les meubles
        // pour pouvoir les re-insérer via les HavenBagFurnituresRequest qui suivent.
        [MessageHandler]
        public static void HandleOpenHavenBagFurnitureSequenceRequest(OpenHavenBagFurnitureSequenceRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.OpenFurnitureSequence(client.Character);
        }

        [MessageHandler]
        public static void HandleCloseHavenBagFurnitureSequenceRequest(CloseHavenBagFurnitureSequenceRequestMessage message, WorldClient client)
        {
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

        // Vanilla ignore ExchangeRequest ; on l'intercepte pour router
        // le bouton "coffre" du havre-sac vers BankExchange.
        [MessageHandler]
        public static void HandleExchangeRequest(ExchangeRequestMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.TryHandleExchangeRequest(client.Character, message.exchangeType);
        }

        [MessageHandler]
        public static void HandleHavenBagDailyLotery(HavenBagDailyLoteryMessage message, WorldClient client)
        {
            OneAirHavenBagPatch.HandleLoteryRequest(client.Character);
        }
    }
}
