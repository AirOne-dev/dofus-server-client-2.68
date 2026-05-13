using Giny.Protocol.Types;
using ProtoBuf;

namespace Giny.World.Records.Alliances
{
    [ProtoContract]
    public class AllianceEmblemRecord
    {
        [ProtoMember(1)] public short SymbolShape { get; set; }
        [ProtoMember(2)] public int SymbolColor { get; set; }
        [ProtoMember(3)] public byte BackgroundShape { get; set; }
        [ProtoMember(4)] public int BackgroundColor { get; set; }

        public AllianceEmblemRecord() { }

        public AllianceEmblemRecord(short symbolShape, int symbolColor, byte backgroundShape, int backgroundColor)
        {
            SymbolShape = symbolShape;
            SymbolColor = symbolColor;
            BackgroundShape = backgroundShape;
            BackgroundColor = backgroundColor;
        }

        public SocialEmblem ToSocialEmblem() => new SocialEmblem
        {
            backgroundColor = BackgroundColor,
            backgroundShape = BackgroundShape,
            symbolColor = SymbolColor,
            symbolShape = SymbolShape,
        };

        public override bool Equals(object obj)
        {
            if (obj is AllianceEmblemRecord e)
                return SymbolShape == e.SymbolShape && SymbolColor == e.SymbolColor
                    && BackgroundShape == e.BackgroundShape && BackgroundColor == e.BackgroundColor;
            return false;
        }

        public override int GetHashCode() => System.HashCode.Combine(SymbolShape, SymbolColor, BackgroundShape, BackgroundColor);
    }
}
