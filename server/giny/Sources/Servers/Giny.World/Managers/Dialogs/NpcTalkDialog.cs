using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.World.Managers.Criterias;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Entities.Npcs;
using Giny.World.Managers.Generic;
using Giny.World.Records.Npcs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Giny.World.Managers.Dialogs
{
    public class NpcTalkDialog : Dialog
    {
        public override DialogTypeEnum DialogType
        {
            get
            {
                return DialogTypeEnum.DIALOG_DIALOG;
            }
        }

        public Npc Npc
        {
            get;
            private set;
        }

        private NpcActionRecord Action
        {
            get;
            set;
        }

        private int MessageId
        {
            get;
            set;
        }

        private IEnumerable<NpcReplyRecord> Replies
        {
            get;
            set;
        }

        public NpcTalkDialog(Character character, Npc npc, NpcActionRecord action)
            : base(character)
        {
            this.Npc = npc;
            this.Action = action;
            this.MessageId = int.Parse(Action.Param1);
            this.Replies = GetValidReply(NpcReplyRecord.GetNpcReplies(npc.SpawnRecord.Id, this.MessageId)).ToArray();
        }
        public override void Open()
        {
            this.Character.Client.Send(new NpcDialogCreationMessage(Npc.SpawnRecord.MapId, (int)Npc.Id));
            this.DialogQuestion();
        }
        public void DialogQuestion()
        {
            var replies = Replies.Select(x => (int)x.ReplyId).ToList();
            // OneAir : ajoute la reply "Reprendre <donjon> où vous l'avez quittée."
            // quand le NPC est à l'entrée d'un donjon et que le joueur a une
            // progression sauvegardée. Ces replyIds sont whitelistés dans le
            // d2o du template NPC (cf. OneAirDungeonResume.ManualOverrides /
            // OneAirDungeonResumeData), donc le client les rend correctement à
            // côté des autres replies vanilla.
            replies.AddRange(Giny.World.Managers.Dungeons.OneAirDungeonResume.GetExtraRepliesForNpcTalk(Character, Npc));
            Character.Client.Send(new NpcDialogQuestionMessage(MessageId, new string[] { "0" }, replies.Distinct().ToArray()));
        }
        public override void Close()
        {
            base.Close();
            LeaveDialogMessage();
        }
        public void Reply(int replyId)
        {
            // OneAir : intercepte la reply "Reprendre" avant le routage vanilla.
            if (Giny.World.Managers.Dungeons.OneAirDungeonResume.TryHandleExtraReply(Character, Npc, replyId))
            {
                this.Close();
                return;
            }

            IEnumerable<NpcReplyRecord> replies = Replies.Where(x => x.ReplyId == replyId);

            if (!replies.Any(x => x.ActionIdentifier == GenericActionEnum.ContinueDialog))
            {
                this.Close();
            }

            foreach (var reply in replies)
            {
                if (reply != null && reply.ActionIdentifier != GenericActionEnum.None)
                {
                    GenericActionsManager.Instance.Handle(Character, reply);
                }
            }

        }
        private IEnumerable<NpcReplyRecord> GetValidReply(IEnumerable<NpcReplyRecord> replies)
        {
            return replies.Where(entry => CriteriaExpression.Eval(entry.Criteria, Character.Client));

        }


    }
}
