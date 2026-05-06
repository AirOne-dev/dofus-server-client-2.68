# OneAir — serveur Dofus 2.68 privé + clients macOS &amp; Windows natifs

Stack auto-hébergée : émulateur **Giny.NETCore** (.NET 6) en Docker +
**OneAir.app** macOS native + **OneAir-Windows/** Windows natif (Adobe AIR
captif des deux côtés, pas de Wine).

```
┌──────────────────────────────┐         ┌─────────────────────────────┐
│  OneAir.app    (macOS Swift) │         │  giny-auth   (5555)         │
│  OneAir-Windows (WPF C#)     │   TCP   │  giny-world  (5556)         │
│  ├─ Pré-launcher (multi-     │ ──────▶ │  giny-mysql  (3306)         │
│  │  comptes, IP/port custom) │         │  giny-dbgate (3000)         │
│  ├─ Dofus 2.68.0.0 natif     │         │  (.NET 6 dans Docker)       │
│  ├─ DofusInvoker-patched.swf │         └─────────────────────────────┘
│  ├─ zaap-server (Go natif)   │
│  └─ Adobe AIR captif         │
└──────────────────────────────┘
```

## Ce qui marche

| Composant | État |
|---|---|
| Stack Docker (auth/world/mysql/admin) | ✅ |
| **Interface admin web** (`http://localhost:3000`) — auth, dashboard, gestion comptes/personnages/inventaires, backups, console SQL | ✅ |
| OneAir.app (macOS arm64+x86_64 universal via Rosetta du SWF) | ✅ |
| OneAir-Windows/ (Windows x64, AIR captif Ankama 2.68 + launcher WPF C#) | ✅ |
| Auth + sélection serveur + entrée en jeu | ✅ |
| Commandes admin in-game (`.help`, `.kamas`, `.tp`, …) | ✅ |
| **Panneau admin in-game `.ui`** (5 onglets PERSO/INVENTAIRE/CARTE/SPAWN/SERVEUR) | ✅ |
| **Panneau joueurs en ligne `.online`** (grille live) | ✅ |
| **Éditeur d'objets visuel `.itemui`** (filtré équipable/utilisable, effets) | ✅ |
| Multi-comptes dans le launcher Swift | ✅ |
| Server custom IP/port/nom via Options avancées | ✅ |
| **Havre-sac** (entrée/sortie, zaap intérieur, coffre, perso, loterie) | ✅ |
| **Capture des actions non gérées** (panel admin → onglet Non géré, copy-paste vers Claude) | ✅ |
| **Sortie de bâtiment** (étoile au sol bones=3507 ; détection on-walk + lookup outdoor via Position.Point comme `.relative`) | ✅ |

## Commandes admin custom (OneAir)

| Commande | Rôle min | Description |
|---|---|---|
| `.ui` | Admin | Ouvre le panneau d'admin général (5 onglets) |
| `.itemui` | Admin | Ouvre l'éditeur visuel d'objets (liste + effets) |
| `.online` | Joueur | Ouvre la grille des joueurs connectés |
| `.gocell <cellId>` | Admin | Téléporte sur la cellule (0-559) de la map courante |
| `.who` | Joueur | Liste des joueurs en ligne (chat + payload structuré) |
| `.invlist` | Admin | Dump inventaire en chat (UID/GID/qty/nom) |
| `.iteminfo <uid>` | Admin | Détaille les effets d'un item |
| `.iteffadd <uid> <id> <val>` | Admin | Ajoute un effet entier sur l'item |
| `.iteffset <uid> <idx> <val>` | Admin | Modifie la valeur de l'effet à l'index |
| `.iteffdel <uid> <idx>` | Admin | Supprime l'effet à l'index |
| `.itemdump` | Admin | Émet le payload `__ONEAIR_INV__` pour `.itemui` |

Implémentation : `server/OneAirChatCommands.cs`. Refresh tooltip auto via
`ObjectModifiedMessage` ; `RefreshStats()` appelé si l'item est équipé.

## Site public (landing)

Une fois la stack démarrée, <http://localhost:3000> sert la **landing page
publique** (no auth) — présentation du serveur, articles, login joueur,
téléchargement macOS. Inspirée Dofus (parchment, or, runes) mais sans copier
ses assets. Sections : hero, présentation, 18 classes, actualités, login
compte / personnages, téléchargement, footer.

Routes publiques :

- `/` — landing
- `/article/{slug}` — page article (Markdown léger rendu en HTML)
- `/download/macos` — 302 vers `DOWNLOAD_MACOS_URL` (env, vide = page
  "bientôt disponible") ; sinon zip à la volée de `OneAir.app`
- `/download/windows` — idem mais sur `DOWNLOAD_WINDOWS_URL` /
  `OneAir-Windows/` (zip à la volée du bundle WPF + AIR captif Windows)
- `/api/public/login` / `/logout` / `/me` — auth joueur (cookie séparé
  `oneair_player`, vérifié contre `giny_auth.accounts`, TTL 7 j)
- `/api/public/character?id=N` — détails d'un personnage du joueur
  connecté (level, XP, kamas, position, items count)
- `/api/public/articles` / `/api/public/articles/{slug}` — JSON
- `/api/public/status` — léger pour widgets (auth/world up, online, …)

Variables d'environnement à connaître côté admin (`.env`) :

- `DOWNLOAD_MACOS_URL` — URL du `.app` ou `.dmg` (S3, R2, GitHub release).
  Si vide, le bouton renvoie le zip de `OneAir.app/` à la volée.
- `DOWNLOAD_WINDOWS_URL` — pareil pour Windows. Si vide, zip de
  `OneAir-Windows/` à la volée.
- `WELCOME_MESSAGE` — message hero de la landing (sinon texte par défaut).
- `XP_RATE`, `DROP_RATE`, `JOB_RATE` — affichés dans la landing pour info.

### Articles

Le site public liste les actualités depuis la table
`giny_world.oneair_articles` (créée à la volée par
`ensureArticlesSchema()`). Markdown léger : `#`/`##`/`###`, listes `-`,
`**gras**`, `*italique*`, `` `code` ``, `[texte](url)`.

**À chaque changement notable du serveur, ajouter un article via l'onglet
Articles du panel admin** (cf. CLAUDE.md, "Règle d'or n°1bis"). C'est la
seule trace publique de l'historique du serveur.

## Interface admin web

L'admin OneAir est à <http://localhost:3000/admin> (la racine sert la
landing publique). **Pas de couple admin/password séparé** : l'auth admin
utilise le même endpoint que le login joueur (`/api/public/login`), avec
un compte qui a `Role >= 5` dans `giny_auth.accounts`. Promotion :

```bash
./scripts/create-account.sh myadmin motdepasse 5
# Ou sur un compte existant : UPDATE accounts SET Role=5 WHERE Username='…';
```

L'admin expose :

- **Tableau de bord** : état auth/world (TCP ping), nb comptes/personnages,
  joueurs en ligne, broadcast à tous les joueurs (via table `oneair_actions`)
- **Comptes** : liste, création, changement de rôle (Joueur → Admin),
  ban/unban, modification mot de passe, suppression
- **Personnages** : liste avec compte/breed/XP/kamas/position, recherche,
  shortcut vers leur inventaire, kick (pour les online)
- **Inventaires** : UI Dofus-like avec **onglets de catégorie** (Tous /
  Équipement / Consommable / Ressource / Familier / Quête / Cérémonial /
  Autre) avec compteurs, **toolbar** (recherche + tri par position/nom/
  niveau/quantité/type + checkbox "Équipés uniquement"). Icônes extraites
  des `.d2p` Dofus locaux + fallback DofusDB API par `iconId` lookup.
  Édition complète des effets (50 effets, dropdown), quantité, position,
  suppression. Décodage des effets via `dump_inventory` (online requis).
  **Catalogue d'items du jeu** (~18 000) accessible via picker modal :
  recherche par nom, filtrage par catégorie, pagination, click pour donner.
- **Sorts** : modal dédié avec catalogue complet (~17 000 sorts), recherche,
  apprentissage en 1 clic, désapprentissage (×), reset. Icônes via
  DofusDB. Niveau dérivé du niveau perso (Giny auto-progression).
- **Apparence** : changement de classe, sexe, et édition raw du look
  string (parsé via `EntityLookManager.Parse`). Aperçus de têtes via
  `/heads/<id>.png` (extrait du client OneAir).
- **Sauvegardes** : liste, déclencher un dump à la demande, restaurer ou
  supprimer un backup (mêmes fichiers que `server/backup-db.sh`)
- **Base de données** (onglet plein écran) : DBGate iframé sur `/dbgate/` —
  reverse-proxyé par l'admin et donc gaté par la session admin (dbgate
  n'est exposé qu'en interne via Docker network). Toute la puissance d'un
  client SQL pro : édition de tables, queries arbitraires, schéma.
- **Historique des actions** : 50 dernières actions postées avec leur
  payload, statut (en attente / traité) et résultat
- **Articles** : éditeur de news (titre, slug, tag, résumé, image de
  couverture, contenu Markdown). Publication immédiate ou brouillon.
  Liste/édition/suppression des articles existants. Lien direct
  vers la page publique de chaque article (`/article/{slug}`).

Auth : cookie HMAC-SHA256 signé, expire 8 h. Comparaison constant-time.

### Actions admin live (poller v2)

Le world tourne un poller (`server/OneAirActionPoller.cs`, démarré depuis
`WorldServer.OnServerStarted`) qui lit `giny_world.oneair_actions` toutes les
1.5 s et exécute les actions postées par l'admin. Types supportés :

| Type | Payload | Effet |
|---|---|---|
| `broadcast` | `<message>` | Notification serveur à tous les joueurs |
| `kick` | `<charId>\|<reason>` | Déconnecte le joueur |
| `reload_inventory` | `<charId>` | Refresh inventaire (après modif DB) |
| `send_pm` | `<charId>\|<msg>` | Message privé `[Admin] ...` |
| `teleport` | `<charId>\|<mapId>[\|<cellId>]` | Téléporte le joueur |
| `set_kamas` / `give_kamas` | `<charId>\|<amount>` | Définit / ajoute des kamas |
| `set_level` | `<charId>\|<level>` | Niveau 1-200 (recalcul XP) |
| `give_xp` | `<charId>\|<xp>` | Ajoute de l'XP |
| `give_item` | `<charId>\|<gid>\|<qty>` | Donne un item |
| `heal` | `<charId>` | Heal complet |
| `save_now` | `` | Sauvegarde DB immédiate |
| `reload_items` | `` | Reload `items.xml` |
| `shutdown` | `<seconds>\|<message>` | Annonce + arrêt programmé |
| `dump_inventory` | `<charId>` | Décode et écrit l'inventaire JSON dans `oneair_inventory_dumps` (online requis) |
| `item_set_qty` | `<charId>\|<uid>\|<qty>` | Modifie la quantité d'un item |
| `item_set_pos` | `<charId>\|<uid>\|<pos>` | Déplace l'item (0-62 = équipé, 63 = sac) |
| `item_delete` | `<charId>\|<uid>` | Supprime un item de l'inventaire |
| `item_eff_add` | `<charId>\|<uid>\|<effectId>\|<value>` | Ajoute un effet sur l'item |
| `item_eff_set` | `<charId>\|<uid>\|<index>\|<value>` | Modifie la valeur d'un effet |
| `item_eff_del` | `<charId>\|<uid>\|<index>` | Supprime un effet |
| `learn_spell` | `<charId>\|<spellId>` | Apprend un sort |
| `forget_spell` | `<charId>\|<spellId>` | Oublie un sort |
| `reset_spells` | `<charId>` | Vide la liste des sorts |
| `dump_spells` | `<charId>` | Décode et écrit les sorts dans `oneair_spell_dumps` |
| `set_breed` | `<charId>\|<breedId>` | Change la classe |
| `set_sex` | `<charId>\|0\|1` | Change le sexe |
| `set_head` | `<charId>\|<cosmeticId>` | Change la tête |
| `set_look` | `<charId>\|<lookString>` | Set look brut Dofus |

L'historique (50 dernières) est visible sur l'onglet **Historique** de
l'admin web, avec le résultat (`ok` / `err: ...`).

## Backups & sauvegarde auto

- **Sauvegarde Giny in-memory → DB** : toutes les **5 min**
  (`SaveIntervalMinutes` dans `server/config/world_config.json.tmpl`).
- **Dump SQL des bases** : service `backup` dans `docker-compose.yml`,
  lance le script `server/backup-db.sh` qui dumpe `giny_auth` + `giny_world`
  (`mysqldump --single-transaction`) toutes les `BACKUP_INTERVAL_SECONDS`
  (300s par défaut), gzip, dans `server/backups/giny_<UTC>.sql.gz`,
  rotation à `BACKUP_KEEP_COUNT` (20 par défaut).

```bash
# Lancer le service backup (n'a pas besoin du build du serveur)
docker compose up -d backup
docker logs -f giny-backup

# Restore interactif (menu de tous les backups dispos)
./scripts/restore-backup.sh

# Restore non-interactif
./scripts/restore-backup.sh --latest        # le plus récent
./scripts/restore-backup.sh -y giny_<ts>.sql.gz   # nom de fichier
```

Le script `restore-backup.sh` arrête `giny-world` pendant l'import
(option `--keep-world` pour bypass) et le redémarre ensuite. Il écrase
les bases `giny_auth` + `giny_world` — confirmation avant destruction.

## Pré-requis

| OS hôte | État | Notes |
|---|---|---|
| **Linux** (Ubuntu 22.04+, Debian 12+, Fedora 40+) | ✅ pleinement supporté | Tout marche en natif. Tous les wrappers Docker (`build-docker-{darwin,windows}.sh`) ont été développés et testés ici. |
| **macOS** 12+ (Apple Silicon M1–M4) | ✅ pleinement supporté | `build-app-darwin.sh` détecte l'OS hôte et utilise `plutil`/`codesign`/`xattr` natifs. Wrappers Docker fonctionnent aussi via Docker Desktop. |
| **Windows** | ⚠️ via WSL2 ou Git Bash | Tous les scripts sont en `bash`. Lance-les depuis **WSL2** (Ubuntu) avec Docker Desktop en mode WSL backend, ou depuis **Git Bash**. PowerShell natif ne marche pas. |

Dans tous les cas :
- **Docker** ≥ 24.x avec Compose v2 (intégré).
- **~25 GB d'espace libre** : 5 GB d'assets Dofus darwin + 5 GB windows +
  5 GB par bundle généré + 700 MB Swift SDK Darwin (cache) + 5 GB containers Docker.
- Connexion Internet pour le 1er build (fetch assets Cytrus + images Docker).

## Démarrage rapide

```bash
# 1. Configuration
cp .env.example .env
# (édite .env si tu veux changer ports / mot de passe MySQL / nom serveur)

# 2. Démarrage de la stack serveur
docker compose up -d --build

# 3. Lancement du client
open OneAir.app
```

## Rebuild des clients en Docker (sans toolchain hôte)

Sur Linux, on peut rebuild les deux bundles **uniquement avec Docker** —
aucun .NET, Swift, Go, ou rcodesign installés sur la machine. Deux images
dédiées (~2 GB chacune, build une fois) :

```bash
# Bundle Windows complet (Dofus.exe WPF + zaap-server.exe + assets)
./client/build-docker-windows.sh    # → OneAir-Windows/  (~4.7 GB)

# Bundle macOS complet (assembly + zaap-server Mach-O + signature ad-hoc)
./client/build-docker-darwin.sh     # → OneAir.app/      (~4.7 GB)
```

Les images sont cachées localement après le 1er build. Le projet est monté
en volume → modifs des sources visibles instantanément, sans rebuild
d'image. Voir CLAUDE.md → *Build clients en Docker* pour les détails
(images de base, override d'arch zaap-server).

### Cross-compile aussi du launcher Swift (opt-in)

Par défaut le wrapper Docker macOS réutilise le binaire `OneAirLauncher`
arm64 déjà committed. Pour le **rebuild** depuis Linux (utile quand on
modifie `OneAirLauncher.swift`), un script assemble un Swift SDK Darwin
en stitchant 3 sources publiques :

```bash
./client/make-darwin-sdk.sh        # 1ère fois : ~3-5 min, ~700 MB cache
./client/build-docker-darwin.sh    # détecte le bundle, swift build --swift-sdk
```

Voir CLAUDE.md → *Cross-compile launcher Swift depuis Linux* pour la
mécanique exacte (phracker SDK + swift.org toolchain + xtool-org LLVM
Linux toolset).

À la première connexion :
- Le launcher Swift te propose **+ Nouveau compte** → saisis username + password,
  coche « Se souvenir », JOUER → le compte est créé en DB côté Giny.
- Pour avoir les droits admin, promotion via SQL (cf. ci-dessous).

## Promouvoir un compte en admin

```bash
./scripts/create-account.sh myuser motdepasse 5
```
ou directement :
```bash
docker exec giny-mysql mysql -u root -pchangeme-rootpw giny_auth \
    -e "UPDATE accounts SET Role=5 WHERE Username='myuser';"
```

`Role=5` = Administrator (cf. `ServerRoleEnum`). Reconnecte-toi en jeu.

## Panneau admin in-game

Une fois connecté avec un compte admin, tape **`.ui`** dans le chat → un panneau
draggable s'ouvre avec :

- **PERSONNAGE** : niveau, XP, kamas, métiers, sorts, apparence
- **INVENTAIRE** : donner objets, sets, titres, ornements
- **CARTE / TÉLÉPORT** : tp map, goto joueur, map random
- **SPAWN / PNJ** : spawn monstres, ajouter NPC
- **SERVEUR** : notif globale, reload data, restore

Chaque bouton dispatche la commande chat correspondante (`.kamas 100000`, etc.)
qui est exécutée par `ChatCommandsManager` côté serveur Giny avec contrôle
du rôle. Les commandes individuelles fonctionnent aussi à la main dans le chat
(préfixe `.`).

## Configurer le serveur ailleurs (LAN ou Internet)

Le launcher Swift expose des **Options avancées** qui réécrivent `config.xml`
au lancement. Tu n'as pas besoin de rebuild le `.app` pour changer la cible.

1. Côté serveur (la machine qui run Docker) : édite `.env` :
   ```
   PUBLIC_AUTH_PORT=8080
   PUBLIC_WORLD_PORT=8081
   ```
   et `docker compose up -d --force-recreate`.

2. Côté client : ouvre OneAir.app, clique **⚙ OPTIONS AVANCÉES** :
   - IP / DNS DU SERVEUR : `ton.dns.fr` ou IP publique
   - PORT : `8080`
   - **TESTER** puis **ENREGISTRER**.

3. JOUER → OneAirLauncher patche `Contents/Resources/config.xml` puis
   exec `dofus-real`. Le client se connecte à la nouvelle cible.

Pour ouvrir au monde, ouvre tes ports `PUBLIC_AUTH_PORT` et `PUBLIC_WORLD_PORT`
sur ton firewall / box Internet (TCP entrant).

## Structure du projet

```
.
├── README.md                       # ce fichier
├── .env / .env.example             # config globale (MySQL, ports, etc.)
├── docker-compose.yml              # mysql + dbgate + auth + world
├── OneAir.app/                     # bundle macOS native (4.6 GB), prêt à lancer
├── server/                         # stack Giny.NETCore
│   ├── Dockerfile                  # build image .NET 6 + patches Giny
│   ├── entrypoint.sh               # bootstrap auth ou world
│   ├── config/
│   │   ├── auth_config.json.tmpl
│   │   └── world_config.json.tmpl
│   ├── OneAirChatCommands.cs       # commandes chat custom (.who, .gocell…)
│   ├── OneAirActionPoller.cs       # poller actions admin → table oneair_actions
│   ├── OneAirEventManager.cs       # multiplicateurs XP/Kamas/Drop runtime
│   ├── OneAirHavenBagHandler.cs    # remplace HavenBagHandler.cs vanilla
│   ├── OneAirHavenBagPatch.cs      # logique complète havre-sac (entrée/sortie/zaap/coffre/perso/loterie)
│   ├── init-sql/                   # dumps importés au 1er boot MySQL
│   ├── admin/                      # service Go admin + landing publique
│   │   ├── main.go                 # routing, auth admin, APIs admin
│   │   ├── landing.go              # landing /, article SSR, download, status
│   │   ├── articles.go             # CRUD articles (table oneair_articles)
│   │   ├── player.go               # auth joueur (cookie oneair_player) + APIs
│   │   ├── d2p.go                  # extraction icônes d'items des .d2p
│   │   ├── templates/              # login.html, app.html, landing.html, article.html
│   │   └── static/                 # admin (style.css, app.js) + public (landing.css, landing.js)
│   └── SWF/AuthPatch.swf           # RawPatch.swf Giny (compilé Apache Flex)
├── client/                         # sources des deux clients
│   ├── README.md                   # détails build + patches SWF
│   ├── build-app-darwin.sh         # rebuild OneAir.app (Mac natif OU Linux/Docker)
│   ├── build-app-windows.sh        # rebuild OneAir-Windows/ (Linux natif)
│   ├── build-docker-darwin.sh      # wrapper Docker → OneAir.app sans toolchain hôte
│   ├── build-docker-windows.sh     # wrapper Docker → OneAir-Windows/ sans toolchain hôte
│   ├── Dockerfile.darwin           # image swift:6 + Go + rcodesign (signature ad-hoc)
│   ├── Dockerfile.windows          # image dotnet:8 + Go (cross-compile WPF)
│   ├── DofusInvoker-patched.swf    # SWF Giny patché (BUILD_TYPE=RELEASE,
│   │                                 signature host bypass, RawData
│   │                                 signature bypass, .ui hook, name override)
│   ├── giny-config.xml             # base config.xml Giny
│   ├── dofus-darwin-2.68/          # client Dofus darwin officiel (5 GB)
│   ├── dofus-windows-2.68/         # client Dofus windows officiel (5 GB)
│   ├── OneAirLauncher/             # source Swift du pré-launcher macOS
│   ├── OneAirLauncher-win/         # source C# WPF du pré-launcher Windows
│   └── zaap-server/                # source Go du fake Zaap (DivaZaap fork)
└── scripts/                        # wrappers docker compose + admin DB
    ├── start-server.sh             # docker compose up -d
    ├── stop-server.sh              # docker compose stop
    ├── create-account.sh           # crée un compte joueur en SQL
    └── reset-db.sh                 # wipe les volumes mysql/dbgate
```

## Commandes utiles

```bash
# Stack
./scripts/start-server.sh           # docker compose up -d
./scripts/stop-server.sh            # docker compose stop
docker compose logs -f world        # suivre les logs world
docker compose ps

# Admin DB
open http://localhost:3000          # DBGate (édition tables Giny)
./scripts/create-account.sh user pw 5
./scripts/reset-db.sh               # wipe tout (volumes + dump réimporté)

# Client
open OneAir.app
tail -f ~/Library/Logs/OneAir/zaap-server.log
tail -f OneAir.app/Contents/Resources/oneair-debug.log
./client/build-app-darwin.sh        # rebuild .app après modif source
```

## Détails techniques

Le SWF du client `DofusInvoker.swf` est patché à plusieurs endroits par
notre `build-app-darwin.sh` (via FFDec + scripts AS3). Les patches sont
documentés dans [`client/README.md`](client/README.md).

Côté serveur, `server/Dockerfile` clone Giny.NETCore et applique des fixes
spécifiques 2.68 (cf. commentaires dans le Dockerfile).

## Crédits

- **Giny.NETCore** par Skinz3 — <https://github.com/Skinz3/Giny.NETCore>
- **DivaZaap** (zaap-server fork) par jordanamr — <https://github.com/jordanamr/DivaZaap>
- **JPEXS Free Flash Decompiler (FFDec)** — patches AS3 du SWF
- **Apache Flex SDK 4.16.1** + **playerglobal.swc 27.0** — compilation RawPatch.swf
- **cytrus-downloader** par loonaire — <https://github.com/loonaire/Cytrus-downloader> (fetch assets Dofus officiels)
- **swift-sdk-darwin** par kabiroberai — base du Swift SDK Darwin pour Linux
- **rcodesign** par indygreg (apple-codesign) — signature ad-hoc Mach-O depuis Linux
- **phracker/MacOSX-SDKs** — mirror des SDK Apple historiques

## Avertissement légal

Ce projet est un serveur Dofus 2.68 privé, à but **strictement personnel,
non-commercial et auto-hébergé**. Il s'adresse aux développeurs curieux de
comprendre le protocole et l'architecture d'un MMO Adobe AIR/Flash.

- **Code OneAir** (ce dépôt) : sous licence MIT — voir [`LICENSE`](LICENSE).
  Ne couvre que les scripts, patches, launchers et serveur admin originaux.
- **Dofus est une marque déposée d'Ankama Games**. Le nom, les graphismes,
  les sons, les textures et les binaires Adobe AIR captifs ne sont **pas
  redistribués** par ce dépôt. Les utilisateurs récupèrent eux-mêmes les
  assets via le CDN officiel Cytrus (script `build-docker-*.sh`) et doivent
  posséder une copie légitime de Dofus. Ankama applique strictement ses
  conditions d'utilisation concernant les serveurs émulés, et ce projet ne
  remplace en aucun cas le service officiel.
- **macOS SDK** (utilisé pour cross-compiler le launcher Swift depuis
  Linux) : son EULA réserve l'usage à du hardware Apple. Le mirror
  `phracker/MacOSX-SDKs` est en zone grise comme tout setup osxcross.
  Activable optionnellement (cf. CLAUDE.md), pas requis pour faire tourner
  le serveur.
- **Aucune garantie** : si Ankama notifie le projet, il sera retiré sans
  préavis. Les utilisateurs assument leur propre risque.

Si tu veux héberger publiquement une instance OneAir, fais-le à tes
risques et périls — et ne mets surtout pas de monétisation dessus.
