# OneAir — serveur Dofus 2.68 privé + clients macOS &amp; Windows natifs

Stack auto-hébergée : émulateur **Giny.NETCore** (.NET 6) en Docker +
**OneAir.app** macOS + **OneAir-Windows/** Windows (Adobe AIR captif des
deux côtés, pas de Wine).

```
┌──────────────────────────────┐         ┌─────────────────────────────┐
│  OneAir.app    (macOS Swift) │         │  giny-auth   (5555)         │
│  OneAir-Windows (WPF C#)     │   TCP   │  giny-world  (5556)         │
│  ├─ Pré-launcher (multi-     │ ──────▶ │  giny-mysql  (3306)         │
│  │  comptes, IP/port custom) │         │  giny-web    (80 → 3000)    │
│  ├─ Dofus 2.68.0.0 natif     │         │  giny-dbgate (interne)      │
│  ├─ DofusInvoker-patched.swf │         │  (.NET 6 dans Docker)       │
│  ├─ zaap-server (Go natif)   │         └─────────────────────────────┘
│  └─ Adobe AIR captif         │
└──────────────────────────────┘
```

## Démarrage rapide

```bash
cp .env.example .env                 # ajuster ports / passwords si besoin
docker compose up -d --build         # stack serveur
open dist/OneAir.app                 # client macOS (build via ./client/build.sh darwin)
```

Premier lancement : **+ Nouveau compte** dans le launcher, JOUER. Le compte
est créé en DB. Pour avoir les droits admin, promotion :

```bash
./scripts/create-account.sh myuser motdepasse 5    # Role=5 = Administrator
```

## Site public &amp; admin

Tout est servi par le service `web` sur **<http://localhost>** :

- `/` — landing publique (no auth) : présentation, classes, articles, login
  joueur, téléchargement.
- `/article/{slug}` — page article (Markdown léger).
- `/admin` — dashboard auth-gated (compte avec `Role >= 5`).
- `/dbgate/` — DBGate iframé (gated par session admin).
- `/download/{macos,windows}` — bundles depuis `dist/` ou env
  `DOWNLOAD_{MACOS,WINDOWS}_URL`.
- `/api/public/{login,logout,me,character,status,articles,community}`.

L'admin gère : comptes, personnages, inventaires (avec catalogue items),
sorts (catalogue), apparence, sauvegardes, articles, actions live, panel
"Non géré".

Cookie HMAC-SHA256, expire 8 h, comparaison constant-time.

## Commandes chat custom

| Commande | Rôle | Description |
|---|---|---|
| `.who` | Joueur | Liste joueurs en ligne (+ payload `__ONEAIR_PLAYERS__`) |
| `.online_data` | Joueur | Dump structuré des joueurs en ligne |
| `.gocell <cell>` | Admin | Téléporte sur la cellule (0-559) de la map |
| `.cell` | Admin | Affiche la cellule actuelle |
| `.elems` | Admin | Liste les `MapElement` de la map |
| `.invlist` | Admin | Dump inventaire en chat |
| `.iteminfo <uid>` | Admin | Détaille les effets d'un item |
| `.iteffadd <uid> <id> <val>` | Admin | Ajoute un effet sur l'item |
| `.iteffset <uid> <idx> <val>` | Admin | Modifie un effet existant |
| `.iteffdel <uid> <idx>` | Admin | Supprime un effet |
| `.itemdump` | Admin | Émet le payload `__ONEAIR_INV__` (consommé par le SWF) |
| `.dj` | Admin | Liste les donjons |
| `.djgo <id>` | Admin | Téléporte à l'entrée d'un donjon |
| `.hbset <theme>` | Admin | Change le thème havre-sac |
| `.hbnpc` / `.hbhere` | Admin | Helpers debug NPC havre-sac |

Implémentation : `server/OneAirChatCommands.cs`. Refresh tooltip auto via
`ObjectModifiedMessage` ; `Character.RefreshStats()` si l'item est équipé.

Les boutons in-game `.ui` (panneau admin) et `.itemui` (éditeur d'objets)
ne sont pas des commandes chat — ils sont déclenchés côté SWF AS3.

## Actions live (poller)

Le world tourne `OneAirActionPoller.cs` qui lit `giny_world.actions` toutes
les 1.5 s. Types : `broadcast`, `kick`, `reload_inventory`, `send_pm`,
`teleport`, `set_kamas`, `give_kamas`, `set_level`, `give_xp`, `give_item`,
`heal`, `save_now`, `reload_items`, `shutdown`, `dump_inventory`,
`item_set_qty`, `item_set_pos`, `item_delete`, `item_eff_{add,set,del}`,
`learn_spell`, `forget_spell`, `reset_spells`, `dump_spells`, `set_breed`,
`set_sex`, `set_head`, `set_look`. Historique sur l'onglet "Historique" de
l'admin (50 dernières + résultat).

## Backups

- **Save Giny in-memory → DB** : 5 min (`SaveIntervalMinutes` dans
  `config/world_config.json.tmpl`).
- **Dump SQL** : service `backup` (`server/backup-db.sh`), `mysqldump
  --single-transaction` toutes les `BACKUP_INTERVAL_SECONDS` (300s par
  défaut), gzip dans `server/backups/`, rotation
  `BACKUP_KEEP_COUNT` (20).

```bash
docker compose up -d backup                 # démarre le service backup
./scripts/restore-backup.sh                 # menu interactif
./scripts/restore-backup.sh --latest        # plus récent, non interactif
./scripts/restore-backup.sh -y giny_<ts>.sql.gz
```

`restore-backup.sh` arrête `giny-world` pendant l'import (`--keep-world`
pour bypass) et écrase `giny_auth` + `giny_world`.

## Configurer le serveur ailleurs (LAN/Internet)

Le launcher expose des **Options avancées** qui patchent `config.xml` au
lancement — pas besoin de rebuild le bundle pour changer la cible.

1. Côté serveur : `.env` →
   ```
   PUBLIC_AUTH_PORT=8080
   PUBLIC_WORLD_PORT=8081
   ```
   puis `docker compose up -d --force-recreate`.
2. Côté client : OneAir.app → ⚙ OPTIONS AVANCÉES → IP, PORT → TESTER →
   ENREGISTRER → JOUER.
3. Ouvrir `PUBLIC_AUTH_PORT` et `PUBLIC_WORLD_PORT` sur le firewall.

## Pré-requis

| OS hôte | État |
|---|---|
| Linux (Ubuntu 22.04+, Debian 12+, Fedora 40+) | ✅ pleinement supporté |
| macOS 12+ (Apple Silicon) | ✅ pleinement supporté |
| Windows | ⚠️ via WSL2 ou Git Bash (les scripts sont en bash) |

Dans tous les cas : Docker ≥ 24.x avec Compose v2, ~25 GB d'espace libre,
connexion Internet pour le 1er build.

## Rebuild des clients (sans toolchain hôte)

```bash
./client/build.sh                   # menu interactif (cible + zip)
./client/build.sh windows           # → dist/OneAir-Windows/ + dist/OneAir-Windows.zip
./client/build.sh darwin            # → dist/OneAir.app/      + dist/OneAir-MacOS.zip
./client/build.sh all
```

Détails (Swift SDK Darwin auto-assemblé, override d'arch zaap-server,
quand rebuild les images) dans `CLAUDE.md`.

## Structure du projet

```
.
├── README.md / CLAUDE.md
├── .env / .env.example
├── docker-compose.yml             # mysql + dbgate + auth + world + web + backup
├── server/                        # stack Giny.NETCore
│   ├── Dockerfile                 # build .NET 6 + sed patches Giny
│   ├── entrypoint.sh
│   ├── config/                    # *.tmpl rendus au boot
│   ├── init-sql/                  # importé au 1er boot MySQL
│   ├── backups/                   # dumps mysqldump (rotation 20, gitignoré)
│   ├── OneAir*.cs                 # patches Giny (commandes chat, poller
│   │                              #   d'actions, havre-sac, events, etc.)
│   ├── web/                       # service Go (landing + admin)
│   │   ├── main.go                # routing, sessions, APIs admin
│   │   ├── landing.go             # landing publique + article SSR
│   │   ├── articles.go            # CRUD articles
│   │   ├── player.go              # auth joueur (cookie oneair_player)
│   │   ├── community.go           # /api/public/community
│   │   ├── unhandled.go           # panel "Non géré"
│   │   ├── d2p.go                 # extraction icônes items
│   │   ├── templates/             # *.html (go:embed)
│   │   └── static/                # *.css, *.js (go:embed)
│   └── SWF/AuthPatch.swf          # RawPatch.swf Giny
├── client/                        # sources des clients (gitignorées :
│   │                              #   dofus-*-2.68/, .cache/)
│   ├── README.md                  # détails build + patches SWF
│   ├── build.sh                   # point d'entrée unique (menu + CLI)
│   ├── Dockerfile.{darwin,windows}
│   ├── DofusInvoker-patched.swf   # SWF Giny patché (BUILD_TYPE=DEBUG)
│   ├── giny-config.xml
│   ├── OneAirLauncher/            # pré-launcher Swift macOS
│   ├── OneAirLauncher-win/        # pré-launcher WPF C# Windows
│   └── zaap-server/               # fake Zaap Thrift en Go (DivaZaap fork)
├── dist/                          # sortie de client/build.sh (gitignoré)
│   ├── OneAir.app/                # bundle macOS prêt à `open`
│   ├── OneAir-Windows/            # bundle Windows
│   ├── OneAir-MacOS.zip           # servi par /download/macos
│   └── OneAir-Windows.zip         # servi par /download/windows
└── scripts/
    ├── start-server.sh / stop-server.sh
    ├── create-account.sh
    ├── publish-article.sh
    ├── restore-backup.sh
    └── reset-db.sh
```

## Crédits

- **Giny.NETCore** par Skinz3 — <https://github.com/Skinz3/Giny.NETCore>
- **DivaZaap** (zaap-server fork) par jordanamr — <https://github.com/jordanamr/DivaZaap>
- **JPEXS Free Flash Decompiler (FFDec)** — patches AS3 du SWF
- **Apache Flex SDK 4.16.1** + **playerglobal.swc 27.0** — RawPatch.swf
- **cytrus-downloader** par loonaire — assets Dofus officiels
- **swift-sdk-darwin** par kabiroberai — base SDK Swift Darwin pour Linux
- **rcodesign** par indygreg — signature ad-hoc Mach-O depuis Linux
- **phracker/MacOSX-SDKs** — mirror SDK Apple historiques

## Avertissement légal

Projet à but **strictement personnel, non-commercial, auto-hébergé**. Pour
les développeurs curieux du protocole et de l'architecture d'un MMO Adobe
AIR/Flash.

- **Code OneAir** (ce dépôt) : MIT — voir [`LICENSE`](LICENSE).
- **Dofus** est une marque déposée d'**Ankama Games**. Aucun nom,
  graphisme, son, texture, ou binaire Adobe AIR captif n'est redistribué
  ici. Les utilisateurs récupèrent les assets via le CDN officiel Cytrus
  (`client/build.sh`) et doivent posséder une copie légitime de Dofus.
- **macOS SDK** (cross-compile launcher Swift depuis Linux) : EULA réserve
  l'usage à du hardware Apple. Mirror phracker en zone grise. Optionnel.
- **Aucune garantie** : si Ankama notifie le projet, il sera retiré.
