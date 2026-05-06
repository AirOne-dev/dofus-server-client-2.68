# CLAUDE.md — Notes pour Claude Code

Guide opérationnel pour assister sur ce dépôt **OneAir** (serveur Dofus 2.68
privé + client macOS native).

## Règle d'or

**À chaque modification notable** (nouveau patch SWF, nouvelle commande chat,
nouveau composant, changement d'arborescence, etc.) :

1. Mettre à jour `README.md` (section "Ce qui marche", "Commandes admin custom",
   ou "Structure du projet" selon le cas).
2. Mettre à jour ce fichier `CLAUDE.md` si la modification change un workflow
   ou ajoute une règle (build, déploiement, debug, etc.).
3. **Publier un article sur le site public** — voir la *Règle d'or n°1bis*
   ci-dessous. C'est obligatoire dès que le changement est visible côté
   joueur (UI, gameplay, contenu, performance, événement).

Ne pas attendre que l'utilisateur le demande — c'est partie intégrante de
chaque tâche.

## Règle d'or n°1ter — Rebuild auto du site web après modif

**Dès qu'un fichier sous `server/web/` est modifié** (`*.go`, `templates/*.html`,
`static/*.css`, `static/*.js`, `Dockerfile`), **rebuild et recreate le container
`giny-web` sans demander confirmation** — c'est la seule façon que les
changements soient visibles sur `http://localhost:3000`.

Procédure standard (à exécuter sans rien demander de plus) :

```bash
# 1. Backup DB (Règle d'or n°2 — giny-web fait partie des giny-*)
docker exec giny-backup /backup.sh 2>/dev/null \
  || docker exec giny-mysql sh -c 'mysqldump --single-transaction -u root \
       -p"$MYSQL_ROOT_PASSWORD" giny_auth giny_world | gzip' \
     > server/backups/manual-$(date +%Y%m%d-%H%M%S).sql.gz

# 2. Rebuild + recreate (web uniquement, ne touche pas auth/world)
docker compose build web \
  && docker compose up -d --no-deps --force-recreate web

# 3. Vérifier que le container est UP et écoute bien
docker logs --tail=20 giny-web
curl -sI http://localhost:3000/ | head -3
```

Cas particuliers :

- **Modif uniquement `static/*` ou `templates/*`** : le rebuild est nécessaire
  car les assets sont embarqués via `go:embed` dans le binaire. Pas de
  hot-reload possible — toujours passer par `docker compose build web`.
- **Modif `Dockerfile`** ou ajout de dépendance Go (`go.mod` / `go.sum`) :
  même procédure, le build embarquera les nouvelles deps.
- **Modif `docker-compose.yml`** (env vars, volumes, ports) : `docker compose
  up -d --force-recreate web` suffit, pas de `build` nécessaire si l'image
  n'a pas changé.
- **Erreur de build Go** : ne pas tenter de "fixer" en bypassant — corriger
  le source (le `go build` local depuis `server/web/` reproduit l'erreur
  rapidement, sans Docker).

Après chaque rebuild, **tester au moins** :
- `curl -s http://localhost:3000/ | head -20` retourne du HTML (pas une 500)
- Si la modif touche la landing, ouvrir le navigateur sur `/`
- Si la modif touche l'admin, ouvrir `/admin` (login admin)

Cette règle s'applique aussi aux modifs en cascade (ex : ajout d'un endpoint
Go + nouvelle UI dans la landing) — un seul rebuild suffit pour le tout.

## Règle d'or n°1bis — Article de changelog **uniquement pour ce qui touche le joueur**

Les articles publiés sur la landing publique sont **adressés aux joueurs**,
pas aux contributeurs/devs. Donc on publie un article **seulement** quand
le changement est perceptible/utile côté joueur :

✅ À publier :
- Nouveau contenu (donjon, zone, monstre, item, classe…)
- Changement de gameplay (rééquilibrage, nouveau sort, nouvelle commande
  chat utilisable par tous, multiplicateurs XP/Drop modifiés…)
- Bugfix visible côté joueur (sortie de bâtiment qui marche, NPC qui
  répond, item qui s'équipe…)
- Annonce serveur (downtime planifié, reboot, événement, concours…)
- Sortie d'un client (nouveau bundle Windows/macOS dispo au téléchargement)

❌ À NE PAS publier :
- Refactorings serveur/client invisibles côté jeu
- Mise à jour de la chaîne de build (Docker, cross-compile, scripts CI…)
- Détails d'implémentation (patch SWF, sed Dockerfile, schéma SQL…)
- Optimisations perf qui ne changent rien à l'expérience
- Tout ce qui ressemble à un commit message technique

Si on doute → c'est probablement non. L'utilisateur du jeu se moque de
*comment* on a réglé un truc, il veut savoir *que* le truc marche.

### Comment publier

```bash
# Édite un article.md en local avec frontmatter YAML :
cat > /tmp/article.md <<'EOF'
---
title: Le donjon des Bouftous est ouvert
slug: donjon-bouftous-ouvert
tag: patch
excerpt: Le donjon -22,-22 est désormais accessible aux personnages niveau 30+.
---
## Tout est prêt

Le donjon des Bouftous accepte les groupes de 1 à 8 joueurs…
EOF

./scripts/publish-article.sh /tmp/article.md
# → ==> Publié : http://localhost/article/donjon-bouftous-ouvert
```

Le script fait un UPSERT direct en DB (re-publier le même slug = mise à
jour). Tags acceptés : `patch`, `annonce`, `event`, `devblog`, `fix`.
`devblog` est OK uniquement si le contenu est *narratif* et *raconté pour
le joueur* (ex : « les coulisses du nouveau donjon »), pas pour des notes
techniques.

Pas besoin de redémarrer — l'admin et la landing tapent directement la
table `oneair_articles`.

## Règle d'or n°2 — Backup avant chaque restart Docker

**Toujours déclencher une sauvegarde DB avant de couper, recreate, ou rebuild
un container giny-*** (auth, world, mysql, web, ou `docker compose down`,
`docker compose restart`, `docker compose up -d --force-recreate`, etc.).

```bash
# Backup synchrone immédiat (utilise le service `backup` déjà démarré)
docker exec giny-backup /backup.sh
# OU dump direct si le service backup n'est pas dispo :
docker exec giny-mysql sh -c 'mysqldump --single-transaction -u root \
  -p"$MYSQL_ROOT_PASSWORD" giny_auth giny_world | gzip' \
  > server/backups/manual-$(date +%Y%m%d-%H%M%S).sql.gz
```

Vérifier que le fichier est bien créé dans `server/backups/` AVANT de lancer
le `docker compose ...` qui touche aux containers. Le service `backup` tourne
en background avec rotation à 20 mais la fenêtre entre deux dumps automatiques
est de plusieurs minutes — un restart pile entre deux peut perdre les actions
récentes.

Cette règle s'applique même pour des opérations "safes" (ex: `--force-recreate
auth world`) parce que :
- un patch foireux peut crasher le world au boot et corrompre l'état mémoire
  non sauvegardé,
- les changements de schema (notamment via `EnsureSchema()` côté managers
  OneAir) sont irréversibles sans backup préalable,
- une erreur de manip humaine (`down -v` au lieu de `restart`) wipe tout.

## Architecture (rappel court)

- `server/` — image Docker .NET 6 qui clone **Giny.NETCore** (branche `2.68`)
  + applique des patches via `Dockerfile` (sed) + copie `OneAirChatCommands.cs`.
  Deux containers : `giny-auth` (5555) et `giny-world` (5556), partagent
  l'image `giny/server:latest`. MySQL et DBGate à côté.
- `client/` — sources des deux clients :
  - `OneAirLauncher/` (Swift macOS) + `dofus-darwin-2.68/` → bundle final
    `OneAir.app/` à la racine du projet, généré par `build-app-darwin.sh`
    (Mac natif) ou `build-docker-darwin.sh` (Linux + Docker, cf. plus bas).
  - `OneAirLauncher-win/` (C# WPF .NET 8) + `dofus-windows-2.68/` → bundle
    final `OneAir-Windows/` à la racine du projet, généré par
    `build-app-windows.sh` (Linux natif) ou `build-docker-windows.sh`
    (Linux + Docker, sans dotnet/Go installés sur l'hôte).
  - `zaap-server/` (Go) — code commun, cross-compile des deux côtés
    (`zaap-server` macOS + `zaap-server.exe` Windows).
  - `DofusInvoker-patched.swf` — **partagé** entre les deux plates-formes,
    c'est le même fichier injecté dans les deux bundles.
  - `Dockerfile.windows` + `Dockerfile.darwin` — images de build qui
    encapsulent toute la toolchain (.NET 8, Swift 6, Go 1.22, rcodesign).
    Voir section *Build clients en Docker* plus bas.
- `OneAir.app/Contents/Resources/DofusInvoker.swf` — c'est le SWF **vivant**
  utilisé par le client macOS. C'est lui qu'on patche en mode dev (et qu'on
  recopie ensuite dans `client/DofusInvoker-patched.swf` comme canonique
  pour les deux plates-formes).
- `OneAir-Windows/DofusInvoker.swf` — copie du même SWF, posée à la
  racine du bundle Windows (à côté de `Dofus.exe`).

## Workflow patch SWF (le plus utilisé)

Le SWF actif côté client est patché par injection AS3 via FFDec (`-importScript`).

```bash
# 1. Édition source (la source AS3 est dans /tmp pour rapidité)
$EDITOR /tmp/scripts-import/com/ankamagames/dofus/logic/game/common/frames/ChatFrame.as

# 2. Re-compilation de la classe dans le SWF (~30 s)
java -jar /tmp/ffdec/ffdec.jar -importScript \
  /tmp/DofusInvoker-base.swf \
  /tmp/DofusInvoker-debug.swf \
  /tmp/scripts-import

# 3. Déploiement
cp /tmp/DofusInvoker-debug.swf \
   ./OneAir.app/Contents/Resources/DofusInvoker.swf
cp /tmp/DofusInvoker-debug.swf \
   ./client/DofusInvoker-patched.swf  # backup canonique

# 4. Relance
pkill -f dofus-real; pkill -f OneAir; pkill -f zaap-server
xattr -cr ./OneAir.app
codesign --force --deep -s - ./OneAir.app
rm -f ./OneAir.app/Contents/Resources/oneair-debug.log
open ./OneAir.app
```

`/tmp/DofusInvoker-base.swf` est le SWF "vanilla patché" (avant nos injections)
qu'on garde immuable comme point de départ. **Ne pas régénérer.**

## Workflow patch serveur

```bash
# 1. Édition (le .cs est COPYé dans le Dockerfile build context)
$EDITOR ./server/OneAirChatCommands.cs

# 2. Rebuild image (auth/world partagent giny/server:latest)
cd .
docker compose build auth        # builds image puis "auth" et "world" la réutilisent

# 3. Recreate containers avec la nouvelle image
docker compose up -d --no-deps --force-recreate auth world

# 4. Logs
docker logs -f giny-world
```

Compilation .NET 6 dans le builder du Dockerfile (~1–2 min en cold cache,
quelques secondes en warm).

## Workflow build client Windows

Pendant exact du `build-app-darwin.sh`, mais sortie à la racine dans
`OneAir-Windows/` (pas `.app`, c'est un dossier plat).

```bash
# 0. Pré-requis — toolchains (à faire UNE fois sur la machine de build) :
#    - Go 1.22+ (apt install golang-go)
#    - dotnet SDK 8.0 OFFICIEL Microsoft (le pkg apt n'inclut pas le
#      Microsoft.NET.Sdk.WindowsDesktop nécessaire pour WPF cross-compile) :
#         curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
#         sudo mkdir -p /opt/dotnet && sudo chown $USER /opt/dotnet
#         bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /opt/dotnet
#         export PATH=/opt/dotnet:$PATH
#    - cytrus-downloader (Go) — cf. https://github.com/loonaire/Cytrus-downloader
#         Sert UNE fois pour récupérer les binaires Dofus 2.68 Windows :
#         /tmp/cytrus-downloader -game dofus -platform windows \
#             -release main -version 6.0_2.68.0.0 \
#             -outdir client/dofus-windows-2.68/

# 1. Édition source du launcher (XAML + C#)
$EDITOR client/OneAirLauncher-win/MainWindow.xaml
$EDITOR client/OneAirLauncher-win/MainWindow.xaml.cs

# 2. Build bundle complet (script auto-build le launcher si besoin)
./client/build-app-windows.sh

# 3. Le bundle final est dans OneAir-Windows/, prêt à dézipper côté Windows.
#    Endpoint /download/windows zippe ce dossier à la volée (cf. landing.go).

# 4. Si modif du launcher uniquement, build manuel rapide :
cd client/OneAirLauncher-win
dotnet publish -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:EnableWindowsTargeting=true
# → bin/Release/net8.0-windows/win-x64/publish/OneAirLauncher.exe (~70 MB)
# Puis re-run build-app-windows.sh pour intégrer dans le bundle.
```

Pièges Windows / WPF / AIR captif :

- **Le launcher remplace `Dofus.exe`** dans le bundle, et l'AIR captive
  exe original est renommé `dofus-real.exe`. C'est exactement le même
  hack que `Contents/MacOS/Dofus` → `dofus-real` côté macOS. AIR ne
  vérifie pas le nom de l'exe au runtime tant qu'on a retiré
  `META-INF/AIR/hash` et `META-INF/signatures.xml` du bundle (le
  `build-app-windows.sh` le fait).
- **`credentials.json` à la racine du bundle** (à côté de Dofus.exe),
  pas dans un sous-dossier — le SWF Giny patché lit
  `File.applicationDirectory.resolvePath("credentials.json")` ce qui
  résout à la dir de `dofus-real.exe` (== bundle root).
- **Pas d'`execv` sous Windows** : le launcher C# `Process.Start`
  `dofus-real.exe`, **puis quitte** via `Environment.Exit(0)` pour
  rendre la main au nouveau process. Pas de double-fenêtre.
- **Single-file publish** embarque le runtime .NET 8 + WPF dans le
  binaire (~70 MB). Première exécution : .NET extrait les natives DLL
  dans `%LOCALAPPDATA%\Temp\.net\OneAirLauncher\…` (silencieux).
- **WPF cross-compile depuis Linux** nécessite le pkg dotnet officiel
  Microsoft (et non `apt-get install dotnet-sdk-8.0` qui n'inclut pas
  `Microsoft.NET.Sdk.WindowsDesktop`). Le flag CLI critique est
  `-p:EnableWindowsTargeting=true`.
- **Logs joueur** : `%LOCALAPPDATA%\OneAir\Logs\launcher.log` et
  `…\zaap-server.log`. Settings persistés dans
  `%APPDATA%\OneAir\settings.json` (multi-comptes + IP/port custom).
- **Pas de signature de code** pour l'instant — Windows SmartScreen
  affiche un avertissement au 1er lancement. L'utilisateur clique
  "Plus d'infos" → "Exécuter quand même".
- **Cytrus 2.68 versions** : le manifest CDN expose officiellement le
  `latest` du release `main` (actuellement 2.73). Pour 2.68 il faut
  passer `-version 6.0_2.68.0.0` explicitement à cytrus-downloader.
  L'URL `https://cytrus.cdn.ankama.com/dofus/releases/main/windows/6.0_2.68.0.0.manifest`
  reste accessible (HTTP 200) pour l'instant.

## Build clients en Docker (workflow recommandé sur Linux)

Sur une machine Linux sans toolchain installée (.NET, Swift, Go), on peut
builder les deux clients **uniquement avec Docker**. Deux images dédiées,
construites à la volée par les wrappers :

```bash
# Bundle Windows complet (Dofus.exe WPF + zaap-server.exe + assets)
./client/build-docker-windows.sh   # → OneAir-Windows/ + dist/OneAir-Windows.zip

# Bundle macOS complet (assembly, zaap-server Mach-O, signature ad-hoc,
# cross-compile launcher Swift via SDK Darwin auto-assemblé)
./client/build-docker-darwin.sh    # → OneAir.app/ + dist/OneAir-MacOS.zip
```

Chaque wrapper finit par `zip -ryq0` du bundle dans `dist/<nom>.zip`
(Store, pas de compression : le bundle contient déjà des assets binaires
compressés, gzip n'aiderait pas). Le service `web` mount `./dist:/app/dist:ro`
et `/download/{macos,windows}` sert ces fichiers via `http.ServeFile` —
téléchargement instant + Range supporté (resume), plus aucun zip à la
volée à chaque clic. Fallback automatique sur zip on-the-fly si dist/ vide.

Première exécution : ~3 min pour fetch les images de base + outils, plus
~3-5 min côté macOS pour assembler le Swift SDK Darwin (cf. plus bas).
Runs suivants : ~30 s grâce au cache des layers + volumes Go nommés
(`oneair-go-cache`, `oneair-go-mod`) + `oneair-swiftpm` pour le SDK.

### Image Windows (`Dockerfile.windows`)

Base `mcr.microsoft.com/dotnet/sdk:8.0` (Debian bookworm) + Go 1.22 + outils
shell. Cross-compile **complète** sans dépendance hôte :
- `dotnet publish ... -p:EnableWindowsTargeting=true` produit le launcher
  WPF self-contained single-file (PE32+ GUI x86-64, ~70 MB).
- `GOOS=windows GOARCH=amd64 go build` produit `zaap-server.exe` (PE32+
  console x86-64).

### Image macOS (`Dockerfile.darwin`)

Base `swift:6.0-jammy` (Ubuntu 22.04) + Go 1.22 + **rcodesign**
(implémentation Rust open-source d'`apple codesign`, prebuilt
x86_64-unknown-linux-musl, version 0.29.0). Ce que le container fait
**tout seul** :
- Cross-compile `zaap-server` en Mach-O darwin (par défaut arm64, override
  via `ZAAP_GOARCH=amd64`).
- Assemble le bundle (cp, rsync, plistlib pour Info.plist).
- Signe ad-hoc le bundle complet avec `rcodesign sign $APP_DIR` — recurse
  sur Adobe AIR.framework (Resources/A2712Enabler, WebKit.dylib, Adobe
  AIR), Contents/MacOS/dofus-real, Contents/MacOS/zaap-server, et le
  main exec Contents/MacOS/Dofus.

### Cross-compile launcher Swift depuis Linux (auto, Swift SDK Darwin)

Le compilo Swift Linux ne sait pas cibler Mach-O Darwin sans un Swift SDK
Darwin (frameworks AppKit/Cocoa). On en assemble un nous-mêmes en stitchant
trois sources publiques **sans Apple ID** :

1. **macOS SDK 11.3** (frameworks/headers) — `phracker/MacOSX-SDKs`, mirror
   GitHub public. Note légale : l'EULA Xcode interdit techniquement l'usage
   du SDK hors hardware Apple, c'est de la zone grise comme tout setup
   osxcross. Limité à 11.3 (le mirror n'a pas plus récent).
2. **Toolchain Swift 6.0.3 macOS** (libswiftCore Darwin + module
   interfaces) — `swift.org`, officiel Apple, public, **pas d'Apple ID
   requis**. C'est un .pkg de 1.4 GB qu'on extrait via `7z` (xar) +
   `cpio`.
3. **Toolset LLVM Linux precompiled** (`ld64.lld`, cctools) — `xtool-org/
   darwin-tools-linux-llvm`. C'est ce qui linke en Mach-O depuis Linux.

`client/make-darwin-sdk.sh` automatise tout : download → extract → assemble
fake Developer dir → patche `swift-sdk-darwin/build.sh` pour mode
MacOSX-only → repack en `.artifactbundle.zip`. Sortie :
`client/.cache/darwin.artifactbundle.zip` (~700 MB, gitignoré).

**On n'appelle pas ce script manuellement** : `build-docker-darwin.sh` le
lance tout seul si le cache est absent (étape 2/4 du wrapper). Premier run
→ ~3-5 min supplémentaires. Runs suivants → no-op (cache hit).

Pour skip le cross-compile et utiliser le binaire pré-compilé committed :
`ONEAIR_SKIP_SWIFT_SDK=1 ./client/build-docker-darwin.sh`.

### Layout SwiftPM du launcher

`OneAirLauncher.swift` est un fichier solo de 1461 lignes avec du top-level
code (`let app = NSApplication.shared; app.run()` à la fin). SwiftPM exige
que les sources avec top-level code soient nommées `main.swift`, donc le
`Package.swift` pointe sur `Sources/OneAirLauncher/main.swift` qui est un
**symlink** vers `../../OneAirLauncher.swift`. Pas de duplication, le
fichier canonique reste à la racine pour l'ancien build natif Mac
(`swiftc -O ...`).

### Pièges connus

- **Warning linker** : `libswiftCore.dylib has version 13.0.0, which is
  newer than target minimum of 11.0.0`. C'est juste un warning, le binaire
  fonctionne. Cause : phracker mirror s'arrête à SDK 11.3 mais Swift 6.0.3
  livre des libs compilées pour macOS 13. Bumper `platforms: [.macOS(.v13)]`
  dans `Package.swift` pour le faire taire si ça gêne.
- **Première install du SDK dans le container** : `swift sdk install`
  unpacke 700 MB → ~25 s. Le volume Docker nommé `oneair-swiftpm` persiste
  l'install entre runs, donc seul le 1er build paie ce coût.
- **`Sources/OneAirLauncher/`** est gitignoré (le symlink est généré par
  `Package.swift`). Le canonique est `client/OneAirLauncher/OneAirLauncher.swift`.

### Détection Mac vs Linux dans `build-app-darwin.sh`

Le script unique fonctionne dans les deux environnements via le bloc :

```bash
case "${OSTYPE:-$(uname -s)}" in
    darwin*|Darwin) IS_MACOS=1 ;;
    *)              IS_MACOS=0 ;;
esac
```

Branches conditionnelles :
| Outil    | Mac natif        | Linux/Docker                          |
|----------|------------------|---------------------------------------|
| Plist    | `plutil -replace`| Python `plistlib.load/dump`           |
| Signature| `codesign -s -`  | `rcodesign sign` (sans key = ad-hoc)  |
| Xattrs   | `xattr -drc`     | skip (no-op sur Linux)                |
| zaap-srv | `go build`       | `GOOS=darwin GOARCH=$ZAAP_GOARCH`     |
| Launcher | `swiftc -O ...`  | copie binaire pré-buildé              |

### Quand rebuild les images Docker

Cache layers Docker = invalidé si :
- modif `Dockerfile.{windows,darwin}` (forcément),
- bump Go ou rcodesign via `--build-arg`.

Pas besoin de rebuild si :
- modif `build-app-{windows,darwin}.sh` ou des sources launcher : le
  projet est monté en volume, donc les changements sont visibles
  immédiatement.

## Pièges AS3 / FFDec — appris à la dure

- **FFDec compile mal les inner functions** déclarées style `function foo(){}`.
  Toujours utiliser `var foo:Function = function(){};`.
- **Closures forward-references** : si `closureA` appelle `closureB` déclarée
  plus loin dans le même scope, FFDec génère du bytecode qui throw `Error #1065`
  ("Variable not defined") au runtime. Solution : extraire en méthode statique
  ou en variable Function déclarée avant utilisation.
- **IIFE typée `:Function`** style `function(...):Function { return function(){}; }(args)` :
  FFDec compile ça comme `new Function(stringBody)` et throw `Error #1066`
  ("The form function('function body') is not supported"). Solution : stocker
  les paramètres bruts dans une `Dictionary` et invoquer via une méthode statique
  qui reconstruit l'appel (cf. `_oneAirDDOptionRegistry` + `oneAirDDStageMouseDown`).
- **JSON natif indisponible** dans le runtime AIR utilisé. Utiliser
  `com.ankamagames.jerakine.json.JSON.decode(s)`.
- **`MouseEvent.CLICK`** sur des Sprite ajoutés dynamiquement à la stage peut
  être intercepté par le framework Berilia — utiliser plutôt un listener
  **stage en mode capture** (`useCapture=true`, priorité élevée) qui bypasse
  les intercepteurs intermédiaires. Cf. `oneAirDDStageMouseDown` dans
  `ChatFrame.as` pour le pattern dropdown.
- **Pas d'erreur visible côté UI** : un patch SWF qui crash à l'exécution ne
  remonte rien à l'utilisateur — toujours wrapper le code custom dans un
  `try/catch` avec écriture vers `oneair-debug.log` via `oneAirLog()` /
  équivalent. Le log est dans `OneAir.app/Contents/Resources/oneair-debug.log`.
- **`Inventory.GetItems()`** côté serveur retourne tous les items y compris
  équipés. Pour une visualisation triée, ordonner par `IsEquiped() ? 0 : 1`
  puis `Position`. Modifier un item équipé nécessite `Character.RefreshStats()`
  pour resynchroniser les stats actives avec le tooltip.

## Marqueurs custom dans les messages serveur → client

On utilise des préfixes `__ONEAIR_*__` dans les `TextInformationMessage` (chat
système) que le SWF intercepte et désaffiche du chat avant de router :

| Préfixe | Émis par | Consommé par (SWF) |
|---|---|---|
| `__ONEAIR_PLAYERS__` | `.who` côté serveur | panneau `.online` |
| `__ONEAIR_INV__[...]` | `.itemdump` / mutations effets | éditeur `.itemui` |

L'interception se fait dans `ChatFrame.as` (handler de `TextInformationMessage`)
et marque `e.preventDefault()` côté Berilia pour que le payload n'apparaisse
pas en chat.

## Debug

- **Log SWF** : `OneAir.app/Contents/Resources/oneair-debug.log` (rolling).
  `oneAirLog("...")` côté AS3 y écrit. `_ot()` y trace les messages framework.
- **Log serveur** : `docker logs giny-world` / `docker logs giny-auth`.
- **DB** : DBGate sur <http://localhost:3000>.
- **Crash silencieux d'un patch SWF** : vider le log, tester un seul scénario,
  filtrer le log avec `grep 'DD\|ITEMUI\|ONEAIR'`.
- **Actions joueur non gérées** : onglet **Non géré** du panel admin
  (`/admin#unhandled`) — captures live des items sans handler, sorts sans
  effect, NPCs muets, paddocks, messages réseau orphelins. Bouton
  *Copier pour Claude* qui produit un Markdown auto-suffisant. Cf. section
  ci-dessous.

## Capture des actions non gérées (`OneAirUnhandledLogger`)

Manager côté world (`server/OneAirUnhandledLogger.cs`) qui sérialise dans la
table `oneair_unhandled_log` chaque action joueur que Giny ne sait pas
exécuter. But : récolter le contexte serveur exactement comme on l'a fait
pour les patchs SWF, puis demander à Claude d'implémenter le manque. Sans ce
filet, la grosse majorité des trous (paddock, mount equip, alliance,
spouse, item consommable custom...) sont silencieux côté serveur.

**Hooks installés** (sed dans le `Dockerfile`, patches 24-34) :

| Catégorie       | Source Giny                                     | Cas typique                                |
|-----------------|-------------------------------------------------|--------------------------------------------|
| `item_use`      | `ItemsManager.UseItem`                          | item consommable sans `[ItemUsageHandler]` |
| `item_use_error`| `ItemsManager.UseItem` (catch)                  | exception pendant `Invoke`                 |
| `item_effect`   | `ItemEffectsManager.AddEffects`                 | effet d'item équipé sans `[ItemEffect]`    |
| `spell_effect`  | `DefaultSpellCastHandler.Initialize`            | effet de sort sans `[SpellEffectHandler]`  |
| `generic_action`| `GenericActionsManager.Handle`                  | enum sans handler (ex: `Paddock`)          |
| `interactive`   | `GenericActions.HandleUnhandled`                | élément interactif marqué Unhandled        |
| `interactive_err`| `MapInstance.UseInteractive`                   | clic sur élément non dispatchable          |
| `npc_action`    | `Npc.InteractWith`                              | NPC sans `NpcActionRecord` pour le type    |
| `exchange_request`| `ExchangesHandler.HandleExchangePlayerRequest` | type d'échange non implémenté              |
| `net_message`   | `WorldClient.OnMessageUnhandled`                | message protocol sans `[MessageHandler]`   |
| `net_error`     | `WorldClient.OnHandlingError`                   | exception dans un handler protocol         |

Le logger est :

- **non-bloquant** : queue mémoire flushée par batch toutes les 2 s.
- **non-throwing** : tous les `LogXxx()` sont try/catch — un crash du logger
  ne casse jamais le code Giny appelant.
- **anti-spam** : dédup `(charId|category|detail)` pendant 30 s pour éviter
  qu'un effet appliqué à chaque tick combat sature la table.
- **auto-purge** : > 30 jours dropped, et cap à 500 lignes par catégorie.

**Ajouter une catégorie / un nouveau hook** :

1. Ajouter un `public const string CatXxx = "xxx";` + une méthode
   `LogXxx(...)` dans `OneAirUnhandledLogger.cs`.
2. Ajouter un sed dans `Dockerfile` qui hooke le point d'appel Giny (le
   plus souvent juste avant un `ReplyWarning` ou un `Send(ErrorMessage)`).
3. Ajouter la ligne descriptive dans la table de `formatUnhandledMarkdown`
   (`server/web/unhandled.go`) — c'est elle qui apparaît dans le rapport
   "Copier pour Claude".
4. Rebuild : `docker compose build auth web && docker compose up -d
   --no-deps --force-recreate auth world web`.

**API admin** (auth-gated) :

- `GET /api/unhandled?category=&characterId=&limit=&since=` → JSON list +
  `byCategory` count + `total`.
- `GET /api/unhandled?format=md&...` → text/markdown auto-suffisant pour
  Claude.
- `DELETE /api/unhandled?category=&characterId=&id=&all=1` → purge.

## Bindings interactifs globaux (sortie de bâtiment)

Le système de bindings OneAir (`oneair_havenbag_interactives`, BonesId →
type) est étendu pour fonctionner **hors havre-sac**, ce qui sert pour les
éléments qui apparaissent sur n'importe quelle map du jeu.

Cas actuellement géré :

- `bones=3507` → `exit` : étoile au sol qui sort d'un bâtiment.
  **Déclenchement on-walk uniquement** (`OnMovementConfirmed`) : aucun
  `InteractiveSkillRecord` n'est posé sur ces éléments. Pourquoi : avec
  un skill posé, le client 2.68 traite la cellule en *click-to-use* et
  fait pathfind sur une cellule **adjacente** au lieu de marcher sur
  l'étoile — résultat, le perso s'arrête juste avant et le hook on-walk
  ne fire jamais. Sans skill, le clic redevient un déplacement standard
  et pose le perso pile sur la cellule. Trade-off : pas de curseur "use"
  spécifique au survol (le curseur est le curseur de marche standard).
- ⚠️ `TeleportersManager.Initialize` parse `Param1` en int sur les rows
  Zaap/Zaapi. Le **Patch 20bis** (sed Dockerfile) remplace `int.Parse`
  par `TryParse + continue` pour que toute row mal formée (Param1=NULL
  ou non numérique) soit skippée proprement au boot. C'est défensif :
  une expérimentation précédente avait inséré 1078 rows Action=Zaap,
  Param1=NULL qui crashaient le 2ᵉ boot. Le patch reste en place même
  si on ne génère plus ces rows, pour blinder le code.
- `TryExitBuilding(character)` cherche la map de sortie via
  **`MapRecord.GetMaps(current.Position.Point)`** — c'est la même
  logique que `.relative` côté admin : l'extérieur d'un bâtiment partage
  les coordonnées world (x, y) avec son intérieur, et a `Outdoor=true`.
  Fallbacks dans l'ordre : sibling Outdoor → sibling autre →
  voisin worldmap *qui correspond à un MapRecord chargé* → SpawnPointMapId.
  ⚠️ Ne PAS retomber sur `TopMap`/`BottomMap`/etc. sans filtrer par
  `MapRecord.GetMap(...) != null` : ces champs pointent souvent vers des
  ids non chargés à l'intérieur des bâtiments (cas vu : `id=58465797`).
- Cellule de destination via `FindEntranceCellOrRandom(outdoor, indoorId)` :
  cherche sur la map extérieure l'élément interactif dont
  `Skill.ActionIdentifier=Teleport` et `Skill.Param1=indoorMapId` (la
  porte d'entrée du bâtiment), puis utilise sa cellule sud-ouest si
  walkable, sinon le premier voisin walkable. À défaut, cellule random.
  Le joueur sort donc *au pied de la porte*, pas au milieu de la map.

Pour ajouter un nouveau binding global :

1. Déclarer le binding dans `LoadInteractiveBindings()` :
   `EnsureGlobalBinding(BONES_ID, "type_name");` — ça insère en DB +
   cache mémoire.
2. Étendre `HandleZaapInteraction` (`else` hors havre-sac) pour gérer le
   nouveau type.
3. Étendre `TryHandleInteractive` aussi pour le cas où on hooke sur
   `MapInstance.UseInteractive` directement.
4. Rebuild auth.

**Limite** : les nouveaux bindings ne deviennent cliquables qu'après le
prochain reboot du world (le scan `EnsureGlobalInteractiveSkillsAsync` ne
tourne qu'au boot). Pour un déploiement chaud, on peut soit forcer le scan
manuellement (à exposer dans le panel admin), soit attendre le prochain
restart prévu.

## Site public (`/`)

Le service `web` sert maintenant **deux interfaces** sur le port 3000 :

- `/` → **landing publique** (no auth). Hero "L'aventure recommence",
  présentation OneAir, grille des 18 classes, articles, login joueur,
  CTA téléchargement macOS, footer.
  - Template : `server/web/templates/landing.html`
  - Style : `server/web/static/landing.css` (palette parchment / gold /
    bois sombre, typo Cinzel + Cormorant Garamond + Inter)
  - JS : `server/web/static/landing.js` (polling status, login player,
    rendu des personnages, détail XP/kamas/items)
- `/admin` → ancien dashboard admin (auth-gated). Sidebar inchangée +
  un onglet **Articles** pour publier les changelogs.
- `/login` → login admin (inchangé).
- `/article/{slug}` → page article SSR (rendue en Markdown léger par
  `landing.go::renderMarkdown`).
- `/download/macos` → 302 vers `DOWNLOAD_MACOS_URL` (env). Si vide,
  page "bientôt disponible".

### Authentification joueur

Cookie séparé `oneair_player` (signature HMAC, body préfixé `p|` pour
distinguer du cookie admin `oneair_admin`). Les credentials sont vérifiés
contre `giny_auth.accounts` (mots de passe en clair côté Giny). Comptes
bannis bloqués au login. TTL 7 jours.

Endpoints publics (no auth) :
- `POST /api/public/login` `{username, password}` → set cookie
- `POST /api/public/logout` → clear cookie
- `GET  /api/public/me` → `{loggedIn, username, characters[]}`
- `GET  /api/public/character?id=N` → détails (XP, kamas, position, items)
- `GET  /api/public/status` → status léger pour widget
- `GET  /api/public/articles` / `/api/public/articles/{slug}`

### Articles (table `oneair_articles`)

| Colonne | Type | Note |
|---|---|---|
| Id | INT auto | PK |
| Slug | VARCHAR(160) UNIQUE | a-z, 0-9, tirets |
| Title | VARCHAR(255) | |
| Excerpt | VARCHAR(512) | accroche home |
| Content | MEDIUMTEXT | Markdown léger |
| Author | VARCHAR(64) | défaut = admin user |
| CoverImage | VARCHAR(512) | URL absolue (https://… ou /…) |
| Tag | VARCHAR(64) | `patch`/`annonce`/`event`/… |
| Published | TINYINT | 0 brouillon, 1 publié |
| CreatedAt / UpdatedAt | DATETIME | |

Schéma créé via `ensureArticlesSchema()` au boot (idempotent).
Un article de bienvenue (`bienvenue-sur-oneair`) est inséré
automatiquement si la table est vide.

Markdown rendu côté serveur (sécurisé : échappement HTML systématique
avant remplacement, URLs `http(s)://` ou `/…` uniquement).

## Site web (`server/web/`)

Service Go single-binary qui remplace dbgate. Stack :
- `main.go` (~700 lignes) : routing stdlib `net/http`, MySQL via
  `github.com/go-sql-driver/mysql`, sessions HMAC cookie. Inclut désormais
  les routes publiques (`/`, `/article/`, `/download/`, `/api/public/*`).
- `landing.go` : handler de la landing publique + rendu article + Markdown.
- `articles.go` : CRUD articles (admin + public).
- `player.go` : auth joueur + APIs `/api/public/me` & `/character`.
- `templates/login.html`, `templates/app.html`, `templates/landing.html`,
  `templates/article.html` : embed via `go:embed`.
- `static/style.css`, `static/app.js` : SPA admin vanilla JS, polling 5s.
- `static/landing.css`, `static/landing.js` : site public.
- `Dockerfile` : multi-stage Go 1.22 → debian-slim avec mysql-client/gzip
  pour mysqldump et restore.

Routes :
- `/` (landing publique, no auth), `/article/{slug}` (SSR public)
- `/login`, `/logout`, `/admin` (dashboard SPA, auth-gated)
- `/download/macos` (302 → `DOWNLOAD_MACOS_URL`)
- `/api/public/{login,logout,me,character,status,articles,articles/{slug}}` (public)
- `/api/articles` (admin — CRUD articles)
- `/api/status`, `/api/accounts` (CRUD), `/api/characters`,
  `/api/inventory?characterId=N` (GET direct SQL),
  `/api/inventory/parsed?characterId=N` (GET — lit oneair_inventory_dumps,
  effets décodés ; nécessite `dump_inventory` au préalable),
  `/api/backups` (GET/POST trigger/restore/delete),
  `/api/broadcast`, `/api/kick`,
  `/api/action` (POST — dispatch générique vers oneair_actions),
  `/api/actions` (GET — historique 50 dernières)
- `/dbgate/*` : reverse-proxy vers le service `dbgate:3000` (gated par
  session admin). dbgate sert avec `WEB_ROOT=/dbgate` pour que ses URLs
  soient préfixées correctement. Iframé en plein écran dans l'onglet
  "Base de données" (panel `position:fixed` qui shadow le reste).
- `/api/items/catalog?q=&type=&offset=&limit=` : catalogue d'items du jeu,
  paginé. Filtrage par typeId (CSV), recherche par nom (LIKE).
- `/api/spells/catalog?q=&offset=&limit=` : catalogue de sorts.
- `/api/spells/parsed?characterId=N` : sorts décodés (depuis
  `oneair_spell_dumps`, écrit par action `dump_spells`).
- `/spells/{id}.png` : redirect vers DofusDB (`api.dofusdb.fr/img/spells/`).
- `/heads/{id}.png` : sert depuis `/heads-src` (volume monté de
  `OneAir.app/Contents/Resources/content/gfx/heads`, fichier `SmallHead_<id>.png`).
- `/breeds/{id}.png` : redirect vers DofusDB.
- `/items/{gid}.png` : sert les icônes d'items.
  1. Cache local `/items-cache/{gid}.png` (extrait des `.d2p` du client
     OneAir.app, ~555 PNGs au démarrage).
  2. Si absent : lookup `iconId` via `https://api.dofusdb.fr/items/{gid}`
     (cache mémoire dans `iconIDCache:sync.Map`), tente le cache local
     par iconId, sinon redirect 302 vers `https://api.dofusdb.fr/img/items/{iconId}.png`.

Décodeur d2p custom (`server/web/d2p.go`) : footer 24 octets =
6 × uint32 BE (baseOffset, baseLength, indexOffset, indexLength,
propsOffset, propsCount). Extraction parallèle de tous les bitmap*.d2p
montés en `/items-d2p:ro` depuis `OneAir.app/Contents/Resources/content/gfx/items`.

Mutations qui doivent se voir live (broadcast, kick, teleport, give_*,
heal, shutdown, save_now, reload_items, send_pm) → écrit dans
`giny_world.oneair_actions`. Le world consomme cette table via le poller
`server/OneAirActionPoller.cs` (intervalle 1.5 s), patché dans le Dockerfile
en hookant `WorldServer.OnServerStarted`. Voir README pour la liste de
types et payloads.

Pour ajouter un type d'action :
1. Ajouter un `case` dans `Dispatch()` de `OneAirActionPoller.cs`.
2. Ajouter le type dans la whitelist `allowedActionTypes` de `main.go` admin.
3. Ajouter un bouton/forme dans `templates/app.html` qui appelle
   `postAction(type, payload)` côté JS.
4. Rebuild : `docker compose build auth web && docker compose up -d --no-deps --force-recreate auth world web`.

Mots de passe / clé HMAC viennent de `.env` :
- `WEB_SESSION_KEY` (32 bytes hex pour HMAC-SHA256, signe les cookies).
- `MYSQL_ROOT_PASSWORD` (le service web se connecte en root pour pouvoir
  tout faire, cf. `cfg.DSN` dans `main.go`).
- **Pas de `ADMIN_USERNAME`/`ADMIN_PASSWORD` séparés** : l'auth admin
  passe par `/api/public/login` (même endpoint que les joueurs) avec un
  compte `accounts.Role >= 5` dans `giny_auth`. Promotion via
  `scripts/create-account.sh <user> <pw> 5` ou un `UPDATE` direct.

Pour rebuild après modif Go :
```bash
docker compose build web && docker compose up -d --no-deps --force-recreate web
docker logs -f giny-web
```

## Havre-sac (implémentation OneAir)

Giny ne fournit qu'un teleport hardcodé à l'entrée (`HavenBagHandler.cs`
vanilla, 6 lignes). Toute la logique métier est dans nous :

- `server/OneAirHavenBagHandler.cs` : remplace le handler vanilla via
  `COPY` dans le Dockerfile (Patch 13). Route les 11 messages haven-bag
  (Enter/Exit, Edit×3, Furnitures, ChangeRoom/Theme, Open/Close
  FurnitureSequence, ExchangeRequest type=24, DailyLotery) vers
  `OneAirHavenBagPatch`.
- `server/OneAirHavenBagPatch.cs` : manager + dialog custom
  `OneAirHavenBagZaapDialog`. Toutes les actions persistent en DB.

Tables (créées au 1er boot via `init-sql/04_oneair_havenbag.sql` ET via
`OneAirHavenBagPatch.EnsureSchema()` au boot du world) :

| Table | Contient |
|---|---|
| `oneair_havenbag_state` | (CharacterId, PreviousMapId, PreviousCellId, Theme, RoomId, LastLoteryAt) — position pré-havre-sac + thème + loterie |
| `oneair_known_zaaps` | (CharacterId, MapId, DiscoveredAt) — zaaps découverts par le joueur, alimenté à chaque OpenZaap (sed sur `GenericActions.HandleZaap`) |
| `oneair_havenbag_furnitures` | (CharacterId, CellId, FurnitureId, Orientation) — meubles posés dans le havre-sac |

Map intérieure : `162791424` (constante `OneAirHavenBagPatch.HavenBagMapId`).
Une seule room V1 (id 0).

Patches Dockerfile associés :
- **Patch 13** : `COPY` du handler de remplacement et du manager.
- **Patch 14** : sed sur `GenericActions.cs` qui remplace
  `character.OpenZaap(...)` par `OneAirHavenBagPatch.HandleZaapInteraction(...)`.
  Ce hook (a) mémorise le zaap dans `oneair_known_zaaps` quand on est hors
  havre-sac, (b) ouvre `OneAirHavenBagZaapDialog` à la place du `ZaapDialog`
  vanilla quand on est dans le havre-sac.
- **Patch 15** : sed sur `ClassicMapInstance.GetMapComplementaryInformationsDataMessage`
  pour transformer le message en `MapComplementaryInformationsDataInHavenBagMessage`
  quand `character.Map.Id == HavenBagMapId`. Sans ça le client ne sait pas
  qu'il est dans un havre-sac et la UI (boutons coffre/perso) reste à moitié
  initialisée.
- **Patch 16** : sed sur `WorldServer` pour appeler
  `OneAirHavenBagPatch.EnsureSchema()` au boot.

Comportement attendu :
- Touche H ou clic Popoche → `EnterHavenBagRequest` → on sauvegarde
  `(MapId, CellId)` actuel en DB, on téléporte sur 162791424, le wrapper
  `MaybeUpgradeToHavenBag` envoie automatiquement la map info en variante
  havre-sac.
- Re-touche H ou bouton "Sortir" → `ExitHavenBagRequest` → on relit la
  position sauvegardée et on téléporte. Fallback `Record.SpawnPointMapId`
  si vide (déco/reco dans le bag).
- Zaap intérieur → `OneAirHavenBagZaapDialog` qui liste les zaaps connus
  du joueur (gratuit). Le joueur découvre des zaaps en cliquant sur les
  zaaps normaux hors havre-sac (auto via Patch 14).
- Coffre → `ExchangeRequestMessage(HAVENBAG=24)` → on dispatch sur
  `BankExchange` standard (le coffre du havre-sac partage la banque
  vanilla, suffisant pour notre cas).
- Personnalisation → cycle Edit/Save/Cancel + persistance des meubles,
  thème, room en DB.
- Loterie → 100 kamas / 24h, cooldown stocké dans
  `oneair_havenbag_state.LastLoteryAt`.

Quand on étend (ajout d'un nouveau handler haven-bag) :
1. Ajouter le `[MessageHandler]` dans `OneAirHavenBagHandler.cs`.
2. Ajouter la méthode publique correspondante dans `OneAirHavenBagPatch.cs`.
3. Rebuild : `docker compose build auth && docker compose up -d --no-deps --force-recreate auth world`.

## Sauvegardes

- Giny écrit le world en DB toutes les 5 min (`SaveIntervalMinutes` dans
  `server/config/world_config.json.tmpl` — la valeur est rendue par
  `entrypoint.sh` au boot du container `world`).
- Service `backup` dans `docker-compose.yml` (image mysql:8.0) :
  `server/backup-db.sh` boucle un `mysqldump --single-transaction` pour
  `giny_auth` + `giny_world`, gzip dans `server/backups/`, rotation à 20.
  Variables d'override : `BACKUP_INTERVAL_SECONDS`, `BACKUP_KEEP_COUNT`.
- Le service est en `restart: unless-stopped`, pas dans un profile : il
  démarre avec le reste sauf si on l'exclut explicitement.
- Pour le démarrer sans toucher au reste : `docker compose up -d backup`.
- Restauration : `./scripts/restore-backup.sh` (menu interactif, arrête
  giny-world le temps de l'import, écrase auth+world). Flags utiles :
  `-y` (skip confirm), `--latest` (plus récent), `--keep-world`,
  ou un nom/chemin de fichier en argument positionnel.

## Mots de passe & secrets

- Les passwords sont dans `.env` (gitignoré). Au moment de la création (avril
  2026) ils ont été régénérés via `openssl rand`.
- MySQL root et user `giny` ont été ALTERés sur la DB live au moment du
  changement (les containers en cours gardent leurs connexions ouvertes ;
  une recreate auth/world prendra le nouveau password depuis `.env`).
- Pour régénérer la clé HMAC du site web :
  ```bash
  openssl rand -hex 32                    # WEB_SESSION_KEY
  ```
  Pour régénérer un password admin in-game (compte `Role>=5`) :
  ```bash
  ./scripts/create-account.sh <user> "$(openssl rand -base64 18 | tr -d /+=)" 5
  ```

## Conventions / nommage

- Préfixe `oneAir`/`OneAir` / `_oneAir` pour tout ce qu'on ajoute (champs,
  méthodes, commandes chat). Permet de séparer notre code du code Giny/Ankama.
- `ServerRoleEnum.Administrator` (= `Role=5`) pour les commandes admin.
- Les états UI custom sont des **statiques de classe** (et pas des champs
  d'instance), pour éviter les problèmes de recréation de `ChatFrame`.

## Ce qu'il ne faut PAS faire

- Ne pas modifier les fichiers UI XML du SWF (`Ankama_Social/ui/friends.xml`
  etc.) — l'expérience a montré que ça corrompt le SWF. Faire des panneaux
  Sprite custom à la place (cf. `.online`, `.ui`).
- Ne pas exécuter `docker compose down -v` sans demander : ça wipe la DB.
- Ne pas commit / push : ce dépôt n'est pas (encore) versionné, et de toute
  façon `OneAir.app/` fait 4.6 GB.
- Ne pas exécuter `/ultrareview` soi-même — c'est utilisateur uniquement.

## Quand l'utilisateur signale un bug

1. Lire `oneair-debug.log` (tail + grep pertinent) AVANT de proposer un fix.
2. Confirmer en relisant le source AS3 actuel — ne pas se baser sur la mémoire
   d'une session précédente.
3. Faire le fix minimal + redéployer (workflow ci-dessus).
4. **Mettre à jour README.md / CLAUDE.md si le fix change un comportement
   documenté ou ajoute une règle apprise.**
