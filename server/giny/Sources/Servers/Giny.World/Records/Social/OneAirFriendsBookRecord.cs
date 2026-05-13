using Giny.ORM;
using Giny.ORM.Attributes;
using Giny.ORM.Interfaces;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Giny.World.Records.Social
{
    // OneAir : carnet social par compte (amis + ignorés + flags warn-on-*).
    // Le client Dofus 2.68 envoie tout par accountId, jamais par characterId,
    // donc on stocke l'état au niveau du compte.
    [Table("friends_book")]
    public class OneAirFriendsBookRecord : IRecord
    {
        [Container]
        private static readonly ConcurrentDictionary<int, OneAirFriendsBookRecord> Books = new ConcurrentDictionary<int, OneAirFriendsBookRecord>();

        [Ignore]
        public long Id => AccountId;

        [Primary]
        public int AccountId { get; set; }

        [Update]
        [Blob]
        public List<int> FriendAccountIds { get; set; } = new List<int>();

        [Update]
        [Blob]
        public List<int> IgnoredAccountIds { get; set; } = new List<int>();

        [Update]
        public bool WarnOnConnection { get; set; }

        [Update]
        public bool WarnOnLevelGain { get; set; }

        [Update]
        public bool StatusShared { get; set; } = true;

        public static OneAirFriendsBookRecord Get(int accountId)
        {
            return Books.TryGetValue(accountId, out var book) ? book : null;
        }

        public static OneAirFriendsBookRecord GetOrCreate(int accountId)
        {
            var existing = Get(accountId);
            if (existing != null) return existing;

            var book = new OneAirFriendsBookRecord
            {
                AccountId = accountId,
                FriendAccountIds = new List<int>(),
                IgnoredAccountIds = new List<int>(),
                WarnOnConnection = false,
                WarnOnLevelGain = false,
                StatusShared = true,
            };
            book.AddLater();
            return book;
        }

        public bool IsFriend(int accountId) => FriendAccountIds.Contains(accountId);
        public bool IsIgnored(int accountId) => IgnoredAccountIds.Contains(accountId);

        public static IEnumerable<OneAirFriendsBookRecord> GetAllBooks() => Books.Values;
    }
}
