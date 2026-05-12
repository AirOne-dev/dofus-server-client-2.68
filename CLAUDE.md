# CLAUDE.md

Guide opérationnel pour assister sur **OneAir** (serveur Dofus 2.68 privé +
clients macOS et Windows).

## Règles d'or

**1. Backup avant tout restart Docker.** Avant `docker compose down`,
`restart`, `up -d --force-recreate` sur n'importe quel `giny-*` :

```bash
docker exec giny-mysql sh -c 'mysqldump --single-transaction --databases \
  giny_auth giny_world -u root -p"$MYSQL_ROOT_PASSWORD" | gzip' \
  > server/backups/manual-$(date +%Y%m%d-%H%M%S).sql.gz
```

Le service `backup` boucle un dump toutes les minutes (rotation 20), mais ne
pas s'y fier pour un restart imminent — la fenêtre de perte est trop grande.

**2. Rebuild auto du `web` après modif `server/web/`.** Tout ce qui est sous
ce dossier est embarqué via `go:embed`, pas de hot-reload :

```bash
docker compose build web && docker compose up -d --no-deps --force-recreate web
docker logs --tail=20 giny-web
curl -sI http://localhost/ | head -3
```

**3. Article changelog** uniquement quand le changement est visible côté
joueur (nouveau contenu, gameplay, bugfix in-game, annonce, sortie client).
Pas pour des refactos, builds, patches SWF, optimisations invisibles.

```bash
cat > /tmp/article.md <<'EOF'
---
title: …
slug: …
tag: patch
excerpt: …
---
…
EOF
./scripts/publish-article.sh /tmp/article.md
```

Tags : `patch`, `annonce`, `event`, `devblog`, `fix`. UPSERT par slug, pas
besoin de redémarrer.

**4. Mettre à jour `README.md` et `CLAUDE.md`** quand la modif change un
workflow ou ajoute une règle. Pas pour chaque commit.

## Architecture

- `server/` — image Docker .NET 6. Le source Giny.NETCore est **vendoré**
  dans `server/giny/` (cf. `server/giny/UPSTREAM.md` pour le commit pinné),
  avec nos modifs `OneAir*` éditées directement dans l'arbre. Le Dockerfile
  ne fait plus que `COPY giny/` + `dotnet publish`. Containers : `giny-auth`
  (5555), `giny-world` (5556) ; image partagée `giny/server:latest`. MySQL
  et DBGate à côté.
- `server/web/` — service Go single-binary : landing publique `/`, dashboard
  admin `/admin`, APIs `/api/*` et `/api/public/*`. Image `oneair/web`.
- `client/launcher/macos/` (Swift) → bundle `client/build/OneAir.app/`.
- `client/launcher/windows/` (WPF .NET 8) → bundle `client/build/OneAir-Windows/`.
- `client/zaap-server/` (Go) — émulateur Thrift Zaap, embarqué dans les deux
  bundles. Spawn par les launchers, écoute sur 127.0.0.1:4242 et :4243.
- `client/DofusInvoker-patched.swf` — SWF AS3 patché, partagé Mac/Win.
- `client/build/OneAir.app/Contents/Resources/DofusInvoker.swf` — SWF
  vivant côté Mac (c'est lui qu'on patche en dev, puis on copie dans
  `client/`).
- `client/build/` — sortie de `client/build.sh` : `OneAir.app/`,
  `OneAir-Windows/` et les zips servis par `/download/{macos,windows}`.
  Monté en lecture seule dans le container `web` (cf. `docker-compose.yml`).
- `client/.cache/` — gitignoré. Reçoit les assets Dofus officiels
  (`dofus-{darwin,windows}-2.68/`, ~5 GB chacun, fetch via cytrus) et le
  Swift SDK Darwin (~700 MB) assemblé pour le cross-compile.

## Workflow patch SWF

Le SWF est patché par injection AS3 via FFDec :

```bash
$EDITOR /tmp/scripts-import/com/ankamagames/dofus/.../ChatFrame.as
java -jar /tmp/ffdec/ffdec.jar -importScript \
  /tmp/DofusInvoker-base.swf /tmp/DofusInvoker-debug.swf /tmp/scripts-import
cp /tmp/DofusInvoker-debug.swf ./client/build/OneAir.app/Contents/Resources/DofusInvoker.swf
cp /tmp/DofusInvoker-debug.swf ./client/DofusInvoker-patched.swf
pkill -f dofus-real; pkill -f OneAir; pkill -f zaap-server
xattr -cr ./client/build/OneAir.app && codesign --force --deep -s - ./client/build/OneAir.app
rm -f ./client/build/OneAir.app/Contents/Resources/oneair-debug.log
open ./client/build/OneAir.app
```

`/tmp/DofusInvoker-base.swf` = SWF vanilla patché (point de départ immuable).

## Workflow patch serveur

Tout le code .cs vit dans `server/giny/Sources/`. Pour modifier un comportement
existant, edit direct le fichier Giny (la modif coexiste avec le code OneAir).
Pour ajouter une feature OneAir : créer/éditer un `OneAir*.cs` au bon endroit
sous `server/giny/Sources/Servers/Giny.World/...`.

```bash
$EDITOR server/giny/Sources/Servers/Giny.World/Managers/Chat/OneAirChatCommands.cs
docker compose build auth                                    # ~10s cold
docker compose up -d --no-deps --force-recreate auth world
docker logs -f giny-world
```

Pour ajouter un nouveau hook dans le code Giny vanilla : éditer directement
le fichier source dans `server/giny/Sources/...`. Préfixer chaque ajout
`OneAir`/`_oneAir` pour rester repérable (`grep -rn "OneAir" server/giny`).
Voir `server/giny/UPSTREAM.md` pour le commit upstream pinné.

## Workflow build clients

Point d'entrée unique `./client/build.sh` (menu interactif si aucun arg) :

```bash
./client/build.sh darwin             # OneAir.app via Docker (toujours)
./client/build.sh darwin --native    # OneAir.app via outils macOS hôte
./client/build.sh windows            # OneAir-Windows/ (Docker obligatoire)
./client/build.sh all
./client/build.sh <target> --no-zip  # skip le zip client/build/
```

Le Swift SDK Darwin (~700 MB) est assemblé automatiquement la 1ère fois
qu'on build `darwin`. Pour forcer une reconstruction : supprimer
`client/.cache/darwin.artifactbundle.zip` avant le build.

Le script se ré-invoque dans le container builder avec
`ONEAIR_INSIDE_CONTAINER=1` pour faire l'assembly. Bundles zippés dans
`client/build/` (Store, pas de compression — assets déjà compressés). Le service
`web` les sert via `/download/{macos,windows}`.

## Pièges AS3 / FFDec

- Inner functions style `function foo(){}` : compilées incorrectement.
  Utiliser `var foo:Function = function(){};`.
- Forward-reference de closure dans le même scope → `Error #1065` à runtime.
  Extraire en méthode statique.
- IIFE typée `:Function` → `Error #1066` (compilée en `new Function(string)`).
  Stocker les params dans un `Dictionary` et invoquer via méthode statique.
- JSON natif indispo (runtime AIR). Utiliser
  `com.ankamagames.jerakine.json.JSON.decode(s)`.
- `MouseEvent.CLICK` sur Sprite ajouté dynamiquement → intercepté par Berilia.
  Listener stage en mode capture (`useCapture=true`, priorité haute).
- Patch SWF qui crash → silencieux côté UI. Toujours wrapper en try/catch
  avec `oneAirLog()` (écrit dans `client/build/OneAir.app/Contents/Resources/oneair-debug.log`).
- `Inventory.GetItems()` retourne aussi les équipés. Trier par
  `IsEquiped() ? 0 : 1`. Modifier un item équipé → `Character.RefreshStats()`.

## Pièges patches Dockerfile

- **HandleTeleportAction** doit utiliser `parameter as MapElement` et **pas**
  `(MapElement)parameter`. Ce code est aussi appelé depuis les répliques
  PNJ, où `parameter` est un `NpcReplyRecord` — un cast strict crashe le
  dialogue. La version `as` retombe sur le comportement vanilla en cas de
  cast échoué.
- **`SpellEffectManager.GetSpellEffectHandler`** : court-circuiter
  `Activator.CreateInstance(null, …)` quand le type est introuvable.
  Sans ça, n'importe quel sort sans `[SpellEffectHandler]` côté serveur fait
  crasher tout le tour de combat.

## Marqueurs ONEAIR (chat système → SWF)

Préfixes dans les `TextInformationMessage` que le SWF intercepte et n'affiche
pas en chat :

| Préfixe | Émis par | Consommé par |
|---|---|---|
| `__ONEAIR_PLAYERS__` | `.who` | panneau `.online` |
| `__ONEAIR_INV__[…]` | `.itemdump` | éditeur `.itemui` |
| `__ONEAIR_EVENTS__` | EventManager | UI multipliers |

Interception dans `ChatFrame.as` (handler `TextInformationMessage`) qui
`preventDefault()`.

## Bindings interactifs globaux

`havenbag_interactives` (BonesId → type) marche **hors havre-sac** pour les
décors présents partout (ex: `bones=3507` = étoile sortie de bâtiment). Pour
les sorties, le déclenchement est on-walk uniquement (pas
`InteractiveSkillRecord` → sinon le client 2.68 pathfind sur cellule
adjacente au lieu de marcher dessus).

`TryExitBuilding` cherche la map de sortie via
`MapRecord.GetMaps(current.Position.Point)` (extérieur partage les coords
world avec l'intérieur, marqué `Outdoor=true`). Fallbacks : sibling Outdoor
→ sibling autre → voisin worldmap chargé → SpawnPointMapId. **Ne pas**
retomber sur `TopMap`/`BottomMap` sans filtrer par `MapRecord.GetMap(...) != null`.

## Capture des actions non gérées

`OneAirUnhandledLogger.cs` sérialise dans `unhandled_log` chaque action
joueur que Giny ne sait pas exécuter (item sans handler, sort sans effet,
NPC muet, message protocol orphelin, etc.). Hooks injectés directement
dans les .cs Giny (cherchables avec `grep -rn "OneAirUnhandledLogger"
server/giny`). Logger non-bloquant (queue 2s), non-throwing, dédup 30s,
auto-purge > 30j.

Panel admin `/admin#unhandled` avec bouton *Copier pour Claude* qui produit
un Markdown auto-suffisant.

Ajouter une catégorie : nouveau `LogXxx()` dans `OneAirUnhandledLogger.cs`
+ appel inline injecté à l'endroit voulu dans le code Giny (`server/giny/
Sources/...`) + ligne descriptive dans `formatUnhandledMarkdown` (`server/
web/unhandled.go`).

## Havre-sac

Map intérieure ID 162791424 (4 thèmes). `OneAirHavenBagHandler.cs` remplace
le handler vanilla, route les 11 messages haven-bag vers
`OneAirHavenBagPatch.cs` (manager + dialog zaap custom). État persisté dans
`havenbag_state`, `known_zaaps`, `havenbag_furnitures`,
`havenbag_interactives`.

## Conventions / nommage

- Préfixe `oneAir`/`OneAir` / `_oneAir` pour tout ce qu'on ajoute (champs,
  méthodes, commandes chat) → sépare notre code du code Giny/Ankama.
- **Pas de préfixe sur les tables MySQL custom** — elles vivent dans
  `giny_world` aux côtés des tables Giny (`actions`, `articles`, `events`,
  `havenbag_state`, `online_clients`, etc.). Vérifier l'absence de
  collision avant d'en créer une nouvelle (`SHOW TABLES IN giny_world;`).
- `ServerRoleEnum.Administrator` (`Role=5`) pour les commandes admin.
- États UI custom = champs **statiques** de classe (la `ChatFrame` est
  recréée → un champ d'instance perd son état).

## À NE PAS faire

- Modifier les XML UI du SWF (`Ankama_Social/ui/friends.xml` etc.) — corrompt
  le SWF. Faire des panneaux Sprite custom à la place.
- `docker compose down -v` sans demander → wipe la DB.
- Lancer `/ultrareview` soi-même — c'est utilisateur uniquement.

## Debug

- Log SWF : `client/build/OneAir.app/Contents/Resources/oneair-debug.log` (rolling).
- Log serveur : `docker logs giny-{auth,world,web}`.
- DB : DBGate via `/dbgate/` (gated par session admin) ou direct
  `docker exec giny-mysql mysql -u root -p"$MYSQL_ROOT_PASSWORD"`.
- Crash silencieux SWF : vider le log, scénario unique, grep sur
  `'DD\|ITEMUI\|ONEAIR'`.

## Quand l'utilisateur signale un bug

1. Lire `oneair-debug.log` AVANT de proposer un fix.
2. Relire le source AS3 / .cs / .go *actuel* — ne pas se baser sur la mémoire
   d'une session précédente.
3. Fix minimal + redéploy.
4. Mettre à jour README/CLAUDE seulement si le fix change un comportement
   documenté ou ajoute une règle apprise.

## Mots de passe & secrets

`.env` (gitignoré) contient `MYSQL_ROOT_PASSWORD`, `MYSQL_PASSWORD`,
`WEB_SESSION_KEY` (32 bytes hex). Régénérer :

```bash
openssl rand -hex 32                         # WEB_SESSION_KEY
./scripts/create-account.sh <user> <pw> 5    # admin in-game (Role=5)
```

Auth admin = `/api/public/login` avec un compte `Role >= 5` (pas de
ADMIN_USERNAME/PASSWORD séparés).
