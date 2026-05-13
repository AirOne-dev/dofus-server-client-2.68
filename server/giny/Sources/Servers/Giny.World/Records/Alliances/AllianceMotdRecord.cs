using ProtoBuf;

namespace Giny.World.Records.Alliances
{
    [ProtoContract]
    public class AllianceMotdRecord
    {
        [ProtoMember(1)] public string Content { get; set; }
        [ProtoMember(2)] public int Timestamp { get; set; }
        [ProtoMember(3)] public long MemberId { get; set; }
        [ProtoMember(4)] public string MemberName { get; set; }

        public AllianceMotdRecord()
        {
            Content = string.Empty;
            MemberName = string.Empty;
        }
    }
}
