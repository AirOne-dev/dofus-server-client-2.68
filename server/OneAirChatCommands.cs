// OneAir custom chat commands
//   .gocell <cellId>            (admin)  → teleport sur la map courante à une cellule
//   .who                        (joueur) → liste tous les joueurs connectés
//   .invlist                    (admin)  → liste les items inventaire avec UID
//   .iteminfo <uid>             (admin)  → liste les effets d'un item
//   .iteffadd <uid> <id> <val>  (admin)  → ajoute un effet entier sur l'item
//   .iteffset <uid> <idx> <val> (admin)  → modifie la valeur de l'effet à l'index
//   .iteffdel <uid> <idx>       (admin)  → supprime l'effet à l'index
//   .dj                          (joueur) → tp sur le hub donjons (4 PNJs par plage de niveau)
//   .djgo <id>                   (joueur) → tp sur l'entrée du donjon donné
//   .hbnpc <chest|lotery> <cellId>  (admin) → [LEGACY NPC] repositionne le NPC coffre/loterie sur la map havre-sac courante
//   .hbhere <chest|lotery>      (admin)  → [LEGACY NPC] place le NPC coffre/loterie sur la cellule courante du joueur
//   .cell                       (admin)  → affiche mapId et cellId actuels (pour utiliser avec .hbnpc / .gocell)
//   .elems                      (admin)  → liste les éléments interactifs (Identifier, CellId, BonesId, GfxId) de la map courante
//   .hbset <chest|lotery|zaap> <elemId>  (admin) → bind l'élément interactif au type donné, propagé à tous les havre-sacs
using Giny.Protocol.Custom.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Effects;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Experiences;
using Giny.World.Network;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Giny.World.Managers.Chat
{
    class OneAirChatCommands
    {
        [ChatCommand("gocell", ServerRoleEnum.Administrator)]
        public static void GoCellCommand(WorldClient client, short cellId)
        {
            client.Character.Teleport((long)client.Character.Map.Id, cellId);
            client.Character.Reply("Téléporté sur la cellule " + cellId);
        }

        [ChatCommand("who", ServerRoleEnum.Player)]
        public static void WhoCommand(WorldClient client)
        {
            var clients = WorldServer.Instance.GetOnlineClients().ToList();
            var sb = new StringBuilder();
            sb.Append("<b>Joueurs connectés (").Append(clients.Count).Append(")</b>\n");
            foreach (var c in clients)
            {
                sb.Append("- ").Append(c.Character.Name);
                sb.Append(" [niv ").Append(ExperienceManager.Instance.GetCharacterLevel(c.Character.Record.Experience)).Append("]\n");
            }
            client.Character.Reply(sb.ToString());
        }

        // Variante silencieuse de .who utilisée par le panneau .online :
        // n'envoie QUE le payload structuré (intercepté par le SWF), pas de
        // dump chat (le panneau l'affiche déjà à l'écran).
        [ChatCommand("online_data", ServerRoleEnum.Player)]
        public static void OnlineDataCommand(WorldClient client)
        {
            var clients = WorldServer.Instance.GetOnlineClients().ToList();
            var rows = clients.Select(c =>
                c.Character.Name + ":" +
                ExperienceManager.Instance.GetCharacterLevel(c.Character.Record.Experience) + ":" +
                c.Character.Record.BreedId + ":" +
                (c.Character.Record.Sex ? 1 : 0));
            client.Character.Reply("__ONEAIR_PLAYERS__" + string.Join("|", rows));
        }

        [ChatCommand("invlist", ServerRoleEnum.Administrator)]
        public static void InvListCommand(WorldClient client)
        {
            var items = client.Character.Inventory.GetItems().Take(60).ToArray();
            var sb = new StringBuilder();
            sb.Append("<b>Inventaire (").Append(items.Length).Append(")</b>\n");
            foreach (var i in items)
            {
                sb.Append("UID:").Append(i.UId)
                  .Append(" GID:").Append(i.GId)
                  .Append(" qty:").Append(i.Quantity)
                  .Append(" — ").Append(i.Record == null ? "?" : i.Record.Name)
                  .Append("\n");
            }
            client.Character.Reply(sb.ToString());
        }

        [ChatCommand("iteminfo", ServerRoleEnum.Administrator)]
        public static void ItemInfoCommand(WorldClient client, int uid)
        {
            var item = client.Character.Inventory.GetItem(uid);
            if (item == null) { client.Character.Reply("UID introuvable"); return; }

            var sb = new StringBuilder();
            sb.Append("<b>").Append(item.Record == null ? "?" : item.Record.Name)
              .Append(" (UID:").Append(item.UId).Append(")</b>\n");
            int idx = 0;
            foreach (var eff in item.Effects)
            {
                sb.Append("[").Append(idx++).Append("] EffectId=").Append(eff.EffectId);
                if (eff is EffectInteger) sb.Append(" Value=").Append(((EffectInteger)eff).Value);
                else if (eff is EffectDice)
                {
                    var d = (EffectDice)eff;
                    sb.Append(" Min=").Append(d.Min).Append(" Max=").Append(d.Max);
                }
                sb.Append("\n");
            }
            client.Character.Reply(sb.ToString());
        }

        [ChatCommand("iteffadd", ServerRoleEnum.Administrator)]
        public static void ItemEffectAddCommand(WorldClient client, int uid, short effectId, int value)
        {
            var item = client.Character.Inventory.GetItem(uid);
            if (item == null) { client.Character.Reply("UID introuvable"); return; }
            item.Effects.Add(new EffectInteger() { EffectId = effectId, Value = value });
            RefreshAfterChange(client, item);
            DumpInventory(client);
        }

        [ChatCommand("iteffset", ServerRoleEnum.Administrator)]
        public static void ItemEffectSetCommand(WorldClient client, int uid, int index, int value)
        {
            var item = client.Character.Inventory.GetItem(uid);
            if (item == null) { client.Character.Reply("UID introuvable"); return; }
            var effects = item.Effects.ToList();
            if (index < 0 || index >= effects.Count) { client.Character.Reply("Index invalide"); return; }
            var eff = effects[index];
            if (eff is EffectInteger) ((EffectInteger)eff).Value = value;
            else if (eff is EffectDice) ((EffectDice)eff).Min = value;
            else { client.Character.Reply("Type d'effet non supporté"); return; }
            RefreshAfterChange(client, item);
            DumpInventory(client);
        }

        [ChatCommand("iteffdel", ServerRoleEnum.Administrator)]
        public static void ItemEffectDelCommand(WorldClient client, int uid, int index)
        {
            var item = client.Character.Inventory.GetItem(uid);
            if (item == null) { client.Character.Reply("UID introuvable"); return; }
            var effects = item.Effects.ToList();
            if (index < 0 || index >= effects.Count) { client.Character.Reply("Index invalide"); return; }
            item.Effects.Remove(effects[index]);
            RefreshAfterChange(client, item);
            DumpInventory(client);
        }

        // Notifie le client + recalcule les stats si l'objet est équipé.
        // Sans ça, modifier un item équipé désynchronise tooltip ↔ stats actives.
        private static void RefreshAfterChange(WorldClient client, Giny.World.Records.Items.CharacterItemRecord item)
        {
            client.Send(new ObjectModifiedMessage(item.GetObjectItem()));
            if (item.IsEquiped())
            {
                try { client.Character.RefreshStats(); } catch { }
            }
        }

        // .itemdump → envoie un payload structuré JSON-like avec tout
        // l'inventaire (UID, GID, name, effects). Marker __ONEAIR_INV__
        // intercepté par le SWF custom pour peupler le panneau .itemui.
        [ChatCommand("itemdump", ServerRoleEnum.Administrator)]
        public static void ItemDumpCommand(WorldClient client)
        {
            DumpInventory(client);
        }

        // Item types qu'on considère équipables ou utilisables (pas une ressource).
        // Liste basée sur Giny.Protocol.Custom.Enums.ItemTypeEnum.
        private static readonly HashSet<int> EquipableOrUsableTypes = new HashSet<int>
        {
            // Armes / outils
            2, 3, 4, 5, 6, 7, 8, 19, 20, 21, 22, 99, 114,
            // Slots équipement
            1, 9, 10, 11, 16, 17, 18, 23, 74, 82,
            // Cérémoniaux
            246, 248, 251,
            // Consommables
            12, 13, 26, 28, 33, 34, 42, 43, 75, 76, 87, 206, 211, 233,
            // Familiers / montures
            18, 121, 143, 190, 250, 255, 256
        };

        private static bool IsEquipableOrUsable(Giny.World.Records.Items.CharacterItemRecord it)
        {
            if (it.IsEquiped()) return true;
            if (it.Record == null) return false;
            if (it.Record.Usable) return true;
            return EquipableOrUsableTypes.Contains((int)it.Record.TypeId);
        }

        private static void DumpInventory(WorldClient client)
        {
            // Filtre : on n'inclut pas les ressources / runes / fragments / items quête.
            // Tri : équipés en premier, puis par position, puis par UID.
            var items = client.Character.Inventory.GetItems()
                .Where(IsEquipableOrUsable)
                .OrderBy(x => x.IsEquiped() ? 0 : 1)
                .ThenBy(x => (int)x.Position)
                .ThenBy(x => x.UId)
                .Take(200)
                .ToArray();
            var sb = new StringBuilder();
            sb.Append("__ONEAIR_INV__[");
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) sb.Append(",");
                var it = items[i];
                var name = it.Record == null ? "?" : it.Record.Name;
                // Échappe les caractères JSON dangereux + caractères de contrôle
                name = JsonEscape(name);
                sb.Append("{\"uid\":").Append(it.UId)
                  .Append(",\"gid\":").Append(it.GId)
                  .Append(",\"qty\":").Append(it.Quantity)
                  .Append(",\"pos\":").Append((int)it.Position)
                  .Append(",\"eq\":").Append(it.IsEquiped() ? 1 : 0)
                  .Append(",\"name\":\"").Append(name).Append("\"")
                  .Append(",\"effects\":[");
                int j = 0;
                foreach (var eff in it.Effects)
                {
                    if (j > 0) sb.Append(",");
                    int val = 0;
                    if (eff is EffectInteger) val = ((EffectInteger)eff).Value;
                    else if (eff is EffectDice) val = ((EffectDice)eff).Min;
                    sb.Append("{\"id\":").Append(eff.EffectId)
                      .Append(",\"value\":").Append(val).Append("}");
                    j++;
                }
                sb.Append("]}");
            }
            sb.Append("]");
            client.Character.Reply(sb.ToString());
        }

        [ChatCommand("dj", ServerRoleEnum.Player)]
        public static void DjCommand(WorldClient client)
        {
            client.Character.Teleport(OneAirDungeons.HubMapId);
        }

        [ChatCommand("djgo", ServerRoleEnum.Player)]
        public static void DjGoCommand(WorldClient client, long dungeonId)
        {
            OneAirDungeons.TpToDungeon(client.Character, dungeonId);
        }

        [ChatCommand("hbnpc", ServerRoleEnum.Administrator)]
        public static void HavenBagNpcMoveCommand(WorldClient client, string type, short cellId)
        {
            OneAirHavenBagPatch.MoveHavenBagNpc(client.Character, type, cellId);
        }

        [ChatCommand("hbhere", ServerRoleEnum.Administrator)]
        public static void HavenBagNpcHereCommand(WorldClient client, string type)
        {
            OneAirHavenBagPatch.MoveHavenBagNpc(client.Character, type, client.Character.CellId);
        }

        [ChatCommand("cell", ServerRoleEnum.Administrator)]
        public static void CellCommand(WorldClient client)
        {
            var ch = client.Character;
            ch.Reply($"<b>Map</b> {ch.Map?.Id}  |  <b>Cell</b> {ch.CellId}  |  <b>Subarea</b> {ch.Map?.SubareaId}");
        }

        [ChatCommand("elems", ServerRoleEnum.Administrator)]
        public static void ElemsCommand(WorldClient client)
        {
            OneAirHavenBagPatch.ListElements(client.Character);
        }

        [ChatCommand("hbset", ServerRoleEnum.Administrator)]
        public static void HavenBagSetCommand(WorldClient client, string type, int elemId)
        {
            OneAirHavenBagPatch.RegisterInteractive(client.Character, type, elemId);
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
