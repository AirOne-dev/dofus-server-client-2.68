# =============================================================================
#  Giny.NETCore — émulateur Dofus 2.x en .NET 6 (cross-platform, arm64 natif)
# =============================================================================
ARG DOTNET_VERSION=6.0
ARG GINY_REPO=https://github.com/Skinz3/Giny.NETCore.git
ARG GINY_REF=30dc4415a7b0cae081144fdc94c78d0b01062bc6

# ---------- BUILD STAGE -----------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS builder
ARG GINY_REPO
ARG GINY_REF

RUN apt-get update && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN git init && \
    git remote add origin "${GINY_REPO}" && \
    git fetch --depth 1 origin "${GINY_REF}" && \
    git checkout FETCH_HEAD

# Patch 1: using manquant dans HealHp.cs
RUN sed -i '1iusing Giny.Core;' Sources/Servers/Giny.World/Managers/Fights/Effects/Heals/HealHp.cs

# Patch 2: GetRawPatch() throw KeyNotFoundException si le SWF n'est pas présent.
# On retourne null (qui sera traité comme "pas de patch" côté handlers).
RUN sed -i 's|return m_patches\[name\];|return m_patches.ContainsKey(name) ? m_patches[name] : new byte[0];|' \
        Sources/Giny.IO/RawPatch/RawPatchManager.cs

# Patch 3: swap l'ordre Send dans AuthClient — le client moderne 2.7x ferme
# la connexion immédiatement quand il reçoit un HelloConnectMessage avec key
# vide, AVANT de traiter le RawDataMessage qui contient l'AuthPatch SWF.
# On envoie donc le SWF AVANT le HelloConnect pour que le SWF patche
# le client à temps.
RUN sed -i \
    -e 's|Send(new HelloConnectMessage(string.Empty, new byte\[0\]));|/* swap: see Patch 3 */|' \
    -e 's|Send(new RawDataMessage(RawPatchManager.Instance.GetRawPatch("AuthPatch")));|Send(new RawDataMessage(RawPatchManager.Instance.GetRawPatch("AuthPatch"))); Send(new HelloConnectMessage(string.Empty, new byte[0]));|' \
    Sources/Servers/Giny.Auth/Network/AuthClient.cs

# Patch 4: log explicite quand un client se connecte ET tout ce qui est send/recv
# pour le debug du flux d'authentification.
RUN sed -i \
    -e 's|public AuthClient(Socket socket) : base(socket)|public AuthClient(Socket socket) : base(socket)|' \
    Sources/Servers/Giny.Auth/Network/AuthClient.cs

# Patch 5: Giny.RawDataMessage.Id reste 3710 — c'est ce que le 2.68 client
# attend en RECEPTION (MessageReceiver._messagesTypes[3710] = RawDataMessage),
# même si la classe AS3 expose protocolId=6253 pour les envois client→serveur.
# Asymétrie d'obfuscation Ankama. Pas de patch nécessaire ici.

# Patch 6: ajout de deux commandes chat OneAir :
#   .gocell <cellId>  (admin)  — teleport sur la map courante à la cellule donnée
#   .who              (joueur) — liste les joueurs connectés en chat
COPY OneAirChatCommands.cs Sources/Servers/Giny.World/Managers/Chat/OneAirChatCommands.cs

# Patch 7 (v2 admin) : poller des actions admin via la table oneair_actions.
# Démarré depuis WorldServer.OnServerStarted() — sed pour insérer l'appel.
COPY OneAirActionPoller.cs Sources/Servers/Giny.World/Managers/Chat/OneAirActionPoller.cs

# Patch 8 (events) : multiplicateurs XP/Kamas runtime persistés en DB.
COPY OneAirEventManager.cs Sources/Servers/Giny.World/Managers/Chat/OneAirEventManager.cs

# Hook l'appel Initialize() + le démarrage du poller au boot du world.
RUN sed -i 's|SetServerStatus(ServerStatusEnum\.ONLINE);|Giny.World.Managers.Chat.OneAirEventManager.Initialize(); Giny.World.Managers.Chat.OneAirActionPoller.Start(); SetServerStatus(ServerStatusEnum.ONLINE);|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Patch 9 (events) : multiplie l'XP gagnée AU DÉBUT de AddExperience pour que
# le SetExperience ET le CharacterExperienceGainMessage envoyé au client
# utilisent tous deux la valeur multipliée (sinon le client affiche la valeur
# d'origine bien que la DB stocke la multipliée).
RUN sed -i 's|SetExperience(Experience + value);|value = (long)(value * Giny.World.Managers.Chat.OneAirEventManager.XpMultiplier); SetExperience(Experience + value);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Patch 10 (events) : idem pour les kamas — patche le += pour multiplier
# au moment du grant. OnKamasGained envoie ensuite la bonne valeur.
RUN sed -i 's|Record\.Kamas += value;|value = (long)(value * Giny.World.Managers.Chat.OneAirEventManager.KamasMultiplier); Record.Kamas += value;|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Patch 11 (events) : Giny.NETCore commente l'AddExperience dans les rewards
# de quête (ligne `//character.AddExperience(value);`). On le décommente pour
# que les quêtes grantent bien l'XP, et donc passent par notre hook XpMultiplier.
RUN sed -i 's|//character\.AddExperience(value);|character.AddExperience(value);|' \
    Sources/Servers/Giny.World/Records/Quests/QuestStepRewardRecord.cs

# Patch 12 (events) : push le statut events au client lors du login (juste
# après le WelcomeMessage). Le SWF custom intercepte __ONEAIR_EVENTS__ pour
# afficher le bandeau permanent.
RUN sed -i 's|this\.Reply(ConfigManager<WorldConfig>\.Instance\.WelcomeMessage, Color\.CornflowerBlue);|this.Reply(ConfigManager<WorldConfig>.Instance.WelcomeMessage, Color.CornflowerBlue); Giny.World.Managers.Chat.OneAirEventManager.SendStatusTo(this.Client);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Patch 13 (havre-sac) : implémentation complète. Giny ne gère que l'entrée
# (un teleport hardcodé sans sortie ni zaap). On remplace HavenBagHandler.cs
# par notre version qui route TOUS les messages haven bag vers OneAirHavenBagPatch.
COPY OneAirHavenBagHandler.cs Sources/Servers/Giny.World/Handlers/Roleplay/HavenBag/HavenBagHandler.cs
COPY OneAirHavenBagPatch.cs   Sources/Servers/Giny.World/Managers/Chat/OneAirHavenBagPatch.cs
COPY OneAirDungeons.cs        Sources/Servers/Giny.World/Managers/Chat/OneAirDungeons.cs
COPY OneAirDeathManager.cs    Sources/Servers/Giny.World/Managers/Chat/OneAirDeathManager.cs
COPY OneAirDungeonSeeder.cs   Sources/Servers/Giny.World/Managers/Chat/OneAirDungeonSeeder.cs

# Patch 14 (havre-sac) : intercepte les clics sur les zaaps via le dispatcher
# générique. Le hook mémorise tous les zaaps utilisés par le joueur (pour la
# liste destinations du zaap intérieur du havre-sac) et redirige le clic
# zaap-dans-le-havre-sac vers notre OneAirHavenBagZaapDialog.
RUN sed -i 's|character\.OpenZaap((MapElement)parameter);|Giny.World.Managers.Chat.OneAirHavenBagPatch.HandleZaapInteraction(character, (MapElement)parameter);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActions.cs

# Patch 14bis (sortie de bâtiment) : intercepte les clics sur les éléments
# Action=Teleport. Posés au boot par EnsureExitInteractiveSkillsAsync sur
# les étoiles bones=3507 (Param1=mapId extérieur, Param2=cellId d'entrée).
# Le hook OneAirHavenBagPatch.HandleTeleportInteraction route les bones=3507
# vers TryExitBuilding (recalcul live de la map extérieure + cellule
# d'entrée). Les autres téléporteurs vanilla passent inchangés.
RUN sed -i \
    -e 's|character\.Teleport(int\.Parse(parameter\.Param1), cellId);|Giny.World.Managers.Chat.OneAirHavenBagPatch.HandleTeleportInteraction(character, (MapElement)parameter, int.Parse(parameter.Param1), cellId, true);|' \
    -e 's|character\.Teleport(int\.Parse(parameter\.Param1));|Giny.World.Managers.Chat.OneAirHavenBagPatch.HandleTeleportInteraction(character, (MapElement)parameter, int.Parse(parameter.Param1), (short)0, false);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActions.cs

# Patch 14ter (curseur silencieux) : pour les étoiles de sortie
# (Type=POINT_OUT_AN_EXIT282), on envoie le skill dans le tableau
# `disabledSkills` au lieu de `enabledSkills`. Effet :
#   - Le client garde elementTypeId pour le curseur "porte/utiliser".
#   - Le skill étant "disabled", le client ne rend PAS le tooltip
#     "Indiquer une sortie" au survol (le tooltip n'est rendu que pour
#     les skills enabled). Comportement vanilla Dofus.
# Le clic continue de marcher : EndMove() détecte Skill.ActionIdentifier
# == Teleport (côté serveur, le Skill est toujours posé) et appelle
# UseInteractive automatiquement.
RUN sed -i 's|InteractiveElementSkill\[\] skills = GetInteractiveElementSkill();|if (Record.Skill != null \&\& Record.Skill.Type == Giny.Protocol.Custom.Enums.InteractiveTypeEnum.POINT_OUT_AN_EXIT282) return new InteractiveElement((int)Record.Identifier, (int)Record.Skill.Type, new InteractiveElementSkill[0], GetInteractiveElementSkill(), true); InteractiveElementSkill[] skills = GetInteractiveElementSkill();|' \
    Sources/Servers/Giny.World/Managers/Maps/Elements/MapInteractiveElement.cs

# Patch 15 (havre-sac) : transforme MapComplementaryInformationsDataMessage
# en variante InHavenBag quand le joueur est sur la map du havre-sac. Cette
# transformation est nécessaire pour que le client active la UI havre-sac
# (boutons coffre/perso/loterie/sortie). Le wrapper retourne le message tel
# quel pour les autres maps.
RUN sed -i 's|return new MapComplementaryInformationsDataMessage(character\.Map\.SubareaId, Record\.Id, GetHousesInformations(), GetGameRolePlayActorsInformations(character),|return Giny.World.Managers.Chat.OneAirHavenBagPatch.MaybeUpgradeToHavenBag(character, new MapComplementaryInformationsDataMessage(character.Map.SubareaId, Record.Id, GetHousesInformations(), GetGameRolePlayActorsInformations(character),|' \
    Sources/Servers/Giny.World/Managers/Maps/Instances/ClassicMapInstance.cs && \
    sed -i 's|GetFightStartingPositions());|GetFightStartingPositions()));|' \
    Sources/Servers/Giny.World/Managers/Maps/Instances/ClassicMapInstance.cs

# Patch 16 (havre-sac) : init du schema havre-sac au boot du world (idempotent).
RUN sed -i 's|Giny.World.Managers.Chat.OneAirEventManager.Initialize(); Giny.World.Managers.Chat.OneAirActionPoller.Start();|Giny.World.Managers.Chat.OneAirEventManager.Initialize(); Giny.World.Managers.Chat.OneAirHavenBagPatch.EnsureSchema(); Giny.World.Managers.Chat.OneAirActionPoller.Start();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Patch 17 (havre-sac) : appelle OneAirHavenBagPatch.OnAfterEnterMap juste
# après SendMapComplementary dans Character.OnEnterMap. Ce hook envoie les
# messages d'init (furnitures/packs/room/zaaps) APRÈS la map info — sinon le
# client les ignore parce qu'il n'a pas encore activé son contexte havre-sac.
RUN sed -i 's|this\.Map\.Instance\.SendMapComplementary(Client);|this.Map.Instance.SendMapComplementary(Client); Giny.World.Managers.Chat.OneAirHavenBagPatch.OnAfterEnterMap(this);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Patch 18 : intercepte le clic NPC. Chaîne 2 hooks OneAir :
#   - havre-sac (coffre/loterie/zaap legacy)
#   - hub donjons (PNJs par plage de niveau)
# Le premier qui retourne true short-circuit le dispatch vanilla.
RUN sed -i 's#if (message\.npcMapId == client\.Character\.Map\.Id)#if (Giny.World.Managers.Chat.OneAirHavenBagPatch.TryHandleNpcAction(client.Character, message.npcId, message.npcMapId) || Giny.World.Managers.Chat.OneAirDungeons.TryHandleNpcAction(client.Character, message.npcId, message.npcMapId)) return; if (message.npcMapId == client.Character.Map.Id)#' \
    Sources/Servers/Giny.World/Handlers/Roleplay/Maps/Npcs/NpcsHandler.cs

# Patch 19 (havre-sac) : appel EnsureHavenBagNpcs + LoadInteractiveBindings
# au boot du world (après SpawnNpcs et MapRecord.Initialize). Spawn idempotent
# des NPCs + load des bindings BonesId → type.
RUN sed -i 's|Giny.World.Managers.Chat.OneAirHavenBagPatch.EnsureSchema();|Giny.World.Managers.Chat.OneAirHavenBagPatch.EnsureSchema(); Giny.World.Managers.Chat.OneAirHavenBagPatch.EnsureHavenBagNpcs(); Giny.World.Managers.Chat.OneAirHavenBagPatch.LoadInteractiveBindings(); Giny.World.Managers.Chat.OneAirDungeons.EnsureNpcs(); Giny.World.Managers.Chat.OneAirDungeons.EnsureDungeonGardiens(); Giny.World.Managers.Chat.OneAirDungeons.CleanupHubDialogs(); Giny.World.Managers.Chat.OneAirDungeonSeeder.SeedAll();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Patch 21 (havre-sac) : redirige le spawn au login. Si le joueur s'était
# déconnecté DANS son havre-sac, le Record.MapId pointe sur une map havre-sac.
# Le client 2.68 ne sait pas activer son contexte havre-sac quand il y arrive
# directement (sans avoir envoyé EnterHavenBagRequestMessage), résultat :
# écran noir tant qu'on ne quitte pas. Le hook réécrit Record.MapId/CellId
# vers la PreviousPosition stockée AVANT le Teleport vanilla.
RUN sed -i 's|client\.Character\.Teleport(client\.Character\.Record\.MapId, client\.Character\.Record\.CellId);|Giny.World.Managers.Chat.OneAirHavenBagPatch.RedirectIfHavenBagSpawn(client.Character); client.Character.Teleport(client.Character.Record.MapId, client.Character.Record.CellId);|' \
    Sources/Servers/Giny.World/Handlers/ContextHandler.cs

# Patch 22 (mort) : persiste les PV de fin de combat sur le character +
# applique la perte d'énergie/ghost/tp sur défaite. Hook avant le dispatch
# vanilla de OnFightEnding (qui ne fait rien en mode classical).
RUN sed -i 's|AchievementManager\.Instance\.OnPlayerFightEnding(this);|Giny.World.Managers.Chat.OneAirDeathManager.OnFightEnding(this); AchievementManager.Instance.OnPlayerFightEnding(this);|' \
    Sources/Servers/Giny.World/Managers/Fights/Fighters/CharacterFighter.cs

# Patch 23 (mort) : SpawnPoint() utilisé par RejoinMap pour les non-admins
# perdants TP normalement vers Record.SpawnPointMapId. On consulte le
# pending override de OneAirDeathManager pour rerouter vers donjon-entrance
# ou Phoenix.
RUN sed -i 's|MapRecord targetMap = MapRecord.GetMap(Record\.SpawnPointMapId);|MapRecord targetMap = MapRecord.GetMap(Giny.World.Managers.Chat.OneAirDeathManager.GetDeathSpawnPoint(this));|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Patch 20 (havre-sac) : SUPPRIMÉ — depuis qu'on insère des interactive_skills
# rows (avec ActionIdentifier=Zaap) pour les éléments bound, le flow vanilla
# dispatche correctement vers GenericActions.HandleZaap qui est déjà patché
# (Patch 14) pour appeler HandleZaapInteraction. Garder Patch 20 court-
# circuitait MapInstance.UseInteractive et empêchait l'envoi de
# InteractiveUseEndedMessage qu'on a besoin pour relâcher le verrou skill
# côté client.

# Patch 20bis (défense) : TeleportersManager.Initialize parse Param1 en int
# sur les rows InteractiveSkillRecord avec Action=Zaap/Zaapi. Si une row
# avec Param1 non-numérique se glisse en DB (cf. expérimentation
# précédente sur les étoiles de sortie), `int.Parse(null)` crashe le boot.
# On remplace `int.Parse(...)` par `TryParse + continue` pour rester
# défensif — les rows sans zone numérique sont ignorées proprement.
RUN sed -i 's|int zoneId = int\.Parse(interactiveSkill\.Param1);|if (!int.TryParse(interactiveSkill.Param1, out int zoneId)) continue;|g' \
    Sources/Servers/Giny.World/Managers/Maps/Teleporters/TeleportersManager.cs

# Patch 24 (debug) : OneAirUnhandledLogger — capture les actions joueur que
# Giny ne sait pas gérer (item sans handler, sort sans effect handler, NPC
# muet, paddock, message réseau non géré, etc.) dans la table
# oneair_unhandled_log. L'admin web expose ces events sous /admin#unhandled
# avec un bouton "Copier pour Claude" — le but est d'avoir un canal direct
# entre "le joueur a tenté X" et "Claude implémente le manque".
COPY OneAirUnhandledLogger.cs Sources/Servers/Giny.World/Managers/Chat/OneAirUnhandledLogger.cs

# Démarre le logger au boot (avant tous les autres pour qu'il soit prêt à
# capter le moindre incident dès le premier message client).
RUN sed -i 's|Giny.World.Managers.Chat.OneAirEventManager.Initialize();|Giny.World.Managers.Chat.OneAirUnhandledLogger.Start(); Giny.World.Managers.Chat.OneAirEventManager.Initialize();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Patch 25 : intercepte les items utilisables sans handler (UseItem retourne
# false silencieusement). Hook les 3 cas : (a) ReplyWarning "No method found
# to handle item usage", (b) catch d'exception pendant l'invocation, (c)
# return false final (item dont aucun effet ne mappe sur un handler).
RUN sed -i 's|character.ReplyWarning("No method found to handle item usage");|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogItemUse(character, item, "no_effect_handler"); character.ReplyWarning("No method found to handle item usage");|' \
    Sources/Servers/Giny.World/Managers/Items/ItemsManager.cs && \
    sed -i 's|Logger.Write("Item usage (" + item.Record.Id + ") : " + ex, Channels.Warning);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogItemUseError(character, item, ex); Logger.Write("Item usage (" + item.Record.Id + ") : " + ex, Channels.Warning);|' \
    Sources/Servers/Giny.World/Managers/Items/ItemsManager.cs

# Patch 26 : effets d'items équipables (anneaux, dofus, ...) — ItemEffectsManager
# ne tape qu'une whitelist d'effets ; tout le reste est silencieusement ignoré
# pour les non-admins (le ReplyWarning vanilla ne se déclenche que pour Role=Admin).
# Range-restreint au corps de AddEffects, pour ne pas logger en RemoveEffects
# (déséquiper avec un effet inconnu n'est pas un bug à signaler).
RUN sed -i '/public void AddEffects/,/public void RemoveEffects/ s|if (Handlers.ContainsKey(effect.EffectEnum))|if (!Handlers.ContainsKey(effect.EffectEnum)) Giny.World.Managers.Chat.OneAirUnhandledLogger.LogItemEffect(character, effect); if (Handlers.ContainsKey(effect.EffectEnum))|' \
    Sources/Servers/Giny.World/Managers/Items/ItemEffectsManager.cs

# Patch 27 : effets de sort sans handler en combat. DefaultSpellCastHandler
# warn "Unknown effect handler" mais ça reste enfoui dans les logs.
RUN sed -i 's|Cast.Source.Fight.Warn("Unknown effect handler " + effect.EffectEnum);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogSpellEffect((Cast.Source as Giny.World.Managers.Fights.Fighters.CharacterFighter)?.Character, effect, (int)Cast.SpellId, (int)(Cast.Spell?.Level?.Grade ?? 0)); Cast.Source.Fight.Warn("Unknown effect handler " + effect.EffectEnum);|' \
    Sources/Servers/Giny.World/Managers/Fights/Cast/DefaultSpellCastHandler.cs

# Patch 28 : actions génériques non implémentées (ex: GenericActionEnum.Paddock
# n'a PAS de handler dans GenericActions.cs — c'est exactement ce qui se passe
# quand un joueur clique sur un enclos d'élevage).
RUN sed -i 's|character.ReplyWarning("Unknown action identifier: " + parameter.ActionIdentifier);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogGenericAction(character, parameter); character.ReplyWarning("Unknown action identifier: " + parameter.ActionIdentifier);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActionsManager.cs

# Patch 29 : élements interactifs marqués Unhandled (HandleUnhandled). Ces
# éléments ont une notif joueur — on garde la notif mais on ajoute le log.
# Range au corps de HandleUnhandled (pour ne pas matcher HandleCraft /
# HandlSmithmagic qui ont la même déclaration locale).
RUN sed -i '/HandleUnhandled/,/HandleStartQuest/ s|MapInteractiveElement element = parameter as MapInteractiveElement;|MapInteractiveElement element = parameter as MapInteractiveElement; Giny.World.Managers.Chat.OneAirUnhandledLogger.LogInteractive(character, element);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActions.cs

# Patch 30 : NPC sans action liée pour le type cliqué. Ne touche pas notre
# fallback OneAirHavenBag/OneAirDungeons (déjà géré via Patch 18 qui
# short-circuite avant ce point), donc ici on capture vraiment les NPCs
# que personne ne sait faire parler.
RUN sed -i 's|character.ReplyWarning("No (" + actionType + ") action linked to this npc...(" + SpawnRecord.Id + ")");|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogNpcAction(character, this.Id, actionType.ToString(), SpawnRecord.Id); character.ReplyWarning("No (" + actionType + ") action linked to this npc...(" + SpawnRecord.Id + ")");|' \
    Sources/Servers/Giny.World/Managers/Entities/Npcs/Npc.cs

# Patch 31 : un clic interactif sans action mappée renvoie InteractiveUseError.
# On log juste avant — les 3 occurrences (no handler / element null / busy)
# sont toutes des cas "le joueur a tenté un truc qui n'a pas marché". Le
# dédup interne du logger absorbe la duplication si l'utilisateur clique en
# boucle.
RUN sed -i 's|character.Client.Send(new InteractiveUseErrorMessage(elemId, skillInstanceUid));|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogInteractiveError(character, elemId, skillInstanceUid); character.Client.Send(new InteractiveUseErrorMessage(elemId, skillInstanceUid));|g' \
    Sources/Servers/Giny.World/Managers/Maps/Instances/MapInstance.cs

# Patch 32 : messages réseau qui arrivent côté world sans aucun handler.
# Très utile : c'est exactement ce qu'on observe quand le client envoie un
# message d'enclos / échange / dragodinde / ami / etc. sans que Giny le
# connaisse — la classe est juste désérialisée puis droppée. On log AVANT
# le if(LogProtocol) pour capturer même quand LogProtocol=false.
# Range-restricted sed : on n'applique le préfixe que dans le corps de
# OnMessageUnhandled, pour ne pas polluer OnMessageReceived/OnSended qui
# utilisent le même test ConfigManager<WorldConfig>.Instance.LogProtocol.
RUN sed -i '/public override void OnMessageUnhandled/,/^        }$/ s|if (ConfigManager<WorldConfig>.Instance.LogProtocol)|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogNetMessage(this, message); if (ConfigManager<WorldConfig>.Instance.LogProtocol)|' \
    Sources/Servers/Giny.World/Network/WorldClient.cs

# Patch 33 : log les erreurs d'handler (exception jetée pendant le dispatch
# d'un message). Ces cas remontent dans LogManager.AppendError mais c'est
# plus pratique de les avoir aussi dans la table unhandled_log.
RUN sed -i 's|LogManager.Instance.AppendError(this, message, ex);|LogManager.Instance.AppendError(this, message, ex); Giny.World.Managers.Chat.OneAirUnhandledLogger.LogNetError(this, message, ex);|' \
    Sources/Servers/Giny.World/Network/WorldClient.cs

# Patch 34 : ExchangePlayerRequestMessage default case (types d'échanges non
# supportés par Giny — pet/mount swap, taxcollector, mount equip, etc.). Le
# handler renvoie REQUEST_IMPOSSIBLE silencieusement. On log uniquement dans
# le default (range default:→break;) pour ne pas spammer sur les cas légitimes
# (target null, busy, map mismatch).
RUN sed -i '/default:/,/break;/ s|client.Character.OnExchangeError(ExchangeErrorEnum.REQUEST_IMPOSSIBLE);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogExchangeRequest(client.Character, message.exchangeType, (long)message.target); client.Character.OnExchangeError(ExchangeErrorEnum.REQUEST_IMPOSSIBLE);|' \
    Sources/Servers/Giny.World/Handlers/Roleplay/Exchanges/ExchangesHandler.cs

# Patch 35 : sortie de bâtiment via cell-trigger (étoile au sol). Les
# "étoiles" Dofus 2.x ne sont ni cliquables (pas d'InteractiveUseRequest)
# ni des scrolls (pas de ChangeMapMessage) — c'est un trigger on-walk
# côté client (.dlm cell teleport). On hooke donc la fin de déplacement
# (HandleMapMovementConfirm) pour vérifier si la cellule d'arrivée porte
# un élément interactif binding "exit", et si oui on téléporte.
RUN sed -i 's|client.Send(new BasicNoOperationMessage());|client.Send(new BasicNoOperationMessage()); Giny.World.Managers.Chat.OneAirHavenBagPatch.OnMovementConfirmed(client.Character);|' \
    Sources/Servers/Giny.World/Handlers/Roleplay/Maps/MapsHandler.cs

# Patch 36 (community) : flux d'activité communautaire pour la landing.
# Émet des events level_up (paliers /25) et dungeon_win (salle de boss) dans
# la table oneair_activity. Lu par /api/public/community côté web.
COPY OneAirActivityFeed.cs Sources/Servers/Giny.World/Managers/Chat/OneAirActivityFeed.cs

# EnsureSchema au boot (avant tout pour que la table existe avant les hooks).
RUN sed -i 's|Giny.World.Managers.Chat.OneAirUnhandledLogger.Start();|Giny.World.Managers.Chat.OneAirUnhandledLogger.Start(); Giny.World.Managers.Chat.OneAirActivityFeed.EnsureSchema();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Patch 37 (community/level) : hook après chaque AddExperience pour détecter
# le franchissement d'un palier de niveau (multiples de 25). On capture
# Experience AVANT le SetExperience, puis on appelle OnExperienceGained avec
# old + new. Le sed s'applique APRÈS Patch 9 — on matche donc le suffixe
# "SetExperience(Experience + value);" qui reste dans la ligne post-patch.
RUN sed -i 's|SetExperience(Experience + value);|long _oaOldXp = Experience; SetExperience(Experience + value); Giny.World.Managers.Chat.OneAirActivityFeed.OnExperienceGained(this, _oaOldXp, Experience);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Patch 38 (community/dungeon) : hook au end-of-fight pour détecter la
# victoire dans la salle de boss d'un donjon. On insère AVANT
# AchievementManager (ce dernier est conservé Patch 22).
RUN sed -i 's|AchievementManager\.Instance\.OnPlayerFightEnding(this);|Giny.World.Managers.Chat.OneAirActivityFeed.OnFightEnding(this); AchievementManager.Instance.OnPlayerFightEnding(this);|' \
    Sources/Servers/Giny.World/Managers/Fights/Fighters/CharacterFighter.cs

# Build Auth + World (les outils WPF — MapEditor/SpellTree/Uplauncher — ne sont
# pas nécessaires côté serveur et requerraient EnableWindowsTargeting).
RUN dotnet publish Sources/Servers/Giny.Auth/Giny.Auth.csproj \
        -c Release -o /out/auth --nologo /p:WarningLevel=0 \
    && dotnet publish Sources/Servers/Giny.World/Giny.World.csproj \
        -c Release -o /out/world --nologo /p:WarningLevel=0

# ---------- RUNTIME STAGE ---------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates tini netcat-openbsd default-mysql-client gettext-base \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/giny
COPY --from=builder /out/auth  /opt/giny/auth/
COPY --from=builder /out/world /opt/giny/world/

# SWF patches (compilés depuis Ressources/SWF Patches.zip de Giny avec
# Apache Flex SDK). Le serveur les lit depuis ./SWF/ au lancement (Giny.IO
# RawPatchManager) et les sert au client via RawDataMessage. Sans
# AuthPatch.swf, le client refuse la connexion avec "le serveur n'a pas pu
# être authentifié" (signature RSA non validée).
COPY SWF/ /opt/giny/auth/SWF/
COPY SWF/ /opt/giny/world/SWF/

# Templates de config (rendered par entrypoint via envsubst)
COPY config/ /etc/giny/config/

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 443 5555 800 9000 9001
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
CMD ["auth"]
