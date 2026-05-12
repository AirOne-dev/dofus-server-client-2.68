// Dialog Dofus natif "Reprendre <donjon> où vous l'avez quittée."
//
// On utilise le système dialogue Dofus standard (NpcDialogCreationMessage +
// NpcDialogQuestionMessage). Le SWF rend le texte à partir des IDs envoyés ;
// le mapping {mapId → list of {dungeon, npcTemplateId, questionMsgId, enterReplyId,
// resumeReplyId, exitReplyId}} est baké dans OneAirDungeonResumeData.cs depuis
// les d2o du client (Npcs.d2o + NpcMessages.d2o + i18n_fr.d2i) avec quelques
// overrides manuels (tools/manual_overrides.json).
//
// Le client filtre les `replyId` par template NPC : on doit absolument spawner
// le bon template (cf. OneAirDungeonResume.EnsureEntranceNpcs) sinon les
// replies sont silencieusement droppées.
//
// Pour les NPCs qui donnent accès à plusieurs donjons (Bibiblop → Clos des
// Blops + Antre du Blop Multicolore Royal), on compose les replies de toutes
// les entrées concernées.

using System.Collections.Generic;
using System.Linq;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Chat;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Entities.Npcs;
using Giny.World.Records.Maps;

namespace Giny.World.Managers.Dialogs
{
    public class OneAirDungeonResumeDialog : Dialog
    {
        public override DialogTypeEnum DialogType => DialogTypeEnum.DIALOG_DIALOG;

        public Npc Npc { get; }
        // Une entrée par donjon accessible depuis ce NPC. Tous partagent
        // forcément le même NpcTemplateId.
        public List<OneAirDungeonResumeEntry> Entries { get; }
        // dungeonId -> savedRoomMapId (rempli depuis la BDD au moment d'ouvrir).
        public Dictionary<long, long> SavedRoomByDungeon { get; }

        public OneAirDungeonResumeDialog(Character character, Npc npc, List<OneAirDungeonResumeEntry> entries, Dictionary<long, long> savedRoomByDungeon)
            : base(character)
        {
            Npc = npc;
            Entries = entries;
            SavedRoomByDungeon = savedRoomByDungeon;
        }

        public override void Open()
        {
            Character.Client.Send(new NpcDialogCreationMessage(Npc.SpawnRecord.MapId, (int)Npc.Id));

            var replies = new List<int>();
            foreach (var e in Entries)
            {
                if (e.EnterReplyId > 0) replies.Add(e.EnterReplyId);
                if (e.ResumeReplyId > 0 && SavedRoomByDungeon.ContainsKey(e.DungeonId))
                    replies.Add(e.ResumeReplyId);
            }
            // Exit replies : déduplique. Les NPCs qui handle plusieurs donjons
            // ont souvent le même exitReplyId ; au cas où ils diffèrent, on
            // envoie le premier non-zéro de la liste.
            int exitReply = Entries.Select(e => e.ExitReplyId).FirstOrDefault(x => x > 0);
            if (exitReply > 0) replies.Add(exitReply);

            // Question : prend la première messageId disponible.
            int msgId = Entries.Select(e => e.QuestionMessageId).FirstOrDefault(x => x > 0);

            Character.Client.Send(new NpcDialogQuestionMessage(
                msgId,
                new[] { "0" },
                replies.Distinct().ToArray()));
        }

        public void Reply(int replyId)
        {
            // Tout choix ferme le dialog.
            this.Close();

            foreach (var e in Entries)
            {
                if (replyId == e.ResumeReplyId && SavedRoomByDungeon.TryGetValue(e.DungeonId, out var savedRoom))
                {
                    if (MapRecord.GetMap(savedRoom) != null)
                    {
                        Character.Teleport(savedRoom);
                        return;
                    }
                    // Map disparue côté Giny : retombe sur la 1ère salle plutôt
                    // que de laisser le joueur coincé.
                    EnterFirstRoomOf(e);
                    return;
                }
                if (replyId == e.EnterReplyId)
                {
                    EnterFirstRoomOf(e);
                    return;
                }
            }
            // ExitReplyId ou inconnu : on ne fait rien (le joueur reste sur
            // la map d'entrée).
        }

        private void EnterFirstRoomOf(OneAirDungeonResumeEntry entry)
        {
            var dungeon = DungeonRecord.GetDungeonRecords()
                .FirstOrDefault(d => d.Id == entry.DungeonId);
            if (dungeon == null || dungeon.Rooms == null || dungeon.Rooms.Count == 0)
            {
                Character.ReplyError("Aucune salle configurée pour ce donjon.");
                return;
            }
            long firstRoom = dungeon.Rooms[0].MapId;
            Character.Teleport(firstRoom);
        }
    }
}
