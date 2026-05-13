using Giny.Core.Network.Messages;
using Giny.ORM;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.Protocol.Types;
using Giny.World.Managers.Social;
using Giny.World.Network;
using Giny.World.Records.Characters;
using Giny.World.Records.Social;
using System.Collections.Generic;
using System.Linq;

namespace Giny.World.Handlers.Social
{
    class FriendsHandler
    {
        // -----------------------------------------------------------------
        // Lists
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleAcquaintancesGetList(AcquaintancesGetListMessage message, WorldClient client)
        {
            // OneAir : pas de système d'acquaintances Haapi, on renvoie vide.
            client.Send(new AcquaintancesListMessage(new AcquaintanceInformation[0]));
        }

        [MessageHandler]
        public static void HandleFriendsGetList(FriendsGetListMessage message, WorldClient client)
        {
            var book = OneAirFriendsBookRecord.Get(client.Account.Id);
            if (book == null || book.FriendAccountIds.Count == 0)
            {
                client.Send(new FriendsListMessage(new FriendInformations[0]));
                return;
            }

            var manager = OneAirFriendsManager.Instance;
            var list = book.FriendAccountIds.Select(id => manager.BuildFriendInformations(id)).ToArray();
            client.Send(new FriendsListMessage(list));
        }

        [MessageHandler]
        public static void HandleIgnoredGetList(IgnoredGetListMessage message, WorldClient client)
        {
            var book = OneAirFriendsBookRecord.Get(client.Account.Id);
            if (book == null || book.IgnoredAccountIds.Count == 0)
            {
                client.Send(new IgnoredListMessage(new IgnoredInformations[0]));
                return;
            }

            var manager = OneAirFriendsManager.Instance;
            var list = book.IgnoredAccountIds.Select(id => manager.BuildIgnoredInformations(id)).ToArray();
            client.Send(new IgnoredListMessage(list));
        }

        // -----------------------------------------------------------------
        // Friend add/remove
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleFriendAddRequest(FriendAddRequestMessage message, WorldClient client)
        {
            int addedId;
            var failure = OneAirFriendsManager.Instance.AddFriend(client, message.target, out addedId);

            if (failure != 0)
            {
                client.Send(new FriendAddFailureMessage((byte)failure));
                return;
            }

            client.Send(new FriendAddedMessage(OneAirFriendsManager.Instance.BuildFriendInformations(addedId)));
        }

        [MessageHandler]
        public static void HandleFriendDeleteRequest(FriendDeleteRequestMessage message, WorldClient client)
        {
            var record = CharacterRecord.GetCharacterRecords().FirstOrDefault(x => x.AccountId == message.accountId);
            string nick = record?.Name ?? ("Compte#" + message.accountId);
            var tag = new AccountTagInformation(nick, message.accountId.ToString("D4"));

            bool success = OneAirFriendsManager.Instance.RemoveFriend(client, message.accountId);
            client.Send(new FriendDeleteResultMessage(success, tag));
        }

        // -----------------------------------------------------------------
        // Ignored add/remove
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleIgnoredAddRequest(IgnoredAddRequestMessage message, WorldClient client)
        {
            int addedId;
            var failure = OneAirFriendsManager.Instance.AddIgnored(client, message.target, message.session, out addedId);

            if (failure != 0)
            {
                client.Send(new IgnoredAddFailureMessage((byte)failure));
                return;
            }

            client.Send(new IgnoredAddedMessage(OneAirFriendsManager.Instance.BuildIgnoredInformations(addedId), message.session));
        }

        [MessageHandler]
        public static void HandleIgnoredDeleteRequest(IgnoredDeleteRequestMessage message, WorldClient client)
        {
            var record = CharacterRecord.GetCharacterRecords().FirstOrDefault(x => x.AccountId == message.accountId);
            string nick = record?.Name ?? ("Compte#" + message.accountId);
            var tag = new AccountTagInformation(nick, message.accountId.ToString("D4"));

            bool success = OneAirFriendsManager.Instance.RemoveIgnored(client, message.accountId);
            client.Send(new IgnoredDeleteResultMessage(success, tag, message.session));
        }

        // -----------------------------------------------------------------
        // Warn flags
        // -----------------------------------------------------------------

        [MessageHandler]
        public static void HandleFriendSetWarnOnConnection(FriendSetWarnOnConnectionMessage message, WorldClient client)
        {
            var book = OneAirFriendsBookRecord.GetOrCreate(client.Account.Id);
            book.WarnOnConnection = message.enable;
            book.UpdateLater();
            client.Send(new FriendWarnOnConnectionStateMessage(message.enable));
        }

        [MessageHandler]
        public static void HandleFriendSetWarnOnLevelGain(FriendSetWarnOnLevelGainMessage message, WorldClient client)
        {
            var book = OneAirFriendsBookRecord.GetOrCreate(client.Account.Id);
            book.WarnOnLevelGain = message.enable;
            book.UpdateLater();
            client.Send(new FriendWarnOnLevelGainStateMessage(message.enable));
        }

        [MessageHandler]
        public static void HandleFriendSetStatusShare(FriendSetStatusShareMessage message, WorldClient client)
        {
            var book = OneAirFriendsBookRecord.GetOrCreate(client.Account.Id);
            book.StatusShared = message.share;
            book.UpdateLater();
            client.Send(new FriendStatusShareStateMessage(message.share));
        }
    }
}
