using Giny.Protocol.Types;
using ProtoBuf;

namespace Giny.World.Records.Guilds
{
    // OneAir : persistance des paramètres de recrutement de guilde
    // (onglet "Recrutement" de l'UI ingame). Stocké en blob sur GuildRecord.
    [ProtoContract]
    public class GuildRecruitmentRecord
    {
        [ProtoMember(1)] public byte RecruitmentType { get; set; }
        [ProtoMember(2)] public string Title { get; set; }
        [ProtoMember(3)] public string Text { get; set; }
        [ProtoMember(4)] public int[] SelectedLanguages { get; set; }
        [ProtoMember(5)] public int[] SelectedCriterion { get; set; }
        [ProtoMember(6)] public short MinLevel { get; set; }
        [ProtoMember(7)] public bool MinLevelFacultative { get; set; }
        [ProtoMember(8)] public int MinSuccess { get; set; }
        [ProtoMember(9)] public bool MinSuccessFacultative { get; set; }
        [ProtoMember(10)] public string LastEditPlayerName { get; set; }
        [ProtoMember(11)] public double LastEditDate { get; set; }
        [ProtoMember(12)] public bool RecruitmentAutoLocked { get; set; }

        public GuildRecruitmentRecord()
        {
            Title = string.Empty;
            Text = string.Empty;
            SelectedLanguages = new int[0];
            SelectedCriterion = new int[0];
            LastEditPlayerName = string.Empty;
        }

        public static GuildRecruitmentRecord FromProtocol(GuildRecruitmentInformation info, string editorName, double editDate)
        {
            return new GuildRecruitmentRecord
            {
                RecruitmentType = info.recruitmentType,
                Title = info.recruitmentTitle ?? "",
                Text = info.recruitmentText ?? "",
                SelectedLanguages = info.selectedLanguages ?? new int[0],
                SelectedCriterion = info.selectedCriterion ?? new int[0],
                MinLevel = info.minLevel,
                MinLevelFacultative = info.minLevelFacultative,
                MinSuccess = info.minSuccess,
                MinSuccessFacultative = info.minSuccessFacultative,
                LastEditPlayerName = editorName ?? "",
                LastEditDate = editDate,
                RecruitmentAutoLocked = info.recruitmentAutoLocked,
            };
        }

        public GuildRecruitmentInformation ToProtocol(int guildId)
        {
            return new GuildRecruitmentInformation
            {
                socialId = guildId,
                recruitmentType = RecruitmentType,
                recruitmentTitle = Title ?? "",
                recruitmentText = Text ?? "",
                selectedLanguages = SelectedLanguages ?? new int[0],
                selectedCriterion = SelectedCriterion ?? new int[0],
                minLevel = MinLevel,
                minLevelFacultative = MinLevelFacultative,
                minSuccess = MinSuccess,
                minSuccessFacultative = MinSuccessFacultative,
                invalidatedByModeration = false,
                lastEditPlayerName = LastEditPlayerName ?? "",
                lastEditDate = LastEditDate,
                recruitmentAutoLocked = RecruitmentAutoLocked,
            };
        }
    }
}
