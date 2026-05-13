using ProtoBuf;
using System;

namespace Giny.World.Records.Alliances
{
    // OneAir : une alliance contient des guildes (pas des personnages).
    // On garde l'historique d'enrôlement + la guilde fondatrice.
    [ProtoContract]
    public class AllianceGuildLinkRecord
    {
        [ProtoMember(1)] public long GuildId { get; set; }
        [ProtoMember(2)] public DateTime EnrollmentDate { get; set; }
        [ProtoMember(3)] public bool IsFounder { get; set; }

        public AllianceGuildLinkRecord() { }

        public AllianceGuildLinkRecord(long guildId, bool isFounder)
        {
            GuildId = guildId;
            EnrollmentDate = DateTime.Now;
            IsFounder = isFounder;
        }
    }
}
