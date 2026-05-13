using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Giny.Core;
using Giny.Core.IO.Configuration;
using Giny.Core.Network.Messages;
using Giny.Protocol.Custom.Enums;
using Giny.Protocol.Enums;
using Giny.Protocol.Messages;
using Giny.Protocol.Types;
using Giny.World.Managers.Dialogs;
using Giny.World.Managers.Entities.Characters;
using Giny.World.Managers.Entities.Npcs;
using Giny.World.Managers.Experiences;
using Giny.World.Managers.Maps.Elements;
using Giny.World.Managers.Maps.Npcs;
using Giny.World.Managers.Maps;
using Giny.World.Managers.Maps.Teleporters;
using Giny.World.Managers.Generic;
using Giny.World.Network;
using Giny.ORM;
using Giny.World.Records.Maps;
using Giny.World.Records.Npcs;
using MySql.Data.MySqlClient;

namespace Giny.World.Managers.HavenBag
{
    public static class OneAirHavenBagPatch
    {
        // Extrait de HavenbagThemes.d2o (2.68). Chaque thème = map distincte,
        // donc "changer de thème" = téléporter sur la map du nouveau thème.
        public static readonly Dictionary<byte, long> ThemeToMapId = new Dictionary<byte, long>
        {
            {  1, 162791424 }, {  2, 162793472 }, {  3, 162795520 }, {  4, 162791426 },
            {  5, 162791428 }, {  6, 162795522 }, {  7, 162795524 }, {  9, 162793474 },
            { 10, 162791430 }, { 11, 162793478 }, { 12, 162795526 }, { 14, 162791432 },
            { 15, 162793480 }, { 16, 162795528 }, { 17, 162791434 }, { 18, 162791436 },
            { 19, 162793484 }, { 20, 162795532 }, { 21, 162791438 }, { 23, 162793482 },
            { 24, 162795530 }, { 25, 162793486 }, { 26, 162791440 }, { 27, 162795534 },
            { 28, 162793488 }, { 29, 162795536 }, { 30, 162791442 }, { 31, 162793490 },
            { 32, 162795538 }, { 33, 162791444 }, { 34, 162793492 }, { 51, 162791448 },
            { 52, 162793496 }, { 53, 162795544 }, { 54, 162791450 }, { 55, 162793498 },
            { 56, 162795546 }, { 57, 162791452 }, { 58, 162793500 },
        };

        public const byte DefaultTheme = 1; // Kerubim
        public static long DefaultHavenBagMapId => ThemeToMapId[DefaultTheme];

        public static readonly HashSet<long> HavenBagMapIds = new HashSet<long>(ThemeToMapId.Values);

        public static bool IsHavenBagMap(long mapId) => HavenBagMapIds.Contains(mapId);

        public static long GetMapIdForTheme(byte theme)
        {
            if (theme == 0) theme = DefaultTheme;
            return ThemeToMapId.TryGetValue(theme, out var mapId) ? mapId : DefaultHavenBagMapId;
        }

        private static readonly TimeSpan LoteryCooldown = TimeSpan.FromHours(24);

        // BonesId → type d'interactive havre-sac. Hydraté par LoadInteractiveBindings.
        private static readonly Dictionary<int, string> _interactiveBonesToType = new Dictionary<int, string>();
        private static readonly object _interactivesLock = new object();

        private static volatile bool _schemaReady = false;
        private static readonly object _schemaLock = new object();

        public static void EnsureSchema()
        {
            if (_schemaReady) return;
            lock (_schemaLock)
            {
                if (_schemaReady) return;
                try
                {
                    using var c = OpenConn();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS havenbag_state (
    CharacterId BIGINT NOT NULL PRIMARY KEY,
    PreviousMapId BIGINT NULL,
    PreviousCellId SMALLINT NULL,
    Theme TINYINT UNSIGNED NOT NULL DEFAULT 0,
    RoomId TINYINT UNSIGNED NOT NULL DEFAULT 0,
    LastLoteryAt DATETIME NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS known_zaaps (
    CharacterId BIGINT NOT NULL,
    MapId BIGINT NOT NULL,
    DiscoveredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (CharacterId, MapId),
    KEY ix_known_zaaps_char (CharacterId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS havenbag_furnitures (
    CharacterId BIGINT NOT NULL,
    ThemeId TINYINT UNSIGNED NOT NULL DEFAULT 1,
    CellId SMALLINT NOT NULL,
    FurnitureId INT NOT NULL,
    Orientation TINYINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (CharacterId, ThemeId, CellId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS havenbag_interactives (
    BonesId INT NOT NULL PRIMARY KEY,
    Type VARCHAR(16) NOT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;";
                    cmd.ExecuteNonQuery();

                    // Migrations idempotentes (SafeAlter = try/catch silencieux).
                    SafeAlter(c, "ALTER TABLE havenbag_state ADD COLUMN LastLoteryAt DATETIME NULL");
                    SafeAlter(c, "ALTER TABLE havenbag_furnitures ADD COLUMN ThemeId TINYINT UNSIGNED NOT NULL DEFAULT 1 AFTER CharacterId");
                    SafeAlter(c, "ALTER TABLE havenbag_furnitures DROP PRIMARY KEY, ADD PRIMARY KEY (CharacterId, ThemeId, CellId)");

                    _schemaReady = true;
                    Logger.Write("[OneAir] HavenBag schema ready", Channels.Info);
                }
                catch (Exception e)
                {
                    Logger.Write("[OneAir] HavenBag schema init failed: " + e.Message, Channels.Warning);
                }
            }
        }

        // Interactives havre-sac : on bypass le système interactive_skills
        // vanilla en interceptant InteractiveUseRequestMessage côté handler.
        public static void LoadInteractiveBindings()
        {
            lock (_interactivesLock)
            {
                _interactiveBonesToType.Clear();
                try
                {
                    using var c = OpenConn();
                    using var sel = c.CreateCommand();
                    sel.CommandText = "SELECT BonesId, Type FROM havenbag_interactives";
                    using var r = sel.ExecuteReader();
                    while (r.Read())
                    {
                        _interactiveBonesToType[r.GetInt32(0)] = r.GetString(1);
                    }
                }
                catch (Exception e) { Logger.Write("[OneAir] LoadInteractiveBindings failed: " + e.Message, Channels.Warning); }

                // Auto-register du zaap : élément 502556 sur la map Kerubim.
                if (!_interactiveBonesToType.ContainsValue("zaap"))
                {
                    try
                    {
                        var keruMap = MapRecord.GetMap(162791424);
                        if (keruMap != null)
                        {
                            var zaapElem = keruMap.Elements.FirstOrDefault(e => e.Identifier == 502556)
                                         ?? keruMap.Elements.FirstOrDefault(e => e.Skill != null && e.Skill.ActionIdentifier == GenericActionEnum.Zaap);
                            // Certaines maps stockent la signature dans GfxId au lieu de BonesId.
                            int signature = 0;
                            if (zaapElem != null) signature = zaapElem.BonesId > 0 ? zaapElem.BonesId : zaapElem.GfxId;
                            if (zaapElem != null && signature > 0)
                            {
                                SaveInteractiveBinding(signature, "zaap");
                                Logger.Write($"[OneAir] Auto-registered haven bag zaap (signature={signature})", Channels.Info);
                            }
                            else
                            {
                                Logger.Write("[OneAir] Zaap auto-register: zaap element not found on Kerubim", Channels.Warning);
                            }
                        }
                        else
                        {
                            Logger.Write("[OneAir] Zaap auto-register: Kerubim map not loaded", Channels.Warning);
                        }
                    }
                    catch (Exception e) { Logger.Write("[OneAir] Zaap auto-register failed: " + e.Message, Channels.Warning); }
                }
                else
                {
                    Logger.Write($"[OneAir] Loaded {_interactiveBonesToType.Count} interactive binding(s)", Channels.Info);
                }
            }

            EnsureInteractiveSkillsForBindings();

            // bones=3507 = étoile au sol "sortir d'un bâtiment" (binding global).
            EnsureGlobalBinding(3507, "exit");

            EnsureExitInteractiveSkillsAsync();
        }

        // ActionIdentifier=Teleport (et non Zaap/Use) parce que :
        //  - le client 2.68 affiche le curseur "porte" au survol,
        //  - le client path sur la cellule de l'élément (cell-trigger), pas
        //    une cellule adjacente,
        //  - Character.EndMove auto-déclenche UseInteractive à l'arrivée
        //    (Character.cs ~1027), → GenericActions.HandleTeleportAction →
        //    notre hook OneAir → TryExitBuilding.
        // Param1/Param2 doivent rester numériques sinon GenericActions crashe
        // sur int.Parse. Async pour ne pas bloquer le boot.
        public static void EnsureExitInteractiveSkillsAsync()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    int seen = 0, created = 0, failed = 0, noOutdoor = 0;
                    foreach (var map in MapRecord.GetMaps())
                    {
                        if (map?.Elements == null) continue;
                        if (map.Position == null) continue;

                        MapRecord outdoor = null;
                        foreach (var sib in MapRecord.GetMaps(map.Position.Point))
                        {
                            if (sib == null || sib.Id == map.Id) continue;
                            if (outdoor == null) outdoor = sib;
                            if (sib.Position != null && sib.Position.Outdoor) { outdoor = sib; break; }
                        }

                        foreach (var elem in map.Elements)
                        {
                            if (elem.BonesId != 3507) continue;
                            seen++;
                            if (elem.Skill != null) continue;

                            // Pas d'outdoor : on pose quand même le skill (le
                            // curseur doit s'afficher) ; TryExitBuilding gère
                            // le fallback SpawnPoint.
                            long destMap = outdoor?.Id ?? map.Id;
                            short destCell = outdoor != null
                                ? FindEntranceCellOrRandom(outdoor, map.Id)
                                : (short)0;
                            if (outdoor == null) noOutdoor++;

                            try
                            {
                                // POINT_OUT_AN_EXIT282 + POINT_OUT_AN_EXIT339 : le
                                // client reconnaît la combinaison et applique le
                                // pathfind cell-trigger + curseur "porte".
                                bool ok = MapsManager.Instance.AddInteractiveSkill(
                                    map,
                                    elem.Identifier,
                                    GenericActionEnum.Teleport,
                                    InteractiveTypeEnum.POINT_OUT_AN_EXIT282,
                                    SkillTypeEnum.POINT_OUT_AN_EXIT339,
                                    destMap.ToString(),
                                    destCell.ToString(),
                                    null);
                                if (ok) created++;
                                else failed++;
                            }
                            catch (Exception e)
                            {
                                failed++;
                                Logger.Write($"[OneAir] AddInteractiveSkill exit on {map.Id}/{elem.Identifier}: {e.Message}", Channels.Warning);
                            }
                        }
                    }
                    Logger.Write($"[OneAir] Exit interactives: {seen} seen, {created} created, {failed} failed, {noOutdoor} without outdoor sibling", Channels.Info);
                }
                catch (Exception e)
                {
                    Logger.Write("[OneAir] EnsureExitInteractiveSkillsAsync failed: " + e.Message, Channels.Warning);
                }
            });
        }

        // Hook qui remplace `character.Teleport(int.Parse(parameter.Param1), cellId);`
        // dans GenericActions.HandleTeleportAction. Pour bones=3507 ("exit"),
        // on délègue à TryExitBuilding (recalcul à chaud).
        public static void HandleTeleportInteraction(Character character, MapElement element, int targetMapId, short cellId, bool hasCell)
        {
            try
            {
                int sig = element?.Record != null
                    ? (element.Record.BonesId > 0 ? element.Record.BonesId : element.Record.GfxId)
                    : 0;
                string globalType;
                lock (_interactivesLock) { _interactiveBonesToType.TryGetValue(sig, out globalType); }
                if (globalType == "exit")
                {
                    TryExitBuilding(character);
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] HandleTeleportInteraction failed: " + e.Message, Channels.Warning);
            }
            if (hasCell) character.Teleport((long)targetMapId, cellId);
            else character.Teleport((long)targetMapId);
        }

        // Binding global (hors havre-sac). Ne déclenche PAS EnsureInteractive
        // Skills : on n'irait pas modifier toutes les maps du jeu.
        private static void EnsureGlobalBinding(int bonesId, string type)
        {
            lock (_interactivesLock)
            {
                if (_interactiveBonesToType.ContainsKey(bonesId)) return;
                _interactiveBonesToType[bonesId] = type;
            }
            try
            {
                using var c = OpenConn();
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO havenbag_interactives (BonesId, Type) VALUES (@b, @t) ON DUPLICATE KEY UPDATE Type=VALUES(Type)";
                ins.Parameters.AddWithValue("@b", bonesId);
                ins.Parameters.AddWithValue("@t", type);
                ins.ExecuteNonQuery();
                Logger.Write($"[OneAir] Global interactive binding: bones={bonesId} → {type}", Channels.Info);
            }
            catch (Exception e) { Logger.Write("[OneAir] EnsureGlobalBinding failed: " + e.Message, Channels.Warning); }
        }

        public static void SaveInteractiveBinding(int bonesId, string type)
        {
            lock (_interactivesLock)
            {
                _interactiveBonesToType[bonesId] = type;
            }
            try
            {
                using var c = OpenConn();
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO havenbag_interactives (BonesId, Type) VALUES (@b, @t) ON DUPLICATE KEY UPDATE Type=VALUES(Type)";
                ins.Parameters.AddWithValue("@b", bonesId);
                ins.Parameters.AddWithValue("@t", type);
                ins.ExecuteNonQuery();
            }
            catch (Exception e) { Logger.Write("[OneAir] SaveInteractiveBinding failed: " + e.Message, Channels.Warning); }

            EnsureInteractiveSkillsForBindings();
        }

        private static bool IsExpectedTypeForBinding(string binding, InteractiveTypeEnum t)
        {
            return binding switch
            {
                "chest"  => t == InteractiveTypeEnum.BANK168,
                "lotery" => t == InteractiveTypeEnum.SLOT_MACHINE373,
                "zaap"   => t == InteractiveTypeEnum.ZAAP16,
                _        => false,
            };
        }

        // Scanne chaque map havre-sac et ajoute une interactive_skills row
        // sur les éléments matchant un binding (par BonesId/GfxId). Sans ça
        // le client ne reçoit pas l'élément dans MapComplementaryInformations
        // et il n'est pas cliquable. ActionIdentifier=Zaap → routé via
        // HandleZaapInteraction qui dispatche par signature.
        public static void EnsureInteractiveSkillsForBindings()
        {
            try
            {
                int created = 0;
                lock (_interactivesLock)
                {
                    if (_interactiveBonesToType.Count == 0) return;

                    foreach (var bagMapId in HavenBagMapIds)
                    {
                        var map = MapRecord.GetMap(bagMapId);
                        if (map == null) continue;

                        foreach (var elem in map.Elements)
                        {
                            int sig = elem.BonesId > 0 ? elem.BonesId : elem.GfxId;
                            if (sig <= 0) continue;
                            if (!_interactiveBonesToType.ContainsKey(sig)) continue;
                            if (elem.Skill != null
                                && elem.Skill.ActionIdentifier == GenericActionEnum.Zaap
                                && IsExpectedTypeForBinding(_interactiveBonesToType[sig], elem.Skill.Type))
                            {
                                continue;
                            }

                            // Tooltip cohérent côté client (Banque/Machine à sous/Zaap).
                            var bindingType = _interactiveBonesToType[sig];
                            InteractiveTypeEnum interactiveType = bindingType switch
                            {
                                "chest"  => InteractiveTypeEnum.BANK168,
                                "lotery" => InteractiveTypeEnum.SLOT_MACHINE373,
                                "zaap"   => InteractiveTypeEnum.ZAAP16,
                                _        => InteractiveTypeEnum.ZAAP16,
                            };

                            try
                            {
                                bool ok = MapsManager.Instance.AddInteractiveSkill(
                                    map,
                                    elem.Identifier,
                                    GenericActionEnum.Zaap,
                                    interactiveType,
                                    SkillTypeEnum.USE114,
                                    "0", null, null);
                                if (ok) created++;
                            }
                            catch (Exception e) { Logger.Write($"[OneAir] AddInteractiveSkill on {bagMapId}/{elem.Identifier}: {e.Message}", Channels.Warning); }
                        }
                    }
                }
                if (created > 0)
                    Logger.Write($"[OneAir] Created {created} interactive_skills row(s) for haven bag bindings", Channels.Info);
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] EnsureInteractiveSkillsForBindings failed: " + e.Message, Channels.Warning);
            }
        }

        // Intercepteur depuis InteractivesHandler.HandleInteractiveUse :
        // retourne true si OneAir a géré (vanilla skip).
        public static bool TryHandleInteractive(Character character, int elemId)
        {
            try
            {
                if (character?.Map == null) return false;

                var elem = character.Map.GetElementRecord(elemId);
                if (elem == null) return false;
                int signature = elem.BonesId > 0 ? elem.BonesId : elem.GfxId;

                string type;
                lock (_interactivesLock)
                {
                    if (!_interactiveBonesToType.TryGetValue(signature, out type)) return false;
                }

                bool inHavenBag = IsHavenBagMap(character.Map.Id);

                switch (type)
                {
                    case "zaap":
                        if (!inHavenBag) return false;
                        OpenHavenBagZaap(character);
                        return true;
                    case "chest":
                        if (!inHavenBag) return false;
                        if (character.Busy) return true;
                        character.OpenBank();
                        return true;
                    case "lotery":
                        if (!inHavenBag) return false;
                        if (character.Busy) return true;
                        HandleLoteryRequest(character);
                        return true;

                    case "exit":
                        if (inHavenBag) return false;
                        if (character.Busy) return true;
                        return TryExitBuilding(character);
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] TryHandleInteractive failed: " + e.Message, Channels.Warning);
            }
            return false;
        }

        // Hook MovementConfirm : déclenche TryExitBuilding si le joueur
        // arrive pile sur une étoile "exit" (bones=3507).
        public static void OnMovementConfirmed(Character character)
        {
            try
            {
                if (character?.Map?.Elements == null) return;
                if (IsHavenBagMap(character.Map.Id)) return;

                short cellId = character.Record.CellId;
                foreach (var elem in character.Map.Elements)
                {
                    if (elem.CellId != cellId) continue;
                    int sig = elem.BonesId > 0 ? elem.BonesId : elem.GfxId;
                    if (sig <= 0) continue;

                    string type;
                    lock (_interactivesLock)
                    {
                        if (!_interactiveBonesToType.TryGetValue(sig, out type)) continue;
                    }

                    if (type == "exit")
                    {
                        TryExitBuilding(character);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] OnMovementConfirmed failed: " + e.Message, Channels.Warning);
            }
        }

        // Pattern Dofus indoor→outdoor : intérieur et extérieur partagent le
        // même Position.Point (mêmes coords world x,y) mais sont 2 MapRecord
        // distincts, l'outdoor ayant Position.Outdoor=true. C'est ce que fait
        // `.relative` côté admin. Les champs TopMap/BottomMap/etc. sont les
        // voisins worldmap (nord/sud/est/ouest) et ne pointent PAS vers
        // l'extérieur du bâtiment — d'où l'historique "Map de sortie
        // introuvable" sur des ids type 58465797.
        private static bool TryExitBuilding(Character character)
        {
            try
            {
                var current = character.Map;
                if (current?.Position == null)
                {
                    character.ReplyWarning("Position de la map courante inconnue.");
                    return true;
                }

                MapRecord targetMap = null;

                // 1. Sibling Outdoor au même Point.world.
                var siblings = MapRecord.GetMaps(current.Position.Point)
                    .Where(m => m != null && m.Id != current.Id)
                    .ToList();
                targetMap = siblings.FirstOrDefault(m => m.Position != null && m.Position.Outdoor)
                            ?? siblings.FirstOrDefault();

                // 2. Voisin worldmap (uniquement si MapRecord chargé, sinon
                //    on retombe sur "introuvable").
                if (targetMap == null)
                {
                    foreach (long candidate in new long[] { current.TopMap, current.BottomMap, current.LeftMap, current.RightMap })
                    {
                        if (candidate <= 0) continue;
                        var m = MapRecord.GetMap(candidate);
                        if (m == null) continue;
                        targetMap = m;
                        break;
                    }
                }

                if (targetMap == null && character.Record.SpawnPointMapId > 0)
                {
                    targetMap = MapRecord.GetMap(character.Record.SpawnPointMapId);
                }

                if (targetMap == null)
                {
                    character.ReplyWarning("Aucune map de sortie disponible pour ce bâtiment.");
                    return true;
                }

                short cellId = FindEntranceCellOrRandom(targetMap, current.Id);
                character.Teleport((int)targetMap.Id, cellId);
                return true;
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] TryExitBuilding failed: " + e.Message, Channels.Warning);
                return false;
            }
        }

        // Sur la map extérieure, trouve la "porte" (élément Teleport dont
        // Param1=indoorMapId) et renvoie une cellule walkable adjacente
        // (sud-ouest préféré, comme MapRecord.GetNearCell).
        private static short FindEntranceCellOrRandom(MapRecord outdoor, long indoorMapId)
        {
            try
            {
                string indoorIdStr = indoorMapId.ToString();
                var doorElem = outdoor.Elements.FirstOrDefault(e =>
                    e.Skill != null
                    && e.Skill.ActionIdentifier == GenericActionEnum.Teleport
                    && (e.Skill.Param1 ?? string.Empty).Trim() == indoorIdStr);

                if (doorElem != null)
                {
                    // Param2 = cellId d'arrivée *à l'intérieur* (cas entrée).
                    // Pour la sortie on veut une cellule près de la porte
                    // sur la map extérieure → sud-ouest puis voisin walkable.
                    var doorPoint = doorElem.Point;
                    if (doorPoint != null)
                    {
                        var sw = doorPoint.GetCellInDirection(DirectionsEnum.DIRECTION_SOUTH_WEST, 1);
                        if (sw != null && outdoor.IsCellWalkable(sw.CellId))
                            return sw.CellId;

                        var near = doorPoint.GetNearPoints();
                        if (near != null)
                        {
                            var w = near.FirstOrDefault(p => p != null && outdoor.IsCellWalkable(p.CellId));
                            if (w != null) return w.CellId;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] FindEntranceCellOrRandom failed: " + e.Message, Channels.Warning);
            }
            return outdoor.RandomWalkableCell().Id;
        }

        // Le binding s'applique ensuite à TOUS les havres-sacs (matching par
        // BonesId), donc une config sur un seul thème propage aux 38 autres.
        public static void RegisterInteractive(Character character, string type, int elemId)
        {
            if (character?.Map == null || !IsHavenBagMap(character.Map.Id))
            {
                character.ReplyError("Commande utilisable uniquement dans le havre-sac.");
                return;
            }
            if (type != "chest" && type != "lotery" && type != "zaap")
            {
                character.ReplyError("Type invalide. Utilise chest / lotery / zaap.");
                return;
            }

            var elem = character.Map.GetElementRecord(elemId);
            if (elem == null)
            {
                character.ReplyError("Élément " + elemId + " introuvable sur cette map. Tape .elems pour la liste.");
                return;
            }
            int signature = elem.BonesId > 0 ? elem.BonesId : elem.GfxId;
            if (signature <= 0)
            {
                character.ReplyError("Élément " + elemId + " sans BonesId ni GfxId, impossible à binder.");
                return;
            }

            SaveInteractiveBinding(signature, type);
            character.Reply($"Binding enregistré : tous les éléments de signature {signature} (bones/gfx) → '{type}' sur les 39 maps havre-sac.");
        }

        public static void ListElements(Character character)
        {
            if (character?.Map == null) return;
            var sb = new StringBuilder();
            sb.Append("<b>Éléments interactifs de la map ").Append(character.Map.Id).Append("</b>\n");
            foreach (var e in character.Map.Elements)
            {
                string vanilla = e.Skill != null ? e.Skill.ActionIdentifier.ToString() : "-";
                string oneair;
                lock (_interactivesLock)
                {
                    oneair = _interactiveBonesToType.TryGetValue(e.BonesId, out var t) ? t : "-";
                }
                sb.Append("Id=").Append(e.Identifier)
                  .Append(" cell=").Append(e.CellId)
                  .Append(" bones=").Append(e.BonesId)
                  .Append(" gfx=").Append(e.GfxId)
                  .Append(" vanilla=").Append(vanilla)
                  .Append(" oneair=").Append(oneair)
                  .Append("\n");
            }
            character.Reply(sb.ToString());
        }

        // Évite l'écran noir au reconnect : le client 2.68 n'active pas
        // correctement le contexte havre-sac quand il y arrive directement
        // sans avoir envoyé EnterHavenBagRequestMessage. On rewrite Record.MapId
        // sur la position pré-bag sauvegardée et on laisse le Teleport vanilla
        // s'exécuter avec les valeurs mises à jour.
        public static bool RedirectIfHavenBagSpawn(Character character)
        {
            try
            {
                if (character?.Record == null) return false;
                if (!IsHavenBagMap(character.Record.MapId)) return false;

                var (prevMap, prevCell) = LoadPreviousPosition(character.Id);
                long mapId = prevMap > 0 ? prevMap : character.Record.SpawnPointMapId;
                short cellId = prevCell > 0 ? prevCell : (short)0;
                if (mapId == 0) return false;

                character.Record.MapId = mapId;
                character.Record.CellId = cellId;
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] RedirectIfHavenBagSpawn failed: " + e.Message, Channels.Warning);
            }
            return false;
        }

        private static void SafeAlter(MySqlConnection c, string sql)
        {
            try
            {
                using var alter = c.CreateCommand();
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            }
            catch { }
        }

        private static MySqlConnection OpenConn()
        {
            var cfg = ConfigManager<WorldConfig>.Instance;
            var cs = $"Server={cfg.SQLHost};Database={cfg.SQLDBName};Uid={cfg.SQLUser};" +
                     $"Pwd={cfg.SQLPassword};AllowPublicKeyRetrieval=true;SslMode=None;Pooling=true;";
            var c = new MySqlConnection(cs);
            c.Open();
            return c;
        }

        public static void EnterHavenBag(Character character, long havenBagOwner)
        {
            EnsureSchema();
            try
            {
                if (character.Map == null)
                {
                    character.ReplyError("Impossible d'entrer dans le havre-sac maintenant.");
                    return;
                }

                // Le SWF envoie EnterHavenBagRequestMessage à chaque pression H,
                // même si on est déjà dans le bag : c'est au serveur de
                // basculer en mode sortie.
                if (IsHavenBagMap(character.Map.Id))
                {
                    ExitHavenBag(character);
                    return;
                }

                SavePreviousPosition(character.Id, character.Map.Id, character.CellId);

                var (theme, _) = LoadThemeAndRoom(character.Id);
                var targetMapId = GetMapIdForTheme(theme);
                var targetMap = MapRecord.GetMap(targetMapId);
                if (targetMap == null)
                {
                    character.ReplyError("Map du havre-sac introuvable (theme " + theme + ", id " + targetMapId + ").");
                    return;
                }

                // MapComplementary upgrade automatique via MaybeUpgradeToHavenBag.
                // SendHavenBagInit part de OnAfterEnterMap.
                character.Teleport(targetMap, GetSpawnCell(targetMap));
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] EnterHavenBag failed: " + e.Message, Channels.Warning);
                character.ReplyError("Erreur havre-sac : " + e.Message);
            }
        }

        // Hook depuis Character.OnEnterMap après SendMapComplementary.
        // Sans la séquence d'init (furnitures/packs/zaaps/room) la map reste
        // noire et le binding 'H = exit' n'est pas armé.
        public static void OnAfterEnterMap(Character character)
        {
            try
            {
                if (character?.Map == null || !IsHavenBagMap(character.Map.Id)) return;
                SendHavenBagInit(character);
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] OnAfterEnterMap failed: " + e.Message, Channels.Warning);
            }
        }

        public static void SendHavenBagInit(Character character)
        {
            try
            {
                byte theme = ResolveCurrentTheme(character);
                var (_, roomId) = LoadThemeAndRoom(character.Id);

                // Meubles indexés PAR thème : ne suivent pas quand on change.
                var furnitures = LoadFurnitures(character.Id, theme);
                character.Client.Send(new HavenBagFurnituresMessage(furnitures));

                var packIds = ThemeToMapId.Keys.ToArray();
                character.Client.Send(new HavenBagPackListMessage(packIds));

                character.Client.Send(new HavenBagRoomUpdateMessage(0,
                    new HavenBagRoomPreviewInformation[] { new HavenBagRoomPreviewInformation(roomId, theme) }));

                // Zaaps connus + tous les zaaps du monde en fallback (comme SendKnownZaapList vanilla).
                var known = LoadKnownZaaps(character.Id);
                var allZaaps = TeleportersManager.Instance.GetMaps(TeleporterTypeEnum.TELEPORTER_ZAAP);
                foreach (var m in allZaaps)
                {
                    if (!known.Contains(m)) known.Add(m);
                }
                character.Client.Send(new KnownZaapListMessage(known.Select(x => (double)x).ToArray()));
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] SendHavenBagInit failed: " + e.Message, Channels.Warning);
            }
        }

        public static void ExitHavenBag(Character character)
        {
            EnsureSchema();
            try
            {
                if (character.Map == null || !IsHavenBagMap(character.Map.Id))
                {
                    return;
                }

                var (prevMap, prevCell) = LoadPreviousPosition(character.Id);

                long fallbackMap = character.Record.SpawnPointMapId;
                long mapId = prevMap > 0 ? prevMap : fallbackMap;
                short? cellId = prevCell > 0 ? (short?)prevCell : null;

                if (mapId == 0)
                {
                    character.ReplyError("Aucune position de retour. Téléporte-toi manuellement.");
                    return;
                }

                var targetMap = MapRecord.GetMap(mapId);
                if (targetMap == null)
                {
                    character.ReplyError("Map de retour introuvable (id " + mapId + ").");
                    return;
                }

                ClearPreviousPosition(character.Id);
                character.Teleport(targetMap, cellId);
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] ExitHavenBag failed: " + e.Message, Channels.Warning);
                character.ReplyError("Erreur sortie havre-sac : " + e.Message);
            }
        }

        // Hook qui remplace character.OpenZaap(element) dans GenericActions.HandleZaap.
        // Sur une map havre-sac : dispatch par signature (zaap/chest/lotery).
        // Hors havre-sac : mémorise le zaap dans known_zaaps puis dialog vanilla.
        public static void HandleZaapInteraction(Character character, MapElement element)
        {
            bool handledOnHavenBag = false;
            try
            {
                EnsureSchema();
                long mapId = character.Map?.Id ?? 0;

                if (IsHavenBagMap(mapId))
                {
                    handledOnHavenBag = true;
                    // Tous nos éléments bindés (zaap/chest/lotery) ont
                    // ActionIdentifier=Zaap : dispatch selon signature.
                    int sig = element?.Record != null
                        ? (element.Record.BonesId > 0 ? element.Record.BonesId : element.Record.GfxId)
                        : 0;
                    string type;
                    lock (_interactivesLock)
                    {
                        _interactiveBonesToType.TryGetValue(sig, out type);
                    }
                    switch (type)
                    {
                        case "chest":
                            if (!character.Busy) character.OpenBank();
                            return;
                        case "lotery":
                            if (!character.Busy) HandleLoteryRequest(character);
                            return;
                        case "zaap":
                        default:
                            OpenHavenBagZaap(character);
                            return;
                    }
                }

                // Bindings globaux (rare : les étoiles bones=3507 passent par
                // OnMovementConfirmed, mais on garde le routage de secours).
                int sig2 = element?.Record != null
                    ? (element.Record.BonesId > 0 ? element.Record.BonesId : element.Record.GfxId)
                    : 0;
                string globalType;
                lock (_interactivesLock) { _interactiveBonesToType.TryGetValue(sig2, out globalType); }
                if (globalType == "exit")
                {
                    TryExitBuilding(character);
                    return;
                }

                if (mapId > 0)
                {
                    AddKnownZaap(character.Id, mapId);
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] HandleZaapInteraction failed: " + e.Message, Channels.Warning);
            }
            finally
            {
                // Sans InteractiveUseEndedMessage le client maintient le verrou
                // skill (SKILL_DURATION=35 deciseconds) et bloque les clics suivants.
                if (handledOnHavenBag && element?.Record?.Skill != null)
                {
                    try
                    {
                        character.Client.Send(new InteractiveUseEndedMessage(
                            element.Record.Identifier,
                            (short)element.Record.Skill.SkillId));
                    }
                    catch (Exception e2) { Logger.Write("[OneAir] InteractiveUseEnded failed: " + e2.Message, Channels.Warning); }
                }
            }

            if (!handledOnHavenBag)
            {
                character.OpenZaap(element);
            }
        }

        private static void OpenHavenBagZaap(Character character)
        {
            var dialog = new OneAirHavenBagZaapDialog(character);
            character.OpenDialog(dialog);
        }

        public static void StartEdit(Character character)
        {
            if (character.Map == null || !IsHavenBagMap(character.Map.Id))
            {
                character.ReplyWarning("Personnalisation accessible uniquement dans le havre-sac.");
                return;
            }
            character.Client.Send(new EditHavenBagStartMessage());
        }

        public static void CancelEdit(Character character)
        {
            character.Client.Send(new EditHavenBagFinishedMessage());
        }

        // Début séquence Save : nettoie les meubles du thème courant pour
        // pouvoir les réinsérer via les HavenBagFurnituresRequest qui suivent.
        public static void OpenFurnitureSequence(Character character)
        {
            EnsureSchema();
            try
            {
                if (character.Map == null || !IsHavenBagMap(character.Map.Id)) return;
                byte theme = ResolveCurrentTheme(character);

                using var c = OpenConn();
                using var del = c.CreateCommand();
                del.CommandText = "DELETE FROM havenbag_furnitures WHERE CharacterId=@cid AND ThemeId=@t";
                del.Parameters.AddWithValue("@cid", character.Id);
                del.Parameters.AddWithValue("@t", theme);
                del.ExecuteNonQuery();
            }
            catch (Exception e) { Logger.Write("[OneAir] OpenFurnitureSequence failed: " + e.Message, Channels.Warning); }
        }

        // UN SEUL EditHavenBagFinishedMessage par save (HavenbagFrame l'attend).
        public static void FinishEdit(Character character)
        {
            try
            {
                if (character?.Map != null && IsHavenBagMap(character.Map.Id))
                {
                    byte theme = ResolveCurrentTheme(character);
                    var infos = LoadFurnitures(character.Id, theme);
                    character.Client.Send(new HavenBagFurnituresMessage(infos));
                }
            }
            catch (Exception e) { Logger.Write("[OneAir] FinishEdit echo failed: " + e.Message, Channels.Warning); }

            character.Client.Send(new EditHavenBagFinishedMessage());
        }

        // Insertion incrémentale (le client splitte via MAX_FURNITURES_PER_PACKET).
        // Ne renvoie RIEN — l'echo + EditHavenBagFinished se font dans FinishEdit.
        public static void SaveFurnitures(Character character, short[] cellIds, int[] furnitureIds, byte[] orientations)
        {
            EnsureSchema();
            try
            {
                if (character.Map == null || !IsHavenBagMap(character.Map.Id)) return;
                byte theme = ResolveCurrentTheme(character);

                int n = Math.Min(cellIds?.Length ?? 0, Math.Min(furnitureIds?.Length ?? 0, orientations?.Length ?? 0));
                if (n == 0) return;

                using var c = OpenConn();
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO havenbag_furnitures (CharacterId, ThemeId, CellId, FurnitureId, Orientation) VALUES (@cid, @t, @c, @f, @o) ON DUPLICATE KEY UPDATE FurnitureId=VALUES(FurnitureId), Orientation=VALUES(Orientation)";
                var pCid = ins.Parameters.Add("@cid", MySqlDbType.Int64);
                var pT = ins.Parameters.Add("@t", MySqlDbType.UByte);
                var pC = ins.Parameters.Add("@c", MySqlDbType.Int16);
                var pF = ins.Parameters.Add("@f", MySqlDbType.Int32);
                var pO = ins.Parameters.Add("@o", MySqlDbType.UByte);
                for (int i = 0; i < n; i++)
                {
                    pCid.Value = character.Id;
                    pT.Value = theme;
                    pC.Value = cellIds[i];
                    pF.Value = furnitureIds[i];
                    pO.Value = orientations[i];
                    ins.ExecuteNonQuery();
                }
            }
            catch (Exception e) { Logger.Write("[OneAir] SaveFurnitures failed: " + e.Message, Channels.Warning); }
        }

        private static byte ResolveCurrentTheme(Character character)
        {
            // Theme = map actuelle (chaque thème a sa propre map).
            if (character?.Map != null)
            {
                foreach (var kv in ThemeToMapId)
                {
                    if (kv.Value == character.Map.Id) return kv.Key;
                }
            }
            return LoadThemeAndRoom(character.Id).theme;
        }

        // V1 : une seule room (id 0).
        public static void ChangeRoom(Character character, byte roomId)
        {
            EnsureSchema();
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO havenbag_state (CharacterId, RoomId) VALUES (@cid, @r) ON DUPLICATE KEY UPDATE RoomId=VALUES(RoomId)";
                cmd.Parameters.AddWithValue("@cid", character.Id);
                cmd.Parameters.AddWithValue("@r", (byte)0);
                cmd.ExecuteNonQuery();
            }
            catch (Exception e) { Logger.Write("[OneAir] ChangeRoom failed: " + e.Message, Channels.Warning); }

            character.Client.Send(new HavenBagRoomUpdateMessage((byte)0,
                new HavenBagRoomPreviewInformation[] { new HavenBagRoomPreviewInformation(0, GetTheme(character.Id)) }));
        }

        public static void ChangeTheme(Character character, byte theme)
        {
            EnsureSchema();
            try
            {
                if (theme == 0) theme = DefaultTheme;
                if (!ThemeToMapId.ContainsKey(theme))
                {
                    character.ReplyWarning("Thème inconnu : " + theme);
                    return;
                }

                using (var c = OpenConn())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO havenbag_state (CharacterId, Theme) VALUES (@cid, @t) ON DUPLICATE KEY UPDATE Theme=VALUES(Theme)";
                    cmd.Parameters.AddWithValue("@cid", character.Id);
                    cmd.Parameters.AddWithValue("@t", theme);
                    cmd.ExecuteNonQuery();
                }

                long newMapId = GetMapIdForTheme(theme);
                if (character.Map != null && character.Map.Id == newMapId)
                {
                    character.Map.Instance.SendMapComplementary(character.Client);
                    SendHavenBagInit(character);
                    return;
                }

                var newMap = MapRecord.GetMap(newMapId);
                if (newMap == null)
                {
                    character.ReplyError("Map du thème " + theme + " introuvable (id " + newMapId + ").");
                    return;
                }

                character.Teleport(newMap, GetSpawnCell(newMap));
            }
            catch (Exception e) { Logger.Write("[OneAir] ChangeTheme failed: " + e.Message, Channels.Warning); }
        }

        public static bool TryHandleExchangeRequest(Character character, byte exchangeType)
        {
            if ((ExchangeTypeEnum)exchangeType != ExchangeTypeEnum.HAVENBAG) return false;

            if (character.Map == null || !IsHavenBagMap(character.Map.Id))
            {
                character.ReplyWarning("Coffre accessible uniquement dans le havre-sac.");
                return true;
            }

            // V1 : coffre = inventaire banque vanilla.
            character.OpenBank();
            return true;
        }

        public static void HandleLoteryRequest(Character character)
        {
            EnsureSchema();
            try
            {
                using var c = OpenConn();
                DateTime? last = null;
                using (var sel = c.CreateCommand())
                {
                    sel.CommandText = "SELECT LastLoteryAt FROM havenbag_state WHERE CharacterId=@cid";
                    sel.Parameters.AddWithValue("@cid", character.Id);
                    var r = sel.ExecuteScalar();
                    if (r != null && r != DBNull.Value) last = (DateTime)r;
                }

                if (last.HasValue && DateTime.UtcNow - last.Value < LoteryCooldown)
                {
                    var remaining = LoteryCooldown - (DateTime.UtcNow - last.Value);
                    character.ReplyWarning($"Loterie déjà jouée. Reviens dans {remaining.Hours}h{remaining.Minutes:D2}.");
                    return;
                }

                long reward = 100;
                character.AddKamas(reward);
                character.OnKamasGained(reward);

                using (var ins = c.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO havenbag_state (CharacterId, LastLoteryAt) VALUES (@cid, UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE LastLoteryAt=UTC_TIMESTAMP()";
                    ins.Parameters.AddWithValue("@cid", character.Id);
                    ins.ExecuteNonQuery();
                }

                // Pas de HavenBagDailyLoteryMessage : le SWF tenterait un
                // appel HAAPI Ankama (HavenbagFrame ~391) qui échoue sur notre
                // serveur privé. Feedback chat suffit.
                character.Reply("Loterie : +" + reward + " kamas.");
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] Lotery failed: " + e.Message, Channels.Warning);
                character.ReplyError("Erreur loterie : " + e.Message);
            }
        }

        private static void SavePreviousPosition(long charId, long mapId, short cellId)
        {
            EnsureSchema();
            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO havenbag_state (CharacterId, PreviousMapId, PreviousCellId) VALUES (@cid, @m, @ce) ON DUPLICATE KEY UPDATE PreviousMapId=VALUES(PreviousMapId), PreviousCellId=VALUES(PreviousCellId)";
            cmd.Parameters.AddWithValue("@cid", charId);
            cmd.Parameters.AddWithValue("@m", mapId);
            cmd.Parameters.AddWithValue("@ce", cellId);
            cmd.ExecuteNonQuery();
        }

        private static (long mapId, short cellId) LoadPreviousPosition(long charId)
        {
            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT PreviousMapId, PreviousCellId FROM havenbag_state WHERE CharacterId=@cid";
            cmd.Parameters.AddWithValue("@cid", charId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                long mid = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                short cid = r.IsDBNull(1) ? (short)0 : r.GetInt16(1);
                return (mid, cid);
            }
            return (0, 0);
        }

        private static void ClearPreviousPosition(long charId)
        {
            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE havenbag_state SET PreviousMapId=NULL, PreviousCellId=NULL WHERE CharacterId=@cid";
            cmd.Parameters.AddWithValue("@cid", charId);
            cmd.ExecuteNonQuery();
        }

        private static (byte theme, byte roomId) LoadThemeAndRoom(long charId)
        {
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Theme, RoomId FROM havenbag_state WHERE CharacterId=@cid";
                cmd.Parameters.AddWithValue("@cid", charId);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    byte theme = (byte)r.GetInt32(0);
                    byte room = (byte)r.GetInt32(1);
                    if (theme == 0) theme = 1; // Kerubim par défaut, theme=0 = noir/invalide
                    return (theme, room);
                }
            }
            catch { }
            return (1, 0); // défaut Kerubim
        }

        private static byte GetTheme(long charId) => LoadThemeAndRoom(charId).theme;

        public static void AddKnownZaap(long charId, long mapId)
        {
            EnsureSchema();
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT IGNORE INTO known_zaaps (CharacterId, MapId) VALUES (@cid, @m)";
                cmd.Parameters.AddWithValue("@cid", charId);
                cmd.Parameters.AddWithValue("@m", mapId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] AddKnownZaap failed: " + e.Message, Channels.Warning);
            }
        }

        public static List<long> LoadKnownZaaps(long charId)
        {
            var result = new List<long>();
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT MapId FROM known_zaaps WHERE CharacterId=@cid ORDER BY DiscoveredAt";
                cmd.Parameters.AddWithValue("@cid", charId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) result.Add(r.GetInt64(0));
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] LoadKnownZaaps failed: " + e.Message, Channels.Warning);
            }
            return result;
        }

        private static HavenBagFurnitureInformation[] LoadFurnitures(long charId, byte themeId)
        {
            var list = new List<HavenBagFurnitureInformation>();
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT CellId, FurnitureId, Orientation FROM havenbag_furnitures WHERE CharacterId=@cid AND ThemeId=@t";
                cmd.Parameters.AddWithValue("@cid", charId);
                cmd.Parameters.AddWithValue("@t", themeId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    list.Add(new HavenBagFurnitureInformation((short)r.GetInt32(0), r.GetInt32(1), (byte)r.GetInt32(2)));
                }
            }
            catch { }
            return list.ToArray();
        }

        // Hook depuis ClassicMapInstance.GetMapComplementaryInformationsDataMessage :
        // transforme le message standard en variante "in haven bag".
        public static MapComplementaryInformationsDataMessage MaybeUpgradeToHavenBag(
            Character character, MapComplementaryInformationsDataMessage msg)
        {
            try
            {
                if (character?.Map == null || !IsHavenBagMap(character.Map.Id)) return msg;

                var (theme, roomId) = LoadThemeAndRoom(character.Id);
                short level = (short)ExperienceManager.Instance.GetCharacterLevel(character.Record.Experience);
                var owner = new CharacterMinimalInformations(level, character.Id, character.Name);

                return new MapComplementaryInformationsDataInHavenBagMessage(
                    owner, theme, roomId, /*maxRoomId*/ 1,
                    msg.subAreaId, msg.mapId, msg.houses, msg.actors,
                    msg.interactiveElements, msg.statedElements, msg.obstacles,
                    msg.fights, msg.hasAggressiveMonsters, msg.fightStartPositions);
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] MaybeUpgradeToHavenBag failed: " + e.Message, Channels.Warning);
                return msg;
            }
        }

        // Cellule sud-ouest du zaap si présent (joueur arrive devant le zaap),
        // sinon random walkable.
        private static short GetSpawnCell(MapRecord map)
        {
            try
            {
                var zaap = map.GetFirstElementRecord(InteractiveTypeEnum.ZAAP16);
                if (zaap != null)
                {
                    return map.GetNearCell(InteractiveTypeEnum.ZAAP16);
                }
                return map.RandomWalkableCell().Id;
            }
            catch { return 0; }
        }
    }

    // Dialog zaap havre-sac : liste = known_zaaps (alimentée par
    // HandleZaapInteraction). Fallback SpawnPointMapId si vide.
    public class OneAirHavenBagZaapDialog : TeleporterDialog
    {
        public override TeleporterTypeEnum TeleporterType => TeleporterTypeEnum.TELEPORTER_ZAAP;

        public OneAirHavenBagZaapDialog(Character character) : base(character)
        {
            var knownMapIds = OneAirHavenBagPatch.LoadKnownZaaps(character.Id);

            Destinations = new Dictionary<long, TeleportDestination>();
            foreach (var mapId in knownMapIds)
            {
                if (OneAirHavenBagPatch.IsHavenBagMap(mapId)) continue;
                var map = MapRecord.GetMap(mapId);
                if (map == null) continue;
                Destinations[map.Id] = new TeleportDestination()
                {
                    cost = 0,
                    level = 1,
                    type = (byte)TeleporterType,
                    mapId = map.Id,
                    subAreaId = map.SubareaId,
                };
            }

            if (Destinations.Count == 0 && character.Record.SpawnPointMapId > 0)
            {
                var spawnMap = MapRecord.GetMap(character.Record.SpawnPointMapId);
                if (spawnMap != null && !OneAirHavenBagPatch.IsHavenBagMap(spawnMap.Id))
                {
                    Destinations[spawnMap.Id] = new TeleportDestination()
                    {
                        cost = 0, level = 1,
                        type = (byte)TeleporterType,
                        mapId = spawnMap.Id,
                        subAreaId = spawnMap.SubareaId,
                    };
                }
            }
        }

        public override void Open()
        {
            this.Character.Client.Send(new ZaapDestinationsMessage(
                Character.Record.SpawnPointMapId,
                (byte)TeleporterType,
                Destinations.Values.ToArray()));
        }

        public override void Teleport(MapRecord map)
        {
            if (map == null || !Destinations.ContainsKey(map.Id)) return;

            short cellId;
            var zaapElement = map.GetFirstElementRecord(InteractiveTypeEnum.ZAAP16);
            if (zaapElement != null)
            {
                cellId = map.GetNearCell(InteractiveTypeEnum.ZAAP16);
            }
            else
            {
                cellId = map.RandomWalkableCell().Id;
            }

            this.Close();
            this.Character.Teleport(map.Id, cellId);
        }

        public override short GetCost(MapRecord teleporterMap, MapRecord currentMap) => 0;
    }
}
