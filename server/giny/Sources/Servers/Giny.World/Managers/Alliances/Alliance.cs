using Giny.Core;
using Giny.Core.Extensions;
using Giny.Core.Network.Messages;
using Giny.ORM;
using Giny.Protocol.Custom.Enums;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.Protocol.Types;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Experiences;
using Giny.World.Managers.Guilds;
using Giny.World.Network;
using Giny.World.Records.Alliances;
using Giny.World.Records.Characters;
using Giny.World.Records.Guilds;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Giny.World.Managers.Alliances
{
    // OneAir : entité runtime d'une alliance. Wrap AllianceRecord et joue les
    // mêmes rôles que Guild pour les guildes.
    public class Alliance
    {
        public const int RANK_FOUNDER = 1;
        public const int RANK_MEMBER = 2;

        public AllianceRecord Record { get; private set; }
        public long Id => Record.Id;
        public string Name => Record.Name;
        public string Tag => Record.Tag;

        // membres connectés (par character id) — sert aux broadcasts.
        private readonly ConcurrentDictionary<long, Character> OnlineMembers = new ConcurrentDictionary<long, Character>();

        public Alliance(AllianceRecord record)
        {
            Record = record;
        }

        public IEnumerable<Guild> GetGuilds()
        {
            foreach (var link in Record.Guilds)
            {
                Guild g = null;
                try { g = GuildsManager.Instance.GetGuild(link.GuildId); } catch { }
                if (g != null) yield return g;
            }
        }

        public int GetMemberCount()
        {
            return GetGuilds().Sum(g => g.Record.Members.Count);
        }

        public bool IsGuildInAlliance(long guildId) => Record.Guilds.Any(x => x.GuildId == guildId);

        public bool CanAddGuild() => Record.Guilds.Count < OneAirAllianceManager.MaxGuildsPerAlliance;

        public void AddGuild(Guild guild, bool founder = false)
        {
            if (IsGuildInAlliance(guild.Id)) return;
            Record.Guilds.Add(new AllianceGuildLinkRecord(guild.Id, founder));
            guild.Record.AllianceId = Id;
            guild.Record.UpdateLater();
            Record.UpdateLater();

            // Broadcast à tous les membres déjà online de cette guilde.
            foreach (var character in OneAirAllianceManager.Instance.GetOnlineCharactersOfGuild(guild))
            {
                SendMembership(character);
            }

            BroadcastTextInformation(TextInformationTypeEnum.TEXT_INFORMATION_MESSAGE, (short)244, new object[] { guild.Record.Name });
            BroadcastMembersUpdate();
        }

        public bool RemoveGuild(Guild guild, bool kicked)
        {
            var link = Record.GetGuildLink(guild.Id);
            if (link == null) return false;

            Record.Guilds.Remove(link);
            guild.Record.AllianceId = 0;
            guild.Record.UpdateLater();
            Record.UpdateLater();

            // Notif aux membres de la guilde sortie.
            foreach (var character in OneAirAllianceManager.Instance.GetOnlineCharactersOfGuild(guild))
            {
                character.OnAllianceKick(this);
            }

            // Notif aux membres restants.
            BroadcastTextInformation(TextInformationTypeEnum.TEXT_INFORMATION_MESSAGE, (short)(kicked ? 246 : 245), new object[] { guild.Record.Name });
            BroadcastMembersUpdate();

            if (Record.Guilds.Count == 0)
                OneAirAllianceManager.Instance.RemoveAlliance(this);

            return true;
        }

        public void OnCharacterConnected(Character character)
        {
            OnlineMembers.TryAdd(character.Id, character);
            SendMembership(character);
            RefreshMotd(character);
        }

        public void OnCharacterDisconnected(Character character)
        {
            OnlineMembers.TryRemove(character.Id, out _);
        }

        public void SendMembership(Character character)
        {
            character.Client.Send(new AllianceMembershipMessage(GetAllianceInformations(), GetRankOfGuild(character.Record.GuildId)));
        }

        public int GetRankOfGuild(long guildId)
        {
            var link = Record.GetGuildLink(guildId);
            if (link == null) return RANK_MEMBER;
            return link.IsFounder ? RANK_FOUNDER : RANK_MEMBER;
        }

        public void SetMotd(Character source, string content)
        {
            if (string.IsNullOrEmpty(content) || content.Length > 255) return;
            Record.Motd = new AllianceMotdRecord
            {
                Content = content,
                Timestamp = DateTime.Now.GetUnixTimeStamp(),
                MemberId = source.Id,
                MemberName = source.Name,
            };
            Record.UpdateLater();
            BroadcastMotd();
        }

        public void RefreshMotd()
        {
            foreach (var character in OnlineMembers.Values)
                RefreshMotd(character);
        }

        public void RefreshMotd(Character character)
        {
            if (Record.Motd == null || string.IsNullOrEmpty(Record.Motd.Content)) return;
            character.Client.Send(new AllianceMotdMessage(
                Record.Motd.Content,
                Record.Motd.Timestamp,
                Record.Motd.MemberId,
                Record.Motd.MemberName));
        }

        public void BroadcastMotd() => RefreshMotd();

        public void BroadcastMembersUpdate()
        {
            foreach (var character in OnlineMembers.Values)
                character.Client.Send(BuildInsiderInfoMessage());
        }

        // -----------------------------------------------------------------
        // Conversion to protocol types
        // -----------------------------------------------------------------

        public AllianceInformation GetAllianceInformations()
        {
            return new AllianceInformation
            {
                allianceEmblem = Record.Emblem.ToSocialEmblem(),
                allianceId = (int)Id,
                allianceName = Record.Name,
                allianceTag = Record.Tag,
            };
        }

        public AllianceFactSheetInformation GetAllianceFactSheet()
        {
            return new AllianceFactSheetInformation
            {
                allianceId = (int)Id,
                allianceName = Record.Name,
                allianceTag = Record.Tag,
                allianceEmblem = Record.Emblem.ToSocialEmblem(),
                creationDate = (int)Record.CreationDate.GetUnixTimeStamp(),
                nbMembers = (short)GetMemberCount(),
                nbSubarea = 0,
                nbTaxCollectors = 0,
                recruitment = BuildRecruitmentInformation(),
            };
        }

        public AllianceRecruitmentInformation BuildRecruitmentInformation()
        {
            return new AllianceRecruitmentInformation
            {
                socialId = (int)Id,
                recruitmentType = (byte)SocialRecruitmentTypeEnum.MANUAL,
                recruitmentTitle = "",
                recruitmentText = "",
                selectedLanguages = new int[0],
                selectedCriterion = new int[0],
                minLevel = 0,
                minLevelFacultative = true,
                invalidatedByModeration = false,
                lastEditPlayerName = "",
                lastEditDate = 0,
                recruitmentAutoLocked = false,
            };
        }

        public AllianceMemberInfo BuildMemberInfo(Character character)
        {
            int rank = GetRankOfGuild(character.Record.GuildId);
            return new AllianceMemberInfo(
                avaRoleId: 0,
                id: character.Id,
                name: character.Name,
                level: character.Level,
                breed: character.Record.BreedId,
                sex: character.Record.Sex,
                connected: 1,
                hoursSinceLastConnection: 0,
                accountId: character.Client.Account.Id,
                status: character.GetPlayerStatus(),
                rankId: rank,
                enrollmentDate: 0);
        }

        public AllianceMemberInfo BuildMemberInfo(CharacterRecord record)
        {
            return new AllianceMemberInfo(
                avaRoleId: 0,
                id: record.Id,
                name: record.Name,
                level: ExperienceManager.Instance.GetCharacterLevel(record.Experience),
                breed: record.BreedId,
                sex: record.Sex,
                connected: 0,
                hoursSinceLastConnection: 0,
                accountId: record.AccountId,
                status: new PlayerStatus((byte)PlayerStatusEnum.PLAYER_STATUS_OFFLINE),
                rankId: GetRankOfGuild(record.GuildId),
                enrollmentDate: 0);
        }

        public AllianceInsiderInfoMessage BuildInsiderInfoMessage()
        {
            var allMembers = new List<AllianceMemberInfo>();
            foreach (var guild in GetGuilds())
            {
                foreach (var memberRecord in guild.Record.Members)
                {
                    var character = guild.GetOnlineMember(memberRecord.CharacterId);
                    if (character != null)
                        allMembers.Add(BuildMemberInfo(character));
                    else
                    {
                        var rec = CharacterRecord.GetCharacterRecord(memberRecord.CharacterId);
                        if (rec != null) allMembers.Add(BuildMemberInfo(rec));
                    }
                }
            }

            return new AllianceInsiderInfoMessage(
                GetAllianceFactSheet(),
                allMembers.ToArray(),
                new PrismGeolocalizedInformation[0],
                new TaxCollectorInformations[0]);
        }

        public AllianceFactsMessage BuildAllianceFactsMessage()
        {
            var members = new List<CharacterMinimalSocialPublicInformations>();
            foreach (var guild in GetGuilds())
            {
                foreach (var member in guild.Record.Members)
                {
                    var rec = CharacterRecord.GetCharacterRecord(member.CharacterId);
                    if (rec == null) continue;
                    members.Add(new CharacterMinimalSocialPublicInformations
                    {
                        id = rec.Id,
                        name = rec.Name,
                        level = ExperienceManager.Instance.GetCharacterLevel(rec.Experience),
                        rank = new RankPublicInformation(0, 0, 0, ""),
                    });
                }
            }

            var leaderRec = CharacterRecord.GetCharacterRecord(Record.LeaderCharacterId);

            return new AllianceFactsMessage(
                infos: GetAllianceFactSheet(),
                members: members.ToArray(),
                controlledSubareaIds: new short[0],
                leaderCharacterId: Record.LeaderCharacterId,
                leaderCharacterName: leaderRec?.Name ?? "");
        }

        // -----------------------------------------------------------------
        // Broadcast helpers
        // -----------------------------------------------------------------

        public void Send(NetworkMessage message)
        {
            foreach (var character in OnlineMembers.Values)
                character.Client.Send(message);
        }

        public void BroadcastTextInformation(TextInformationTypeEnum type, short msgId, params object[] parameters)
        {
            foreach (var character in OnlineMembers.Values)
                character.TextInformation(type, msgId, parameters);
        }

        public Character GetOnlineMember(long characterId) => OnlineMembers.TryGetValue(characterId, out var c) ? c : null;
        public IEnumerable<Character> GetOnlineMembers() => OnlineMembers.Values;
    }
}
