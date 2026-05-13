// Régénération PV hors combat — comportement du vrai Dofus 2.x :
//  - 1 HP/s debout (regenRate = 10, soit 10 × 100 ms / HP)
//  - 2 HP/s assis (regenRate = 5)
// Côté protocole : on envoie LifePointsRegenBeginMessage(rate) une fois, le
// client anime visuellement la barre de PV, puis on commit avec
// LifePointsRegenEndMessage (qui hérite de UpdateLifePointsMessage) à la fin
// (PV pleins) ou sur changement de rythme.
//
// L'état "assis" est traqué via les emotes 1 (S'asseoir / AnimEmoteSit_0) et
// 19 (Se reposer / AnimEmoteRest_0). Tout autre emote ou tout début de
// déplacement casse l'état assis et fait redescendre à 1 HP/s.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Giny.Core;
using Giny.Protocol.Messages;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Network;

namespace Giny.World.Managers.Regen
{
    public static class OneAirRegenManager
    {
        private const int IntervalMs = 1000;

        // regenRate côté protocole = decisecondes par HP (10 = 1 HP/s, 5 = 2 HP/s).
        private const byte StandingRate = 10;
        private const byte SittingRate = 5;
        private const int StandingHpPerTick = 1;
        private const int SittingHpPerTick = 2;

        // Emote IDs qui déclenchent le boost (D2O Emoticons.part00.json) :
        //   1  : AnimEmoteSit_0  (S'asseoir, persistancy=true)
        //   19 : AnimEmoteRest_0 (Se reposer / Allongé)
        private static readonly HashSet<short> SitEmoteIds = new HashSet<short> { 1, 19 };

        private class State
        {
            public bool IsSitting;
            public bool IsRegenerating;
            public byte ActiveRate;
            public int Gained;
        }

        private static readonly Dictionary<long, State> _states = new Dictionary<long, State>();
        private static readonly object _lock = new object();
        private static volatile bool _running = false;

        public static void Start()
        {
            if (_running) return;
            _running = true;
            Task.Run(LoopAsync);
            Logger.Write("[OneAir] Regen manager started (standing=" + StandingHpPerTick + " HP/s, sitting=" + SittingHpPerTick + " HP/s)", Channels.Info);
        }

        public static void Stop() { _running = false; }

        public static void OnEmotePlayed(Character character, short emoteId)
        {
            if (character == null) return;
            try
            {
                var state = GetOrCreate(character.Id);
                state.IsSitting = SitEmoteIds.Contains(emoteId);
            }
            catch (Exception e) { Logger.Write("[OneAir] regen OnEmote: " + e.Message, Channels.Warning); }
        }

        public static void OnMovementStarted(Character character)
        {
            if (character == null) return;
            try
            {
                var state = GetOrCreate(character.Id);
                state.IsSitting = false;
            }
            catch (Exception e) { Logger.Write("[OneAir] regen OnMove: " + e.Message, Channels.Warning); }
        }

        private static State GetOrCreate(long charId)
        {
            lock (_lock)
            {
                if (!_states.TryGetValue(charId, out var s))
                {
                    s = new State();
                    _states[charId] = s;
                }
                return s;
            }
        }

        private static async Task LoopAsync()
        {
            while (_running)
            {
                try { Tick(); }
                catch (Exception e) { Logger.Write("[OneAir] regen tick: " + e.Message, Channels.Warning); }
                await Task.Delay(IntervalMs);
            }
        }

        private static void Tick()
        {
            var online = WorldServer.Instance.GetOnlineClients().ToList();
            var seenIds = new HashSet<long>();

            foreach (var client in online)
            {
                Character ch = null;
                try { ch = client?.Character; } catch { }
                if (ch == null) continue;
                seenIds.Add(ch.Id);

                try { TickCharacter(ch); }
                catch (Exception e) { Logger.Write("[OneAir] regen tick(" + ch.Id + "): " + e.Message, Channels.Warning); }
            }

            // Nettoyage des entrées orphelines (déconnexions).
            lock (_lock)
            {
                var toRemove = _states.Keys.Where(k => !seenIds.Contains(k)).ToList();
                foreach (var k in toRemove) _states.Remove(k);
            }
        }

        private static void TickCharacter(Character ch)
        {
            var state = GetOrCreate(ch.Id);

            bool canRegen = !ch.Fighting && !ch.IsDead() && !ch.ChangeMap;
            int missing = ch.Stats.MaxLifePoints - ch.Stats.LifePoints;

            if (!canRegen || missing <= 0)
            {
                if (state.IsRegenerating) EndRegen(ch, state);
                return;
            }

            byte desiredRate = state.IsSitting ? SittingRate : StandingRate;
            int hpPerTick = state.IsSitting ? SittingHpPerTick : StandingHpPerTick;

            // Si le rythme change (assis → debout ou inverse) on commit puis on relance.
            if (state.IsRegenerating && state.ActiveRate != desiredRate)
            {
                EndRegen(ch, state);
            }

            if (!state.IsRegenerating)
            {
                BeginRegen(ch, state, desiredRate);
            }

            int gain = Math.Min(hpPerTick, missing);
            if (gain <= 0) return;

            try { ch.Stats.Life.Current = ch.Stats.LifePoints + gain; }
            catch (Exception e) { Logger.Write("[OneAir] regen apply hp: " + e.Message, Channels.Warning); return; }
            state.Gained += gain;

            // PV pleins → on commit immédiatement (sinon la barre du client reste figée
            // à la valeur animée jusqu'au prochain UpdateLifePoints).
            if (ch.Stats.LifePoints >= ch.Stats.MaxLifePoints)
            {
                EndRegen(ch, state);
            }
        }

        private static void BeginRegen(Character ch, State state, byte rate)
        {
            try
            {
                ch.Client.Send(new LifePointsRegenBeginMessage(rate));
                state.IsRegenerating = true;
                state.ActiveRate = rate;
                state.Gained = 0;
            }
            catch (Exception e) { Logger.Write("[OneAir] regen Begin: " + e.Message, Channels.Warning); }
        }

        private static void EndRegen(Character ch, State state)
        {
            try
            {
                ch.Client.Send(new LifePointsRegenEndMessage(state.Gained, ch.Stats.LifePoints, ch.Stats.MaxLifePoints));
            }
            catch (Exception e) { Logger.Write("[OneAir] regen End: " + e.Message, Channels.Warning); }
            state.IsRegenerating = false;
            state.ActiveRate = 0;
            state.Gained = 0;
        }
    }
}
