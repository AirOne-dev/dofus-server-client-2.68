using Giny.Core.DesignPattern;
using Giny.ORM;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.Protocol.Types;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Experiences;
using Giny.World.Managers.Guilds;
using Giny.World.Network;
using Giny.World.Records.Characters;
using Giny.World.Records.Social;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Giny.World.Managers.Social
{
    // OneAir : Social book runtime. Pas de cache permanent côté manager,
    // tout passe par OneAirFriendsBookRecord.Get(...). Le manager s'occupe
    // surtout des broadcasts (warn-on-connection / warn-on-level / online updates)
    // et des conversions Character → FriendInformations / IgnoredInformations.
    public class OneAirFriendsManager : Singleton<OneAirFriendsManager>
    {
        public const int MaxFriends = 50;
        public const int MaxIgnored = 50;

        [StartupInvoke("OneAir Friends", StartupInvokePriority.SixthPath)]
        public void Initialize()
        {
            // OneAirFriendsBookRecord est chargé par l'ORM via [Container] ; rien à faire ici.
        }

        // ------------------------------------------------------------------
        // Lookup helpers
        // ------------------------------------------------------------------

        public static CharacterRecord ResolveCharacterByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return CharacterRecord.GetCharacterRecords().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static int? ResolveAccountByName(string name)
        {
            var record = ResolveCharacterByName(name);
            return record == null ? (int?)null : record.AccountId;
        }

        // First character of the account, used to display a "main char" in lists.
        public static CharacterRecord GetMainCharacter(int accountId)
        {
            return CharacterRecord.GetCharactersByAccountId(accountId).OrderByDescending(x => x.Experience).FirstOrDefault();
        }

        public static WorldClient GetOnlineClientByAccount(int accountId)
        {
            return WorldServer.Instance.GetOnlineClient(c => c.Account != null && c.Account.Id == accountId);
        }

        // ------------------------------------------------------------------
        // Mutations
        // ------------------------------------------------------------------

        public ListAddFailureEnum AddFriend(WorldClient client, AbstractPlayerSearchInformation target, out int addedAccountId)
        {
            addedAccountId = 0;
            CharacterRecord record = ResolveSearchTarget(target);
            if (record == null) return ListAddFailureEnum.LIST_ADD_FAILURE_NOT_FOUND;

            if (record.AccountId == client.Account.Id) return ListAddFailureEnum.LIST_ADD_FAILURE_EGOCENTRIC;

            var book = OneAirFriendsBookRecord.GetOrCreate(client.Account.Id);
            if (book.FriendAccountIds.Count >= MaxFriends) return ListAddFailureEnum.LIST_ADD_FAILURE_OVER_QUOTA;
            if (book.IsFriend(record.AccountId)) return ListAddFailureEnum.LIST_ADD_FAILURE_IS_DOUBLE;

            book.FriendAccountIds.Add(record.AccountId);
            book.UpdateLater();
            addedAccountId = record.AccountId;
            return 0;
        }

        public bool RemoveFriend(WorldClient client, int accountId)
        {
            var book = OneAirFriendsBookRecord.Get(client.Account.Id);
            if (book == null || !book.FriendAccountIds.Contains(accountId)) return false;
            book.FriendAccountIds.Remove(accountId);
            book.UpdateLater();
            return true;
        }

        public ListAddFailureEnum AddIgnored(WorldClient client, AbstractPlayerSearchInformation target, bool session, out int addedAccountId)
        {
            addedAccountId = 0;
            CharacterRecord record = ResolveSearchTarget(target);
            if (record == null) return ListAddFailureEnum.LIST_ADD_FAILURE_NOT_FOUND;

            if (record.AccountId == client.Account.Id) return ListAddFailureEnum.LIST_ADD_FAILURE_EGOCENTRIC;

            var book = OneAirFriendsBookRecord.GetOrCreate(client.Account.Id);
            if (book.IgnoredAccountIds.Count >= MaxIgnored) return ListAddFailureEnum.LIST_ADD_FAILURE_OVER_QUOTA;
            if (book.IsIgnored(record.AccountId)) return ListAddFailureEnum.LIST_ADD_FAILURE_IS_DOUBLE;

            addedAccountId = record.AccountId;

            if (session)
            {
                // session-only : on ne persiste pas
                return 0;
            }

            book.IgnoredAccountIds.Add(record.AccountId);
            book.UpdateLater();
            return 0;
        }

        public bool RemoveIgnored(WorldClient client, int accountId)
        {
            var book = OneAirFriendsBookRecord.Get(client.Account.Id);
            if (book == null || !book.IgnoredAccountIds.Contains(accountId)) return false;
            book.IgnoredAccountIds.Remove(accountId);
            book.UpdateLater();
            return true;
        }

        private static CharacterRecord ResolveSearchTarget(AbstractPlayerSearchInformation target)
        {
            if (target is PlayerSearchCharacterNameInformation byName)
                return ResolveCharacterByName(byName.name);
            // PlayerSearchTagInformation : non supporté en 2.68 sur OneAir
            return null;
        }

        // ------------------------------------------------------------------
        // Build payloads
        // ------------------------------------------------------------------

        public FriendInformations BuildFriendInformations(int targetAccountId)
        {
            var online = GetOnlineClientByAccount(targetAccountId);
            var main = online != null ? online.Character.Record : GetMainCharacter(targetAccountId);

            var accountTag = BuildAccountTag(targetAccountId, main);

            if (online != null && online.InGame)
            {
                var character = online.Character;
                var book = OneAirFriendsBookRecord.Get(targetAccountId);
                bool statusShared = book?.StatusShared ?? true;

                var status = statusShared
                    ? character.GetPlayerStatus()
                    : new PlayerStatus((byte)PlayerStatusEnum.PLAYER_STATUS_AVAILABLE);

                GuildInformations guildInfo = character.HasGuild
                    ? character.Guild.GetGuildInformations()
                    : new GuildInformations { guildId = 0, guildName = "", guildLevel = 0, guildEmblem = new SocialEmblem((short)0, 0, (byte)0, 0) };

                return new FriendOnlineInformations(
                    playerId: character.Id,
                    playerName: character.Name,
                    level: character.Level,
                    alignmentSide: 0,
                    breed: character.Record.BreedId,
                    sex: character.Record.Sex,
                    guildInfo: guildInfo,
                    moodSmileyId: 0,
                    status: status,
                    havenBagShared: false,
                    accountId: targetAccountId,
                    accountTag: accountTag,
                    playerState: character.Fighting ? (byte)PlayerStateEnum.GAME_TYPE_FIGHT : (byte)PlayerStateEnum.GAME_TYPE_ROLEPLAY,
                    lastConnection: 0,
                    achievementPoints: character.AchievementPoints ?? 0,
                    leagueId: 0,
                    ladderPosition: 0
                );
            }

            return new FriendInformations(
                playerState: (byte)PlayerStateEnum.NOT_CONNECTED,
                lastConnection: 0,
                achievementPoints: 0,
                leagueId: 0,
                ladderPosition: 0,
                accountId: targetAccountId,
                accountTag: accountTag
            );
        }

        public IgnoredInformations BuildIgnoredInformations(int targetAccountId)
        {
            var online = GetOnlineClientByAccount(targetAccountId);
            var main = online != null ? online.Character.Record : GetMainCharacter(targetAccountId);
            var accountTag = BuildAccountTag(targetAccountId, main);

            if (online != null && online.InGame)
            {
                var character = online.Character;
                return new IgnoredOnlineInformations(
                    playerId: character.Id,
                    playerName: character.Name,
                    breed: character.Record.BreedId,
                    sex: character.Record.Sex,
                    accountId: targetAccountId,
                    accountTag: accountTag);
            }

            return new IgnoredInformations(accountId: targetAccountId, accountTag: accountTag);
        }

        private static AccountTagInformation BuildAccountTag(int accountId, CharacterRecord main)
        {
            // OneAir n'a pas de système de tag global Ankama (#1234) ; on dérive
            // un tag stable et lisible depuis l'accountId.
            string nickname = main != null ? main.Name : ("Compte#" + accountId);
            string tagNumber = accountId.ToString("D4");
            return new AccountTagInformation(nickname, tagNumber);
        }

        // ------------------------------------------------------------------
        // Hooks lifecycle
        // ------------------------------------------------------------------

        public void OnCharacterConnected(Character character)
        {
            BroadcastFriendUpdate(character);

            // Annonce optionnelle aux amis avec WarnOnConnection.
            int myAccountId = character.Client.Account.Id;
            string myNick = character.Name;
            foreach (var book in OneAirFriendsBookRecord.GetAllBooks())
            {
                if (!book.WarnOnConnection || !book.FriendAccountIds.Contains(myAccountId)) continue;
                var target = GetOnlineClientByAccount(book.AccountId);
                if (target?.InGame == true)
                    target.Character.TextInformation(TextInformationTypeEnum.TEXT_INFORMATION_MESSAGE, 143, myNick);
            }
        }

        public void OnCharacterDisconnected(Character character)
        {
            BroadcastFriendUpdate(character, forceOffline: true);
        }

        public void OnCharacterLevelGain(Character character, short oldLevel, short newLevel)
        {
            if (newLevel <= oldLevel) return;
            int myAccountId = character.Client.Account.Id;
            string myNick = character.Name;
            foreach (var book in OneAirFriendsBookRecord.GetAllBooks())
            {
                if (!book.WarnOnLevelGain || !book.FriendAccountIds.Contains(myAccountId)) continue;
                var target = GetOnlineClientByAccount(book.AccountId);
                if (target?.InGame == true)
                    target.Character.TextInformation(TextInformationTypeEnum.TEXT_INFORMATION_MESSAGE, 144, myNick, newLevel.ToString());
            }
            BroadcastFriendUpdate(character);
        }

        // Push un FriendUpdateMessage à chaque ami connecté qui a 'character' dans son livre.
        public void BroadcastFriendUpdate(Character character, bool forceOffline = false)
        {
            int myAccountId = character.Client.Account.Id;
            foreach (var book in OneAirFriendsBookRecord.GetAllBooks())
            {
                if (!book.FriendAccountIds.Contains(myAccountId)) continue;
                var target = GetOnlineClientByAccount(book.AccountId);
                if (target?.InGame != true) continue;

                FriendInformations info = forceOffline
                    ? new FriendInformations(
                        playerState: (byte)PlayerStateEnum.NOT_CONNECTED,
                        lastConnection: 0,
                        achievementPoints: character.AchievementPoints ?? 0,
                        leagueId: 0,
                        ladderPosition: 0,
                        accountId: myAccountId,
                        accountTag: BuildAccountTag(myAccountId, character.Record))
                    : BuildFriendInformations(myAccountId);

                target.Send(new FriendUpdateMessage(info));
            }
        }
    }
}
