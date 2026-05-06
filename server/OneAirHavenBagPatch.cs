// OneAir — implémentation complète des havres-sacs (Giny ne fournit qu'un
// teleport bête à l'entrée, pas de sortie, pas de zaap, pas de coffre, etc.).
//
// Couvert ici :
//   * Entrée : sauvegarde la position (mapId+cellId) en DB, teleporte sur la
//     map intérieure (162791424), envoie MapComplementaryInformationsDataInHavenBag
//     pour que le client active la UI havre-sac.
//   * Sortie (touche H ou bouton "Sortir") : lit la position sauvegardée et
//     teleporte le joueur dessus. Fallback sur SpawnPointMapId.
//   * Zaap intérieur : ouvre un OneAirHavenBagZaapDialog custom dont la liste
//     de destinations = tous les zaaps connus du joueur (alimenté à chaque
//     fois qu'il OUVRE un zaap normal hors havre-sac, via le hook
//     HandleZaapInteraction sed-injecté dans GenericActions).
//   * Coffre : ExchangeRequestMessage(HAVENBAG=24) ouvre la BankExchange
//     standard.
//   * Personnalisation : ack le cycle Edit/Save/Cancel + persiste meubles,
//     thème, room en DB.
//   * Loterie : répond une fois par jour (cooldown stocké dans la table state
//     via le champ UpdatedAt).
//
// La table oneair_havenbag_state contient (CharacterId, PreviousMapId,
// PreviousCellId, Theme, RoomId).
// La table oneair_known_zaaps contient (CharacterId, MapId).
// La table oneair_havenbag_furnitures contient (CharacterId, CellId,
// FurnitureId, Orientation).

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

namespace Giny.World.Managers.Chat
{
    public static class OneAirHavenBagPatch
    {
        // Mapping thème → mapId (extrait de HavenbagThemes.d2o du client 2.68).
        // Chaque thème de havre-sac est une map distincte ; le visuel
        // (floor/walls/déco) provient du .dlm de cette map. Donc changer de
        // thème = téléporter le joueur sur la map du nouveau thème.
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

        // NPC templates (legacy — gardés pour compat, mais inutiles depuis
        // qu'on intercepte directement les éléments interactifs visibles).
        public const short ChestNpcTemplateId = 2000;
        public const short LoteryNpcTemplateId = 1451;

        // Cooldown loterie : 1 par 24h.
        private static readonly TimeSpan LoteryCooldown = TimeSpan.FromHours(24);

        // Cache mémoire des bindings BonesId → type d'interactive havre-sac.
        // Populé depuis oneair_havenbag_interactives au boot et après chaque
        // .hbset. Chargé/lookup synchronisé.
        private static readonly Dictionary<int, string> _interactiveBonesToType = new Dictionary<int, string>();
        private static readonly object _interactivesLock = new object();

        private static volatile bool _npcsSpawned = false;
        private static readonly object _npcsLock = new object();

        private static volatile bool _schemaReady = false;
        private static readonly object _schemaLock = new object();

        // -------------------------------------------------------------------
        // Schema bootstrap
        // -------------------------------------------------------------------
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
CREATE TABLE IF NOT EXISTS oneair_havenbag_state (
    CharacterId BIGINT NOT NULL PRIMARY KEY,
    PreviousMapId BIGINT NULL,
    PreviousCellId SMALLINT NULL,
    Theme TINYINT UNSIGNED NOT NULL DEFAULT 0,
    RoomId TINYINT UNSIGNED NOT NULL DEFAULT 0,
    LastLoteryAt DATETIME NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS oneair_known_zaaps (
    CharacterId BIGINT NOT NULL,
    MapId BIGINT NOT NULL,
    DiscoveredAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (CharacterId, MapId),
    KEY ix_known_zaaps_char (CharacterId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS oneair_havenbag_furnitures (
    CharacterId BIGINT NOT NULL,
    ThemeId TINYINT UNSIGNED NOT NULL DEFAULT 1,
    CellId SMALLINT NOT NULL,
    FurnitureId INT NOT NULL,
    Orientation TINYINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (CharacterId, ThemeId, CellId)
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS oneair_havenbag_interactives (
    BonesId INT NOT NULL PRIMARY KEY,
    Type VARCHAR(16) NOT NULL,
    UpdatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
) ENGINE=InnoDB;";
                    cmd.ExecuteNonQuery();

                    // Migrations douces (idempotent — chaque ALTER est wrap
                    // dans un try/catch, "duplicate column" si déjà appliqué).
                    SafeAlter(c, "ALTER TABLE oneair_havenbag_state ADD COLUMN LastLoteryAt DATETIME NULL");
                    SafeAlter(c, "ALTER TABLE oneair_havenbag_furnitures ADD COLUMN ThemeId TINYINT UNSIGNED NOT NULL DEFAULT 1 AFTER CharacterId");
                    // Reconstruit la PK si elle est encore (CharacterId, CellId) sans ThemeId.
                    SafeAlter(c, "ALTER TABLE oneair_havenbag_furnitures DROP PRIMARY KEY, ADD PRIMARY KEY (CharacterId, ThemeId, CellId)");

                    _schemaReady = true;
                    Logger.Write("[OneAir] HavenBag schema ready", Channels.Info);
                }
                catch (Exception e)
                {
                    Logger.Write("[OneAir] HavenBag schema init failed: " + e.Message, Channels.Warning);
                }
            }
        }

        // -------------------------------------------------------------------
        // NPCs (coffre + loterie) sur la map du havre-sac
        // -------------------------------------------------------------------
        /// <summary>
        /// Spawn idempotent du coffre (NPC 2000) et de la loterie (NPC 1451)
        /// sur la map du havre-sac. Appelé une fois au boot du world (depuis
        /// EnsureSchema). Si les rows existent déjà en DB (boot suivant),
        /// SpawnNpcs() les a déjà chargés en runtime, on skip.
        /// </summary>
        // -------------------------------------------------------------------
        // Interactives (clic sur les éléments visibles du décor : zaap,
        // coffre, loterie). On bypass le système interactive_skills vanilla
        // en interceptant InteractiveUseRequestMessage côté handler.
        // -------------------------------------------------------------------

        /// <summary>
        /// Charge les bindings BonesId → Type depuis la DB au boot. Auto-
        /// register le zaap (BonesId trouvé via l'élément déjà skillé sur
        /// la map Kerubim) si pas encore défini.
        /// </summary>
        public static void LoadInteractiveBindings()
        {
            lock (_interactivesLock)
            {
                _interactiveBonesToType.Clear();
                try
                {
                    using var c = OpenConn();
                    using var sel = c.CreateCommand();
                    sel.CommandText = "SELECT BonesId, Type FROM oneair_havenbag_interactives";
                    using var r = sel.ExecuteReader();
                    while (r.Read())
                    {
                        _interactiveBonesToType[r.GetInt32(0)] = r.GetString(1);
                    }
                }
                catch (Exception e) { Logger.Write("[OneAir] LoadInteractiveBindings failed: " + e.Message, Channels.Warning); }

                // Auto-register du zaap si pas encore là : on cherche sur la map
                // Kerubim (162791424) l'élément 502556 (ID hardcodé dans
                // l'init SQL, c'est le zaap du havre-sac Kerubim).
                if (!_interactiveBonesToType.ContainsValue("zaap"))
                {
                    try
                    {
                        var keruMap = MapRecord.GetMap(162791424);
                        if (keruMap != null)
                        {
                            // Dump pour debug : on log tous les éléments du Kerubim
                            // pour comprendre la structure (BonesId/GfxId).
                            foreach (var e in keruMap.Elements)
                            {
                                Logger.Write($"[OneAir] Kerubim elem: Id={e.Identifier} cell={e.CellId} bones={e.BonesId} gfx={e.GfxId} skill={(e.Skill?.ActionIdentifier.ToString() ?? "null")}", Channels.Info);
                            }
                            var zaapElem = keruMap.Elements.FirstOrDefault(e => e.Identifier == 502556)
                                         ?? keruMap.Elements.FirstOrDefault(e => e.Skill != null && e.Skill.ActionIdentifier == GenericActionEnum.Zaap);
                            // Use GfxId si BonesId vaut 0 (certaines maps stockent
                            // l'info dans GfxId au lieu de BonesId).
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

            // Une fois les bindings chargés (et le zaap auto-registré),
            // s'assurer que chaque élément matchant a bien sa interactive_skills
            // → cliquable côté client.
            EnsureInteractiveSkillsForBindings();

            // Bindings de portée GLOBALE (hors havre-sac).
            // bones=3507 = étoile au sol pour quitter un bâtiment.
            EnsureGlobalBinding(3507, "exit");

            // Installe un InteractiveSkillRecord(Action=Teleport) sur chaque
            // étoile bones=3507. Pourquoi Teleport (et pas Zaap/Use) :
            //  - Le client 2.68 affiche le curseur "porte / utiliser" au survol.
            //  - Le client path SUR la cellule de l'élément (cell-trigger),
            //    pas sur une cellule adjacente, pour les éléments Teleport.
            //  - Quand le perso arrive sur la cellule, Character.EndMove()
            //    appelle automatiquement UseInteractive sur l'élément
            //    (cf. Character.cs lignes 1027-1032), ce qui déclenche
            //    GenericActions.HandleTeleportAction → notre hook OneAir
            //    (Patch 14bis) → TryExitBuilding.
            // Param1/Param2 doivent être numériques (sinon GenericActions
            // crashe sur int.Parse), donc on calcule en avance la map
            // extérieure et la cellule d'entrée. Le hook OneAir ne s'en
            // sert PAS (il appelle TryExitBuilding qui recalcule à chaud,
            // au cas où la map extérieure changerait), mais ça reste un
            // fallback propre si le hook ne fire pas.
            EnsureExitInteractiveSkillsAsync();
        }

        /// <summary>
        /// Scanne toutes les maps et installe un InteractiveSkillRecord sur
        /// chaque élément bones=3507. ActionIdentifier=Teleport pour que :
        /// (a) le curseur "porte" apparaisse au survol côté client,
        /// (b) le client path SUR la cellule (cell-trigger),
        /// (c) Character.EndMove auto-déclenche UseInteractive en arrivant.
        /// Le clic est ensuite routé via Patch 14bis (HandleTeleportAction
        /// hook) → TryExitBuilding. Idempotent : skip les éléments qui
        /// ont déjà un Skill. Async pour ne pas bloquer le boot.
        /// </summary>
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

                        // Trouve la map extérieure (sibling au même Point,
                        // Outdoor=true, sinon n'importe quelle autre sibling).
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

                            // Calcule la cellule d'entrée côté extérieur.
                            // Si pas d'outdoor → on pose quand même le skill
                            // (Param1=mapId courant, Param2=cellId courant)
                            // pour que le curseur s'affiche ; le hook
                            // TryExitBuilding gérera le fallback SpawnPoint.
                            long destMap = outdoor?.Id ?? map.Id;
                            short destCell = outdoor != null
                                ? FindEntranceCellOrRandom(outdoor, map.Id)
                                : (short)0;
                            if (outdoor == null) noOutdoor++;

                            try
                            {
                                // Type/Skill spécifiques aux étoiles de sortie en Dofus 2.x.
                                // Le client reconnaît la combinaison POINT_OUT_AN_EXIT282
                                // + POINT_OUT_AN_EXIT339 et applique le bon comportement
                                // de pathfind (sur la cellule, pas adjacent) et le bon
                                // curseur "porte/utiliser".
                                bool ok = MapsManager.Instance.AddInteractiveSkill(
                                    map,
                                    elem.Identifier,
                                    GenericActionEnum.Teleport,                  // routé via HandleTeleportAction (Patch 14bis)
                                    InteractiveTypeEnum.POINT_OUT_AN_EXIT282,    // type "exit star"
                                    SkillTypeEnum.POINT_OUT_AN_EXIT339,          // skill "exit star"
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

        /// <summary>
        /// Hook injecté par sed (Patch 14bis) à la place de
        /// `character.Teleport(int.Parse(parameter.Param1), cellId);` dans
        /// GenericActions.HandleTeleportAction. Si l'élément cliqué est une
        /// étoile de sortie (bones=3507 = binding "exit"), on délègue à
        /// TryExitBuilding (qui recalcule la map extérieure à chaud +
        /// trouve la cellule d'entrée). Sinon, comportement vanilla
        /// (téléport selon Param1/Param2 du skill).
        /// </summary>
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
            // Comportement vanilla
            if (hasCell) character.Teleport((long)targetMapId, cellId);
            else character.Teleport((long)targetMapId);
        }

        /// <summary>
        /// Persiste un binding "global" (utilisé hors havre-sac, ex: sortie
        /// de bâtiment). Idempotent : INSERT ... ON DUPLICATE KEY UPDATE
        /// + cache mémoire. Ne déclenche PAS EnsureInteractiveSkills (on
        /// n'irait pas modifier toutes les maps du jeu).
        /// </summary>
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
                ins.CommandText = "INSERT INTO oneair_havenbag_interactives (BonesId, Type) VALUES (@b, @t) ON DUPLICATE KEY UPDATE Type=VALUES(Type)";
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
                ins.CommandText = "INSERT INTO oneair_havenbag_interactives (BonesId, Type) VALUES (@b, @t) ON DUPLICATE KEY UPDATE Type=VALUES(Type)";
                ins.Parameters.AddWithValue("@b", bonesId);
                ins.Parameters.AddWithValue("@t", type);
                ins.ExecuteNonQuery();
            }
            catch (Exception e) { Logger.Write("[OneAir] SaveInteractiveBinding failed: " + e.Message, Channels.Warning); }

            // Force la création des interactive_skills pour les éléments
            // matchant ce binding sur toutes les maps havre-sac → ils
            // deviennent cliquables côté client immédiatement.
            EnsureInteractiveSkillsForBindings();
        }

        /// <summary>
        /// Pour chaque map havre-sac, scanne ses éléments interactifs et
        /// ajoute une interactive_skills row pour ceux qui matchent un
        /// binding OneAir (par BonesId/GfxId). Sans ça le client ne reçoit
        /// pas l'élément dans MapComplementaryInformations et ne le rend
        /// pas cliquable. ActionIdentifier=Zaap → vanilla GenericActions.
        /// HandleZaap → notre HandleZaapInteraction qui dispatche par signature.
        /// </summary>
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
                            // Si elem.Skill existe avec le BON Type/Action OneAir, on skip.
                            // Sinon AddInteractiveSkill update les params (Type/Action) et
                            // appelle map.Instance.Reload().
                            if (elem.Skill != null
                                && elem.Skill.ActionIdentifier == GenericActionEnum.Zaap
                                && IsExpectedTypeForBinding(_interactiveBonesToType[sig], elem.Skill.Type))
                            {
                                continue;
                            }

                            // Type d'interactive choisi selon le binding pour
                            // que le tooltip côté client soit cohérent
                            // ("Banque" / "Machine à sous" / "Zaap" au lieu
                            // de "Zaap" partout).
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
                                    GenericActionEnum.Zaap,        // routé via HandleZaapInteraction (dispatch par signature)
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

        /// <summary>
        /// Intercepteur appelé depuis InteractivesHandler.HandleInteractiveUse
        /// (via sed). Si l'élément cliqué est sur une map havre-sac et son
        /// BonesId est registré comme zaap/chest/lotery, on déclenche
        /// l'action OneAir et on retourne true (vanilla skip).
        /// </summary>
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

                // Bindings spécifiques havre-sac : on les gère uniquement quand
                // le perso EST sur une map havre-sac (chest/lotery/zaap-haven).
                bool inHavenBag = IsHavenBagMap(character.Map.Id);

                switch (type)
                {
                    case "zaap":
                        if (!inHavenBag) return false; // zaap classique → laisse vanilla / GenericActions
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

                    // Sortie de bâtiment : étoile au sol qui ramène à
                    // l'extérieur. Fonctionne sur TOUTES les maps (pas que
                    // havre-sac). On scanne les 4 voisins (Top/Bottom/Left/
                    // Right) et on téléporte sur le premier non-zéro. Pour la
                    // plupart des bâtiments Dofus, un seul des 4 est défini.
                    case "exit":
                        if (inHavenBag) return false; // havre-sac a sa propre logique de sortie
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

        /// <summary>
        /// Hook appelé à la fin de chaque déplacement RP (MovementConfirm).
        /// Si la cellule où le joueur vient d'arriver porte un élément
        /// interactif avec un binding "exit" (typiquement bones=3507),
        /// on déclenche la sortie de bâtiment.
        ///
        /// Comme aucun InteractiveSkillRecord n'est posé sur les étoiles
        /// (cf. LoadInteractiveBindings), le clic sur une étoile redevient
        /// un déplacement standard qui pose le perso PILE sur la cellule
        /// — donc une comparaison stricte CellId == cellId suffit.
        /// </summary>
        public static void OnMovementConfirmed(Character character)
        {
            try
            {
                if (character?.Map?.Elements == null) return;
                if (IsHavenBagMap(character.Map.Id)) return; // havre-sac : sa propre logique

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

        /// <summary>
        /// Sortie d'un bâtiment via l'étoile au sol (BonesId=3507). Le pattern
        /// Dofus indoor→outdoor est : la map intérieure et la map extérieure
        /// partagent le même Position.Point (mêmes coordonnées world x,y) mais
        /// sont deux MapRecord distincts, l'extérieure ayant Position.Outdoor=true.
        /// C'est ce que fait `.relative` côté admin — on réutilise la même
        /// logique ici. Les champs TopMap/BottomMap/LeftMap/RightMap des
        /// MapRecord sont les voisins worldmap (nord/sud/est/ouest) et ne
        /// pointent PAS vers l'extérieur du bâtiment — d'où le "Map de sortie
        /// introuvable" historique sur les ids du genre 58465797.
        ///
        /// Pour la cellule de destination, on cherche la PORTE D'ENTRÉE sur
        /// la map extérieure : un élément interactif avec un Skill de type
        /// Teleport dont Param1 pointe vers la map intérieure courante. La
        /// cellule sud-ouest de cette porte (ou un voisin walkable) est le
        /// "tapis d'entrée" naturel — au lieu de spawn aléatoire au milieu
        /// de la map.
        /// </summary>
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

                // 1. Maps au même Point.world que la courante (sibling indoor/outdoor).
                var siblings = MapRecord.GetMaps(current.Position.Point)
                    .Where(m => m != null && m.Id != current.Id)
                    .ToList();
                // Préférer une sibling Outdoor (extérieur du bâtiment).
                targetMap = siblings.FirstOrDefault(m => m.Position != null && m.Position.Outdoor)
                            ?? siblings.FirstOrDefault();

                // 2. Fallback : voisins worldmap, mais uniquement ceux qui
                //    correspondent à une MapRecord effectivement chargée
                //    (sinon on retombe sur l'erreur "introuvable").
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

                // 3. Fallback ultime : SpawnPoint (évite de laisser le joueur bloqué).
                if (targetMap == null && character.Record.SpawnPointMapId > 0)
                {
                    targetMap = MapRecord.GetMap(character.Record.SpawnPointMapId);
                }

                if (targetMap == null)
                {
                    character.ReplyWarning("Aucune map de sortie disponible pour ce bâtiment.");
                    return true;
                }

                // Choix de la cellule : on cible la porte d'entrée du bâtiment
                // sur la map extérieure (un élément Teleport pointant vers la
                // map intérieure courante). À défaut, cellule walkable random.
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

        /// <summary>
        /// Sur <paramref name="outdoor"/>, trouve l'élément interactif dont le
        /// Skill est un Teleport vers <paramref name="indoorMapId"/> — c'est la
        /// "porte d'entrée" du bâtiment. Retourne une cellule walkable près
        /// de la porte (sud-ouest comme `MapRecord.GetNearCell`, sinon voisin
        /// walkable, sinon random).
        /// </summary>
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
                    // Param2 = cellId de destination *sur la map intérieure*
                    // (utilisé quand on rentre). Pour SORTIR, on veut la cellule
                    // près de la porte sur la map extérieure : Point sud-ouest
                    // de l'élément, fallback voisin walkable.
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

        /// <summary>
        /// Commande admin .hbset : associe l'élément interactif <elemId> de
        /// la map courante (havre-sac) au type donné. La règle s'applique
        /// ensuite à TOUS les havres-sacs (matching par BonesId), donc une
        /// config sur Kerubim suffit pour les 38 autres thèmes.
        /// </summary>
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

        /// <summary>
        /// Commande admin .elems : liste les éléments interactifs de la map
        /// courante avec leurs identifiants, cellule, BonesId, GfxId, et
        /// l'éventuel binding OneAir / skill vanilla déjà associé.
        /// </summary>
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

        public static void EnsureHavenBagNpcs()
        {
            if (_npcsSpawned) return;
            lock (_npcsLock)
            {
                if (_npcsSpawned) return;
                try
                {
                    int spawned = 0, skipped = 0, failed = 0;

                    foreach (var bagMapId in HavenBagMapIds)
                    {
                        var map = MapRecord.GetMap(bagMapId);
                        if (map == null) { failed++; continue; }

                        bool hasChest   = map.Instance.GetEntities<Npc>().Any(n => n.Template != null && n.Template.Id == ChestNpcTemplateId);
                        bool hasLotery  = map.Instance.GetEntities<Npc>().Any(n => n.Template != null && n.Template.Id == LoteryNpcTemplateId);

                        if (!hasChest)
                        {
                            try
                            {
                                short cell = SafeWalkableCell(map);
                                NpcsManager.Instance.AddNpc((int)bagMapId, cell, DirectionsEnum.DIRECTION_SOUTH_WEST, ChestNpcTemplateId);
                                ApplyInvisibleLook(map, ChestNpcTemplateId);
                                spawned++;
                            }
                            catch (Exception e) { failed++; Logger.Write("[OneAir] chest spawn on " + bagMapId + " failed: " + e.Message, Channels.Warning); }
                        }
                        else { ApplyInvisibleLook(map, ChestNpcTemplateId); skipped++; }

                        if (!hasLotery)
                        {
                            try
                            {
                                short cell = SafeWalkableCell(map);
                                NpcsManager.Instance.AddNpc((int)bagMapId, cell, DirectionsEnum.DIRECTION_SOUTH_EAST, LoteryNpcTemplateId);
                                ApplyInvisibleLook(map, LoteryNpcTemplateId);
                                spawned++;
                            }
                            catch (Exception e) { failed++; Logger.Write("[OneAir] lotery spawn on " + bagMapId + " failed: " + e.Message, Channels.Warning); }
                        }
                        else { ApplyInvisibleLook(map, LoteryNpcTemplateId); skipped++; }
                    }

                    Logger.Write($"[OneAir] Haven bag NPCs : {spawned} spawned, {skipped} already present, {failed} failed", Channels.Info);
                    _npcsSpawned = true;
                }
                catch (Exception e)
                {
                    Logger.Write("[OneAir] EnsureHavenBagNpcs failed: " + e.Message, Channels.Warning);
                }
            }
        }

        /// <summary>
        /// Override le look du NPC pour qu'il soit visuellement invisible
        /// (scale=1 = 1% de taille, sub-pixel) tout en restant cliquable
        /// (le hitbox du sprite est conservé minimal mais existant).
        /// </summary>
        private static void ApplyInvisibleLook(MapRecord map, short templateId)
        {
            try
            {
                var npc = map.Instance.GetEntities<Npc>().FirstOrDefault(n => n.Template != null && n.Template.Id == templateId);
                if (npc == null) return;
                var origLook = npc.Look;
                var invisible = new Giny.World.Managers.Entities.Look.ServerEntityLook(
                    origLook.BonesId,
                    origLook.Skins,
                    origLook.Colors,
                    new short[] { 1 },              // scale 1% → invisible
                    new Giny.World.Managers.Entities.Look.ServerSubentityLook[0]);
                npc.Look = invisible;
            }
            catch (Exception e) { Logger.Write("[OneAir] ApplyInvisibleLook failed: " + e.Message, Channels.Warning); }
        }

        /// <summary>
        /// Bouge le NPC du coffre/loterie de la map du havre-sac courante
        /// vers la cellule indiquée. Persisté en DB via NpcSpawnRecord pour
        /// survivre au redémarrage du world.
        /// </summary>
        public static void MoveHavenBagNpc(Character character, string type, short cellId)
        {
            if (character?.Map == null || !IsHavenBagMap(character.Map.Id))
            {
                character.ReplyError("Commande utilisable uniquement dans le havre-sac.");
                return;
            }
            short templateId = type switch
            {
                "chest" or "coffre" => ChestNpcTemplateId,
                "lotery" or "loterie" => LoteryNpcTemplateId,
                _ => (short)0
            };
            if (templateId == 0)
            {
                character.ReplyError("Type inconnu. Utilise 'chest' ou 'lotery'.");
                return;
            }
            try
            {
                var npc = character.Map.Instance.GetEntities<Npc>().FirstOrDefault(n => n.Template != null && n.Template.Id == templateId);
                if (npc == null)
                {
                    character.ReplyError("NPC non trouvé sur cette map.");
                    return;
                }
                var dir = npc.SpawnRecord.Direction;
                long spawnId = npc.SpawnRecord.Id;
                NpcsManager.Instance.MoveNpc(spawnId, (int)character.Map.Id, cellId, dir);
                ApplyInvisibleLook(character.Map, templateId);
                character.Reply($"NPC {type} déplacé sur la cellule {cellId} de la map {character.Map.Id}.");
            }
            catch (Exception e)
            {
                character.ReplyError("Échec déplacement : " + e.Message);
            }
        }

        private static short SafeWalkableCell(MapRecord map)
        {
            try { return map.RandomWalkableCell().Id; }
            catch { return 0; }
        }

        /// <summary>
        /// Au login (ContextHandler.HandleGameContextCreateRequestMessage), si
        /// le Record du joueur pointe sur une map havre-sac, on remplace la
        /// position par la PreviousPosition stockée (ou le SpawnPoint en
        /// fallback). Ça évite le bug "écran noir au reconnect" : le client
        /// 2.68 a une fenêtre où il n'active pas correctement le contexte
        /// havre-sac quand il y arrive directement sans avoir envoyé
        /// EnterHavenBagRequestMessage.
        ///
        /// Retourne false (pas de prise en charge) pour que le Teleport
        /// vanilla continue de s'exécuter avec les valeurs Record mises à
        /// jour. PreviousPosition n'est pas effacée — sera écrasée au
        /// prochain .H si le joueur rentre à nouveau dans son bag.
        /// </summary>
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

        /// <summary>
        /// Intercepteur appelé depuis NpcsHandler.HandleNpcGenericActionRequestMessage
        /// (sed dans Dockerfile). Si le NPC cliqué est sur la map du havre-sac
        /// et matche un de nos templates (coffre/loterie), on déclenche
        /// l'action OneAir et on retourne true (vanilla skip). Sinon false
        /// (dispatch vanilla).
        /// </summary>
        public static bool TryHandleNpcAction(Character character, double npcId, double npcMapId)
        {
            try
            {
                if (!IsHavenBagMap((long)npcMapId)) return false;
                if (character?.Map == null || !IsHavenBagMap(character.Map.Id)) return false;

                var npc = character.Map.Instance.GetEntity<Npc>((long)npcId);
                if (npc == null || npc.Template == null) return false;

                if (npc.Template.Id == ChestNpcTemplateId)
                {
                    character.OpenBank();
                    return true;
                }
                if (npc.Template.Id == LoteryNpcTemplateId)
                {
                    HandleLoteryRequest(character);
                    return true;
                }
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] TryHandleNpcAction failed: " + e.Message, Channels.Warning);
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
            catch { /* déjà appliqué */ }
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

        // -------------------------------------------------------------------
        // Entrée / sortie
        // -------------------------------------------------------------------
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

                // Le SWF (HavenbagEnterAction → RoleplayContextFrame case 58)
                // envoie TOUJOURS EnterHavenBagRequestMessage quand H est pressée,
                // même si on est déjà dans le bag (cf. décompil DofusInvoker
                // 2.68). C'est au serveur de détecter "déjà dedans" et de
                // basculer en mode sortie.
                if (IsHavenBagMap(character.Map.Id))
                {
                    ExitHavenBag(character);
                    return;
                }

                // Sauvegarde de la position de retour.
                SavePreviousPosition(character.Id, character.Map.Id, character.CellId);

                // Map du thème courant (chaque thème a sa propre map intérieure).
                var (theme, _) = LoadThemeAndRoom(character.Id);
                var targetMapId = GetMapIdForTheme(theme);
                var targetMap = MapRecord.GetMap(targetMapId);
                if (targetMap == null)
                {
                    character.ReplyError("Map du havre-sac introuvable (theme " + theme + ", id " + targetMapId + ").");
                    return;
                }

                // L'upgrade en MapComplementaryInformationsDataInHavenBagMessage
                // se fait automatiquement via le wrapper MaybeUpgradeToHavenBag.
                // L'init (furniture, packs, zaaps, room update) part dans
                // OnAfterEnterMap après SendMapComplementary.
                character.Teleport(targetMap, GetSpawnCell(targetMap));
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] EnterHavenBag failed: " + e.Message, Channels.Warning);
                character.ReplyError("Erreur havre-sac : " + e.Message);
            }
        }

        /// <summary>
        /// Hook appelé depuis Character.OnEnterMap juste après
        /// SendMapComplementary. Si le joueur vient d'entrer dans le havre-sac,
        /// envoie la séquence d'init (furnitures, packs, zaaps, room update).
        /// Sans ces messages la map reste noire et le binding 'H = exit' n'est
        /// pas armé.
        /// </summary>
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

        /// <summary>
        /// Séquence d'initialisation de la UI havre-sac : meubles, packs,
        /// liste zaaps, room preview. Idempotent.
        /// </summary>
        public static void SendHavenBagInit(Character character)
        {
            try
            {
                byte theme = ResolveCurrentTheme(character);
                var (_, roomId) = LoadThemeAndRoom(character.Id);

                // Meubles persistés POUR CE THÈME (chaque thème a son propre
                // setup pour que les meubles ne suivent pas quand on change).
                var furnitures = LoadFurnitures(character.Id, theme);
                character.Client.Send(new HavenBagFurnituresMessage(furnitures));

                // Packs/thèmes disponibles : tous les thèmes connus côté
                // client (1..58 avec les trous d'origine).
                var packIds = ThemeToMapId.Keys.ToArray();
                character.Client.Send(new HavenBagPackListMessage(packIds));

                // Room update : action=0 (set), preview = la room courante avec
                // le thème courant.
                character.Client.Send(new HavenBagRoomUpdateMessage(0,
                    new HavenBagRoomPreviewInformation[] { new HavenBagRoomPreviewInformation(roomId, theme) }));

                // Liste de zaaps pour le zaap intérieur. Le zaap du havre-sac
                // permet de retourner sur n'importe quel zaap utilisé +
                // (en fallback) tous les zaaps du monde, de la même manière
                // que SendKnownZaapList vanilla.
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

        // -------------------------------------------------------------------
        // Zaap intérieur (intercepté par le sed dans GenericActions.HandleZaap)
        // -------------------------------------------------------------------
        /// <summary>
        /// Hook injecté à la place de character.OpenZaap(element).
        /// Si on est dans le havre-sac, ouvre notre dialog custom avec la
        /// liste de zaaps connus. Sinon, mémorise ce zaap comme connu et
        /// dispatch sur le comportement vanilla.
        /// </summary>
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
                    // Sur une map havre-sac, l'ActionIdentifier=Zaap est utilisé
                    // pour TOUS nos éléments bound (zaap, coffre, loterie). On
                    // dispatche selon la signature visuelle de l'élément.
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

                // Hors havre-sac : check les bindings globaux (ex: exit).
                // Cas plus rare aujourd'hui (les étoiles bones=3507 n'ont plus
                // d'InteractiveSkillRecord et passent par OnMovementConfirmed),
                // mais on garde le routage si jamais un binding global
                // se retrouve attaché à un élément Zaap-routed.
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

                // Pas de binding → comportement vanilla zaap (mémorise + ouvre dialog)
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
                // Sur les havre-sacs, on doit relâcher le verrou skill côté
                // client (sinon SKILL_DURATION=35 deciseconds bloque les clics
                // suivants, et la loterie en cooldown bloque tout puisqu'on
                // ne send rien). InteractiveUseEndedMessage signale au SWF
                // que l'utilisation du skill est terminée.
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

            // Hors havre-sac : comportement vanilla
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

        // -------------------------------------------------------------------
        // Édition (perso) — cycle Start / Save / Cancel / Finish
        // -------------------------------------------------------------------
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

        /// <summary>
        /// Début d'une séquence d'enregistrement de meubles (le client va
        /// envoyer ensuite plusieurs HavenBagFurnituresRequest puis un
        /// CloseHavenBagFurnitureSequenceRequest). On nettoie les meubles
        /// existants pour le thème courant pour pouvoir tout réinsérer.
        /// </summary>
        public static void OpenFurnitureSequence(Character character)
        {
            EnsureSchema();
            try
            {
                if (character.Map == null || !IsHavenBagMap(character.Map.Id)) return;
                byte theme = ResolveCurrentTheme(character);

                using var c = OpenConn();
                using var del = c.CreateCommand();
                del.CommandText = "DELETE FROM oneair_havenbag_furnitures WHERE CharacterId=@cid AND ThemeId=@t";
                del.Parameters.AddWithValue("@cid", character.Id);
                del.Parameters.AddWithValue("@t", theme);
                del.ExecuteNonQuery();
            }
            catch (Exception e) { Logger.Write("[OneAir] OpenFurnitureSequence failed: " + e.Message, Channels.Warning); }
        }

        /// <summary>
        /// Fin de la séquence (Close*). Renvoie au client la liste finale
        /// + signal EditHavenBagFinishedMessage pour sortir le SWF du mode
        /// édition. UN SEUL EditHavenBagFinishedMessage par save (le SWF
        /// l'attend via HavenbagFrame).
        /// </summary>
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

        /// <summary>
        /// Insertion incrémentale d'un paquet de meubles (le client peut
        /// splitter en plusieurs paquets via MAX_FURNITURES_PER_PACKET). Ne
        /// renvoie RIEN — le ECHO + EditHavenBagFinished sont envoyés
        /// uniquement par FinishEdit (CloseSequence).
        /// </summary>
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
                ins.CommandText = "INSERT INTO oneair_havenbag_furnitures (CharacterId, ThemeId, CellId, FurnitureId, Orientation) VALUES (@cid, @t, @c, @f, @o) ON DUPLICATE KEY UPDATE FurnitureId=VALUES(FurnitureId), Orientation=VALUES(Orientation)";
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
            // Trouve le thème en se basant sur la map actuelle (chaque thème
            // a sa propre map). Fallback sur la valeur DB / DefaultTheme.
            if (character?.Map != null)
            {
                foreach (var kv in ThemeToMapId)
                {
                    if (kv.Value == character.Map.Id) return kv.Key;
                }
            }
            return LoadThemeAndRoom(character.Id).theme;
        }

        public static void ChangeRoom(Character character, byte roomId)
        {
            // V1 : une seule room (id 0). On ack sans erreur.
            EnsureSchema();
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO oneair_havenbag_state (CharacterId, RoomId) VALUES (@cid, @r) ON DUPLICATE KEY UPDATE RoomId=VALUES(RoomId)";
                cmd.Parameters.AddWithValue("@cid", character.Id);
                cmd.Parameters.AddWithValue("@r", (byte)0); // forcé à 0 V1
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

                // Persist theme.
                using (var c = OpenConn())
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO oneair_havenbag_state (CharacterId, Theme) VALUES (@cid, @t) ON DUPLICATE KEY UPDATE Theme=VALUES(Theme)";
                    cmd.Parameters.AddWithValue("@cid", character.Id);
                    cmd.Parameters.AddWithValue("@t", theme);
                    cmd.ExecuteNonQuery();
                }

                // Téléport vers la map du nouveau thème (chaque thème = sa propre
                // map, le visuel vient du .dlm de cette map). On garde la
                // PreviousPosition existante pour la sortie.
                long newMapId = GetMapIdForTheme(theme);
                if (character.Map != null && character.Map.Id == newMapId)
                {
                    // Déjà sur la bonne map : juste un refresh.
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

        // -------------------------------------------------------------------
        // Coffre (ExchangeRequestMessage type=HAVENBAG=24)
        // -------------------------------------------------------------------
        public static bool TryHandleExchangeRequest(Character character, byte exchangeType)
        {
            if ((ExchangeTypeEnum)exchangeType != ExchangeTypeEnum.HAVENBAG) return false;

            if (character.Map == null || !IsHavenBagMap(character.Map.Id))
            {
                character.ReplyWarning("Coffre accessible uniquement dans le havre-sac.");
                return true;
            }

            // Le coffre du havre-sac partage l'inventaire banque vanilla
            // (suffisant pour notre serveur privé).
            character.OpenBank();
            return true;
        }

        // -------------------------------------------------------------------
        // Loterie quotidienne
        // -------------------------------------------------------------------
        public static void HandleLoteryRequest(Character character)
        {
            EnsureSchema();
            try
            {
                using var c = OpenConn();
                DateTime? last = null;
                using (var sel = c.CreateCommand())
                {
                    sel.CommandText = "SELECT LastLoteryAt FROM oneair_havenbag_state WHERE CharacterId=@cid";
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

                // Récompense V1 : 100 kamas (l'admin peut faire mieux via .ui).
                long reward = 100;
                character.AddKamas(reward);
                character.OnKamasGained(reward);

                using (var ins = c.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO oneair_havenbag_state (CharacterId, LastLoteryAt) VALUES (@cid, UTC_TIMESTAMP()) ON DUPLICATE KEY UPDATE LastLoteryAt=UTC_TIMESTAMP()";
                    ins.Parameters.AddWithValue("@cid", character.Id);
                    ins.ExecuteNonQuery();
                }

                // On NE renvoie PAS HavenBagDailyLoteryMessage : le SWF
                // tenterait un appel HAAPI Ankama (cf. décompil HavenbagFrame
                // ligne 391-398) pour consommer la récompense — qui échoue
                // sur notre serveur privé. À la place, simple feedback chat.
                character.Reply("Loterie : +" + reward + " kamas.");
            }
            catch (Exception e)
            {
                Logger.Write("[OneAir] Lotery failed: " + e.Message, Channels.Warning);
                character.ReplyError("Erreur loterie : " + e.Message);
            }
        }

        // -------------------------------------------------------------------
        // DB helpers
        // -------------------------------------------------------------------
        private static void SavePreviousPosition(long charId, long mapId, short cellId)
        {
            EnsureSchema();
            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO oneair_havenbag_state (CharacterId, PreviousMapId, PreviousCellId) VALUES (@cid, @m, @ce) ON DUPLICATE KEY UPDATE PreviousMapId=VALUES(PreviousMapId), PreviousCellId=VALUES(PreviousCellId)";
            cmd.Parameters.AddWithValue("@cid", charId);
            cmd.Parameters.AddWithValue("@m", mapId);
            cmd.Parameters.AddWithValue("@ce", cellId);
            cmd.ExecuteNonQuery();
        }

        private static (long mapId, short cellId) LoadPreviousPosition(long charId)
        {
            using var c = OpenConn();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT PreviousMapId, PreviousCellId FROM oneair_havenbag_state WHERE CharacterId=@cid";
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
            cmd.CommandText = "UPDATE oneair_havenbag_state SET PreviousMapId=NULL, PreviousCellId=NULL WHERE CharacterId=@cid";
            cmd.Parameters.AddWithValue("@cid", charId);
            cmd.ExecuteNonQuery();
        }

        private static (byte theme, byte roomId) LoadThemeAndRoom(long charId)
        {
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT Theme, RoomId FROM oneair_havenbag_state WHERE CharacterId=@cid";
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
        private static byte GetRoom(long charId) => LoadThemeAndRoom(charId).roomId;

        public static void AddKnownZaap(long charId, long mapId)
        {
            EnsureSchema();
            try
            {
                using var c = OpenConn();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT IGNORE INTO oneair_known_zaaps (CharacterId, MapId) VALUES (@cid, @m)";
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
                cmd.CommandText = "SELECT MapId FROM oneair_known_zaaps WHERE CharacterId=@cid ORDER BY DiscoveredAt";
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
                cmd.CommandText = "SELECT CellId, FurnitureId, Orientation FROM oneair_havenbag_furnitures WHERE CharacterId=@cid AND ThemeId=@t";
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

        // -------------------------------------------------------------------
        // Map info havre-sac : appelé depuis ClassicMapInstance.GetMapComplementaryInformationsDataMessage
        // (patché via sed dans le Dockerfile) pour transformer le message
        // standard en variante "in haven bag" quand le joueur est sur la
        // map du havre-sac.
        // -------------------------------------------------------------------
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

        /// <summary>
        /// Cell où on dépose le joueur quand il entre dans le havre-sac.
        /// On vise la cellule adjacente (sud-ouest) du zaap si présent —
        /// le joueur arrive devant le zaap, prêt à l'utiliser. Fallback
        /// random walkable si pas de zaap registré.
        /// </summary>
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

    // ----------------------------------------------------------------------
    // Custom dialog : zaap havre-sac. Liste = zaaps connus du joueur.
    // ----------------------------------------------------------------------
    public class OneAirHavenBagZaapDialog : TeleporterDialog
    {
        public override TeleporterTypeEnum TeleporterType => TeleporterTypeEnum.TELEPORTER_ZAAP;

        public OneAirHavenBagZaapDialog(Character character) : base(character)
        {
            // Le zaap intérieur du havre-sac liste UNIQUEMENT les zaaps
            // découverts par le joueur (table oneair_known_zaaps, alimentée
            // à chaque OpenZaap via HandleZaapInteraction). Fallback sur
            // SpawnPointMapId si aucun zaap découvert pour ne pas montrer
            // une liste vide.
            var knownMapIds = OneAirHavenBagPatch.LoadKnownZaaps(character.Id);

            Destinations = new Dictionary<long, TeleportDestination>();
            foreach (var mapId in knownMapIds)
            {
                if (OneAirHavenBagPatch.IsHavenBagMap(mapId)) continue;
                var map = MapRecord.GetMap(mapId);
                if (map == null) continue;
                Destinations[map.Id] = new TeleportDestination()
                {
                    cost = 0, // gratuit depuis le havre-sac
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
