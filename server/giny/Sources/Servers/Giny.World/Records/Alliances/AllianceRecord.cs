using Giny.ORM.Attributes;
using Giny.ORM.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Giny.World.Records.Alliances
{
    [Table("alliances")]
    public class AllianceRecord : IRecord
    {
        [Container]
        private static readonly ConcurrentDictionary<long, AllianceRecord> Alliances = new ConcurrentDictionary<long, AllianceRecord>();

        [Primary] public long Id { get; set; }

        [Update] public string Name { get; set; }

        [Update] public string Tag { get; set; }

        [Update]
        [Blob]
        public AllianceEmblemRecord Emblem { get; set; }

        public DateTime CreationDate { get; set; }

        [Update]
        [Blob]
        public AllianceMotdRecord Motd { get; set; }

        // Bulletin - chaîne libre, max 1024 char.
        [Update]
        public string Bulletin { get; set; }

        // Char leader (chef d'alliance, généralement le créateur)
        [Update]
        public long LeaderCharacterId { get; set; }

        [Update]
        [Blob]
        public List<AllianceGuildLinkRecord> Guilds { get; set; }

        public static bool NameExists(string name) => Alliances.Values.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        public static bool TagExists(string tag) => Alliances.Values.Any(x => string.Equals(x.Tag, tag, StringComparison.OrdinalIgnoreCase));
        public static bool EmblemExists(AllianceEmblemRecord emblem) => Alliances.Values.Any(x => x.Emblem != null && x.Emblem.Equals(emblem));

        public static IEnumerable<AllianceRecord> GetAlliances() => Alliances.Values;
        public static AllianceRecord GetAlliance(long id)
        {
            return Alliances.TryGetValue(id, out var rec) ? rec : null;
        }

        public AllianceGuildLinkRecord GetGuildLink(long guildId) => Guilds.FirstOrDefault(x => x.GuildId == guildId);
    }
}
