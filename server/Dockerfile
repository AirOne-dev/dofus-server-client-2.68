# Giny.NETCore — émulateur Dofus 2.x en .NET 6.
ARG DOTNET_VERSION=6.0
ARG GINY_REPO=https://github.com/Skinz3/Giny.NETCore.git
ARG GINY_REF=30dc4415a7b0cae081144fdc94c78d0b01062bc6

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

# using manquant dans HealHp.cs
RUN sed -i '1iusing Giny.Core;' Sources/Servers/Giny.World/Managers/Fights/Effects/Heals/HealHp.cs

# GetRawPatch throw KeyNotFoundException si le SWF est absent → return new byte[0].
RUN sed -i 's|return m_patches\[name\];|return m_patches.ContainsKey(name) ? m_patches[name] : new byte[0];|' \
        Sources/Giny.IO/RawPatch/RawPatchManager.cs

# Le client 2.7x ferme la connexion sur HelloConnectMessage avant de traiter
# le RawDataMessage qui transporte l'AuthPatch. On envoie donc le SWF AVANT
# le HelloConnect pour que le patch arrive à temps.
RUN sed -i \
    -e 's|Send(new HelloConnectMessage(string.Empty, new byte\[0\]));|/* swap */|' \
    -e 's|Send(new RawDataMessage(RawPatchManager.Instance.GetRawPatch("AuthPatch")));|Send(new RawDataMessage(RawPatchManager.Instance.GetRawPatch("AuthPatch"))); Send(new HelloConnectMessage(string.Empty, new byte[0]));|' \
    Sources/Servers/Giny.Auth/Network/AuthClient.cs

RUN sed -i \
    -e 's|public AuthClient(Socket socket) : base(socket)|public AuthClient(Socket socket) : base(socket)|' \
    Sources/Servers/Giny.Auth/Network/AuthClient.cs

# Note : RawDataMessage.Id reste 3710 (réception client). La classe AS3 expose
# protocolId=6253 côté envoi — asymétrie d'obfuscation Ankama. Pas de patch.

COPY OneAirChatCommands.cs Sources/Servers/Giny.World/Managers/Chat/OneAirChatCommands.cs

COPY OneAirActionPoller.cs Sources/Servers/Giny.World/Managers/Chat/OneAirActionPoller.cs

COPY OneAirEventManager.cs Sources/Servers/Giny.World/Managers/Chat/OneAirEventManager.cs

RUN sed -i 's|SetServerStatus(ServerStatusEnum\.ONLINE);|Giny.World.Managers.Chat.OneAirEventManager.Initialize(); Giny.World.Managers.Chat.OneAirActionPoller.Start(); SetServerStatus(ServerStatusEnum.ONLINE);|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Multiplie l'XP AU DÉBUT de AddExperience : sinon le CharacterExperienceGainMessage
# envoyé au client utiliserait la valeur d'origine alors que la DB stocke la multipliée.
RUN sed -i 's|SetExperience(Experience + value);|value = (long)(value * Giny.World.Managers.Chat.OneAirEventManager.XpMultiplier); SetExperience(Experience + value);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

RUN sed -i 's|Record\.Kamas += value;|value = (long)(value * Giny.World.Managers.Chat.OneAirEventManager.KamasMultiplier); Record.Kamas += value;|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Giny commente AddExperience dans les rewards de quête : on le décommente
# pour que les quêtes passent par notre hook XpMultiplier.
RUN sed -i 's|//character\.AddExperience(value);|character.AddExperience(value);|' \
    Sources/Servers/Giny.World/Records/Quests/QuestStepRewardRecord.cs

RUN sed -i 's|this\.Reply(ConfigManager<WorldConfig>\.Instance\.WelcomeMessage, Color\.CornflowerBlue);|this.Reply(ConfigManager<WorldConfig>.Instance.WelcomeMessage, Color.CornflowerBlue); Giny.World.Managers.Chat.OneAirEventManager.SendStatusTo(this.Client);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

COPY OneAirHavenBagHandler.cs Sources/Servers/Giny.World/Handlers/Roleplay/HavenBag/HavenBagHandler.cs
COPY OneAirHavenBagPatch.cs   Sources/Servers/Giny.World/Managers/Chat/OneAirHavenBagPatch.cs
COPY OneAirDungeons.cs        Sources/Servers/Giny.World/Managers/Chat/OneAirDungeons.cs
COPY OneAirDeathManager.cs    Sources/Servers/Giny.World/Managers/Chat/OneAirDeathManager.cs
COPY OneAirDungeonSeeder.cs   Sources/Servers/Giny.World/Managers/Chat/OneAirDungeonSeeder.cs
COPY OneAirItemEffects.cs     Sources/Servers/Giny.World/Managers/Items/OneAirItemEffects.cs
COPY OneAirItemUses.cs        Sources/Servers/Giny.World/Managers/Items/OneAirItemUses.cs
COPY OneAirNoopHandlers.cs    Sources/Servers/Giny.World/Handlers/OneAir/OneAirNoopHandlers.cs

# Stacking dans un batch de craft. ItemCollection.AddItems(results) check
# GetSameItem contre m_items, mais les insertions dans m_items sont
# différées après la boucle → 90 items frais (UId=0) ne se voient pas entre
# eux et finissent en 90 entrées Quantity=1. AddItem(item) (singulier) ajoute
# immédiatement à m_items donc l'item N+1 stack sur l'item N.
RUN sed -i 's|Character\.Inventory\.AddItems(results);|foreach (var __oneAirCraftRes in results) Character.Inventory.AddItem(__oneAirCraftRes); /* OneAir: stack within craft batch */|' \
    Sources/Servers/Giny.World/Managers/Exchanges/Jobs/CraftExchange.cs

# Multi-effets : vanilla ItemsManager.UseItem return après le 1er effet
# matché → un Pain Gouin (AddHealth + AddPermanentVitality) ne donnait que le
# heal, le +1 vita perma était perdu. On expose m_usageHandlers en internal
# et on inject un return vers OneAirItemUses.DispatchEffects avant le
# foreach (le foreach vanilla devient unreachable, CS0162 toléré).
RUN sed -i 's|private Dictionary<ItemUsageHandlerAttribute, MethodInfo> m_usageHandlers|internal Dictionary<ItemUsageHandlerAttribute, MethodInfo> m_usageHandlers|' \
    Sources/Servers/Giny.World/Managers/Items/ItemsManager.cs && \
    sed -i 's|foreach (var effect in item.Effects.OfType<Effect>())|return Giny.World.Managers.Items.OneAirItemUses.DispatchEffects(character, item, m_usageHandlers); foreach (var effect in item.Effects.OfType<Effect>())|' \
    Sources/Servers/Giny.World/Managers/Items/ItemsManager.cs

RUN sed -i 's|character\.OpenZaap((MapElement)parameter);|Giny.World.Managers.Chat.OneAirHavenBagPatch.HandleZaapInteraction(character, (MapElement)parameter);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActions.cs

# `as MapElement` (et non cast strict) car HandleTeleportAction est aussi
# appelé depuis NpcTalkDialog.Reply avec un NpcReplyRecord — un cast strict
# crashait avec InvalidCastException.
RUN sed -i \
    -e 's|character\.Teleport(int\.Parse(parameter\.Param1), cellId);|Giny.World.Managers.Chat.OneAirHavenBagPatch.HandleTeleportInteraction(character, parameter as MapElement, int.Parse(parameter.Param1), cellId, true);|' \
    -e 's|character\.Teleport(int\.Parse(parameter\.Param1));|Giny.World.Managers.Chat.OneAirHavenBagPatch.HandleTeleportInteraction(character, parameter as MapElement, int.Parse(parameter.Param1), (short)0, false);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActions.cs

# Étoiles de sortie : skill posé en `disabledSkills` au lieu de `enabledSkills`.
# Le client garde elementTypeId pour le curseur "porte" mais ne rend pas le
# tooltip "Indiquer une sortie". Le clic continue de marcher via EndMove +
# UseInteractive (côté serveur le Skill est toujours posé).
RUN sed -i 's|InteractiveElementSkill\[\] skills = GetInteractiveElementSkill();|if (Record.Skill != null \&\& Record.Skill.Type == Giny.Protocol.Custom.Enums.InteractiveTypeEnum.POINT_OUT_AN_EXIT282) return new InteractiveElement((int)Record.Identifier, (int)Record.Skill.Type, new InteractiveElementSkill[0], GetInteractiveElementSkill(), true); InteractiveElementSkill[] skills = GetInteractiveElementSkill();|' \
    Sources/Servers/Giny.World/Managers/Maps/Elements/MapInteractiveElement.cs

RUN sed -i 's|return new MapComplementaryInformationsDataMessage(character\.Map\.SubareaId, Record\.Id, GetHousesInformations(), GetGameRolePlayActorsInformations(character),|return Giny.World.Managers.Chat.OneAirHavenBagPatch.MaybeUpgradeToHavenBag(character, new MapComplementaryInformationsDataMessage(character.Map.SubareaId, Record.Id, GetHousesInformations(), GetGameRolePlayActorsInformations(character),|' \
    Sources/Servers/Giny.World/Managers/Maps/Instances/ClassicMapInstance.cs && \
    sed -i 's|GetFightStartingPositions());|GetFightStartingPositions()));|' \
    Sources/Servers/Giny.World/Managers/Maps/Instances/ClassicMapInstance.cs

RUN sed -i 's|Giny.World.Managers.Chat.OneAirEventManager.Initialize(); Giny.World.Managers.Chat.OneAirActionPoller.Start();|Giny.World.Managers.Chat.OneAirEventManager.Initialize(); Giny.World.Managers.Chat.OneAirHavenBagPatch.EnsureSchema(); Giny.World.Managers.Chat.OneAirActionPoller.Start();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# OnAfterEnterMap doit fire APRÈS SendMapComplementary : sinon les
# furnitures/packs/room/zaaps sont ignorés par le client (contexte havre-sac
# pas encore activé).
RUN sed -i 's|this\.Map\.Instance\.SendMapComplementary(Client);|this.Map.Instance.SendMapComplementary(Client); Giny.World.Managers.Chat.OneAirHavenBagPatch.OnAfterEnterMap(this);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

RUN sed -i 's#if (message\.npcMapId == client\.Character\.Map\.Id)#if (Giny.World.Managers.Chat.OneAirDungeons.TryHandleNpcAction(client.Character, message.npcId, message.npcMapId)) return; if (message.npcMapId == client.Character.Map.Id)#' \
    Sources/Servers/Giny.World/Handlers/Roleplay/Maps/Npcs/NpcsHandler.cs

RUN sed -i 's|Giny.World.Managers.Chat.OneAirHavenBagPatch.EnsureSchema();|Giny.World.Managers.Chat.OneAirHavenBagPatch.EnsureSchema(); Giny.World.Managers.Chat.OneAirHavenBagPatch.LoadInteractiveBindings(); Giny.World.Managers.Chat.OneAirDungeons.EnsureNpcs(); Giny.World.Managers.Chat.OneAirDungeons.EnsureDungeonGardiens(); Giny.World.Managers.Chat.OneAirDungeons.CleanupHubDialogs(); Giny.World.Managers.Chat.OneAirDungeonSeeder.SeedAll();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Évite l'écran noir au reconnect dans le havre-sac : le client 2.68 n'active
# pas le contexte havre-sac s'il y arrive sans avoir envoyé EnterHavenBagRequest.
RUN sed -i 's|client\.Character\.Teleport(client\.Character\.Record\.MapId, client\.Character\.Record\.CellId);|Giny.World.Managers.Chat.OneAirHavenBagPatch.RedirectIfHavenBagSpawn(client.Character); client.Character.Teleport(client.Character.Record.MapId, client.Character.Record.CellId);|' \
    Sources/Servers/Giny.World/Handlers/ContextHandler.cs

RUN sed -i 's|AchievementManager\.Instance\.OnPlayerFightEnding(this);|Giny.World.Managers.Chat.OneAirDeathManager.OnFightEnding(this); AchievementManager.Instance.OnPlayerFightEnding(this);|' \
    Sources/Servers/Giny.World/Managers/Fights/Fighters/CharacterFighter.cs

# Défense : TeleportersManager.Initialize crash sur Param1 non-numérique.
# TryParse + continue rend le boot robuste aux rows mal formées.
RUN sed -i 's|int zoneId = int\.Parse(interactiveSkill\.Param1);|if (!int.TryParse(interactiveSkill.Param1, out int zoneId)) continue;|g' \
    Sources/Servers/Giny.World/Managers/Maps/Teleporters/TeleportersManager.cs

COPY OneAirUnhandledLogger.cs Sources/Servers/Giny.World/Managers/Chat/OneAirUnhandledLogger.cs

RUN sed -i 's|Giny.World.Managers.Chat.OneAirEventManager.Initialize();|Giny.World.Managers.Chat.OneAirUnhandledLogger.Start(); Giny.World.Managers.Chat.OneAirEventManager.Initialize();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Hook les 3 cas d'item sans handler : ReplyWarning, exception pendant
# l'invocation, return false final.
RUN sed -i 's|character.ReplyWarning("No method found to handle item usage");|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogItemUse(character, item, "no_effect_handler"); character.ReplyWarning("No method found to handle item usage");|' \
    Sources/Servers/Giny.World/Managers/Items/ItemsManager.cs && \
    sed -i 's|Logger.Write("Item usage (" + item.Record.Id + ") : " + ex, Channels.Warning);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogItemUseError(character, item, ex); Logger.Write("Item usage (" + item.Record.Id + ") : " + ex, Channels.Warning);|' \
    Sources/Servers/Giny.World/Managers/Items/ItemsManager.cs

# Range-restreint au corps de AddEffects pour ne pas logger en RemoveEffects.
RUN sed -i '/public void AddEffects/,/public void RemoveEffects/ s|if (Handlers.ContainsKey(effect.EffectEnum))|if (!Handlers.ContainsKey(effect.EffectEnum)) Giny.World.Managers.Chat.OneAirUnhandledLogger.LogItemEffect(character, effect); if (Handlers.ContainsKey(effect.EffectEnum))|' \
    Sources/Servers/Giny.World/Managers/Items/ItemEffectsManager.cs

RUN sed -i 's|Cast.Source.Fight.Warn("Unknown effect handler " + effect.EffectEnum);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogSpellEffect((Cast.Source as Giny.World.Managers.Fights.Fighters.CharacterFighter)?.Character, effect, (int)Cast.SpellId, (int)(Cast.Spell?.Level?.Grade ?? 0)); Cast.Source.Fight.Warn("Unknown effect handler " + effect.EffectEnum);|' \
    Sources/Servers/Giny.World/Managers/Fights/Cast/DefaultSpellCastHandler.cs

# SpellEffectManager.GetSpellEffectHandler appelait Activator.CreateInstance(null,…)
# quand aucun handler n'était enregistré, → ArgumentNullException qui crashait
# tout le tour de combat. Le caller gère déjà un retour null.
RUN sed -i 's|return (SpellEffectHandler)Activator.CreateInstance(type, new object\[\] { effect, castHandler });|if (type == null) return null; return (SpellEffectHandler)Activator.CreateInstance(type, new object[] { effect, castHandler });|' \
    Sources/Servers/Giny.World/Managers/Fights/Effects/SpellEffectManager.cs

RUN sed -i 's|character.ReplyWarning("Unknown action identifier: " + parameter.ActionIdentifier);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogGenericAction(character, parameter); character.ReplyWarning("Unknown action identifier: " + parameter.ActionIdentifier);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActionsManager.cs

# Range au corps de HandleUnhandled (HandleCraft/HandleSmithmagic ont la
# même déclaration locale).
RUN sed -i '/HandleUnhandled/,/HandleStartQuest/ s|MapInteractiveElement element = parameter as MapInteractiveElement;|MapInteractiveElement element = parameter as MapInteractiveElement; Giny.World.Managers.Chat.OneAirUnhandledLogger.LogInteractive(character, element);|' \
    Sources/Servers/Giny.World/Managers/Generic/GenericActions.cs

RUN sed -i 's|character.ReplyWarning("No (" + actionType + ") action linked to this npc...(" + SpawnRecord.Id + ")");|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogNpcAction(character, this.Id, actionType.ToString(), SpawnRecord.Id); character.ReplyWarning("No (" + actionType + ") action linked to this npc...(" + SpawnRecord.Id + ")");|' \
    Sources/Servers/Giny.World/Managers/Entities/Npcs/Npc.cs

RUN sed -i 's|character.Client.Send(new InteractiveUseErrorMessage(elemId, skillInstanceUid));|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogInteractiveError(character, elemId, skillInstanceUid); character.Client.Send(new InteractiveUseErrorMessage(elemId, skillInstanceUid));|g' \
    Sources/Servers/Giny.World/Managers/Maps/Instances/MapInstance.cs

# Range-restricted : on log seulement dans OnMessageUnhandled, pas dans
# OnMessageReceived/OnSended qui partagent le même test LogProtocol.
RUN sed -i '/public override void OnMessageUnhandled/,/^        }$/ s|if (ConfigManager<WorldConfig>.Instance.LogProtocol)|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogNetMessage(this, message); if (ConfigManager<WorldConfig>.Instance.LogProtocol)|' \
    Sources/Servers/Giny.World/Network/WorldClient.cs

RUN sed -i 's|LogManager.Instance.AppendError(this, message, ex);|LogManager.Instance.AppendError(this, message, ex); Giny.World.Managers.Chat.OneAirUnhandledLogger.LogNetError(this, message, ex);|' \
    Sources/Servers/Giny.World/Network/WorldClient.cs

# LogManager.AppendError check `client.Character != null` ligne 49 puis lit
# `client.Character.Map` ligne 54 SANS re-checker. Si Character est null à ce
# moment-là (chat reçu pendant la fenêtre de switch perso / reco), la
# 2ᵉ NRE remonte non gérée hors du try/catch du dispatcher → process world
# down. Mirror le null-check pour qu'AppendError ne throw plus.
RUN sed -i 's|if (client.Character.Map != null)|if (client.Character != null \&\& client.Character.Map != null)|' \
    Sources/Servers/Giny.World/Logging/LogManager.cs

# Range default:→break: ne log que les types d'échange non supportés
# (pet/mount swap, taxcollector, …), pas les rejets légitimes.
RUN sed -i '/default:/,/break;/ s|client.Character.OnExchangeError(ExchangeErrorEnum.REQUEST_IMPOSSIBLE);|Giny.World.Managers.Chat.OneAirUnhandledLogger.LogExchangeRequest(client.Character, message.exchangeType, (long)message.target); client.Character.OnExchangeError(ExchangeErrorEnum.REQUEST_IMPOSSIBLE);|' \
    Sources/Servers/Giny.World/Handlers/Roleplay/Exchanges/ExchangesHandler.cs

# Étoiles 2.x = trigger on-walk côté client (.dlm cell teleport), pas un clic
# interactif. Donc on hooke la fin de déplacement (HandleMapMovementConfirm).
RUN sed -i 's|client.Send(new BasicNoOperationMessage());|client.Send(new BasicNoOperationMessage()); Giny.World.Managers.Chat.OneAirHavenBagPatch.OnMovementConfirmed(client.Character);|' \
    Sources/Servers/Giny.World/Handlers/Roleplay/Maps/MapsHandler.cs

COPY OneAirActivityFeed.cs Sources/Servers/Giny.World/Managers/Chat/OneAirActivityFeed.cs

RUN sed -i 's|Giny.World.Managers.Chat.OneAirUnhandledLogger.Start();|Giny.World.Managers.Chat.OneAirUnhandledLogger.Start(); Giny.World.Managers.Chat.OneAirActivityFeed.EnsureSchema();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Le sed s'applique APRÈS le hook XpMultiplier — on matche le suffixe qui
# reste dans la ligne post-patch.
RUN sed -i 's|SetExperience(Experience + value);|long _oaOldXp = Experience; SetExperience(Experience + value); Giny.World.Managers.Chat.OneAirActivityFeed.OnExperienceGained(this, _oaOldXp, Experience);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

RUN sed -i 's|AchievementManager\.Instance\.OnPlayerFightEnding(this);|Giny.World.Managers.Chat.OneAirActivityFeed.OnFightEnding(this); AchievementManager.Instance.OnPlayerFightEnding(this);|' \
    Sources/Servers/Giny.World/Managers/Fights/Fighters/CharacterFighter.cs

# Régen PV hors combat — 1 HP/s debout, 2 HP/s assis (emote sit/rest).
# Comportement vanilla Dofus 2.x : LifePointsRegenBeginMessage(regenRate) +
# LifePointsRegenEndMessage à la fin / sur changement de rythme.
COPY OneAirRegenManager.cs Sources/Servers/Giny.World/Managers/Chat/OneAirRegenManager.cs

RUN sed -i 's|Giny.World.Managers.Chat.OneAirActionPoller.Start();|Giny.World.Managers.Chat.OneAirActionPoller.Start(); Giny.World.Managers.Chat.OneAirRegenManager.Start();|' \
    Sources/Servers/Giny.World/Network/WorldServer.cs

# Hook PlayEmote → tracking de l'état assis pour accélérer la régen.
RUN sed -i 's|EmoteRecord template = EmoteRecord.GetEmote(emoteId);|EmoteRecord template = EmoteRecord.GetEmote(emoteId); Giny.World.Managers.Chat.OneAirRegenManager.OnEmotePlayed(this, emoteId);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Tout déplacement casse l'état assis (comportement Dofus). MovementKeys=keyMovements
# n'apparaît qu'une fois dans le fichier (corps de MoveOnMap).
RUN sed -i 's|this.MovementKeys = keyMovements;|this.MovementKeys = keyMovements; Giny.World.Managers.Chat.OneAirRegenManager.OnMovementStarted(this);|' \
    Sources/Servers/Giny.World/Managers/Entities/Characters/Character.cs

# Build Auth + World (les outils WPF Giny ne sont pas buildés : pas utiles
# côté serveur et requerraient EnableWindowsTargeting).
RUN dotnet publish Sources/Servers/Giny.Auth/Giny.Auth.csproj \
        -c Release -o /out/auth --nologo /p:WarningLevel=0 \
    && dotnet publish Sources/Servers/Giny.World/Giny.World.csproj \
        -c Release -o /out/world --nologo /p:WarningLevel=0

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates tini netcat-openbsd default-mysql-client gettext-base \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/giny
COPY --from=builder /out/auth  /opt/giny/auth/
COPY --from=builder /out/world /opt/giny/world/

# SWF patches lus par RawPatchManager au lancement, servis via RawDataMessage.
# Sans AuthPatch.swf, le client refuse la connexion (signature RSA non validée).
COPY SWF/ /opt/giny/auth/SWF/
COPY SWF/ /opt/giny/world/SWF/

COPY config/ /etc/giny/config/

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 443 5555 800 9000 9001
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
CMD ["auth"]
