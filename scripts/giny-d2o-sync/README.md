# giny-d2o-sync — wrapper du synchronizer officiel Giny

Outil pour parser tes `.d2o` locaux du client Dofus 2.68 et populer la base
`giny_world` avec **toutes** les données statiques (monstres, PNJ, donjons,
quêtes, items, sorts, recettes, succès, maps, etc.).

## Comment ça marche

Le projet upstream `Skinz3/Giny.NETCore` contient déjà un outil
`Giny.DatabaseSynchronizer` qui fait exactement ça : il lit les `.d2o`,
résout les chaînes via `i18n_*.d2i`, et insère dans la DB via la même ORM
que les serveurs Giny utilisent. Les blobs protobuf-net (effets d'items,
sorts par grade, etc.) sont donc **encodés correctement** puisque c'est
le code Giny lui-même qui les sérialise.

Ce wrapper :
1. Clone Giny.NETCore branche 2.68 dans une image Docker `dotnet/sdk:6`
2. Patche 2 lignes :
   - `ClientConstants.ClientPath` → `/data` (au lieu d'un chemin Windows)
   - `DatabaseManager.Initialize(...)` → lit les credentials depuis l'env
3. Compile `Giny.DatabaseSynchronizer.dll`
4. Lance le binaire avec ton dossier `OneAir.app/Contents/Resources` monté
   en read-only sur `/data`, et connecté à ton MySQL existant via le
   réseau Docker `dofus-giny_giny-net`.

Aucune donnée Ankama n'est embarquée dans l'image. La data reste sur ton
disque et n'est lue qu'au runtime, sur ta machine.

## Pré-requis

- Stack OneAir up (au moins `giny-mysql` doit tourner)
- Le dossier `OneAir.app/Contents/Resources/data/` doit contenir :
  - `data/common/*.d2o` (Monsters.d2o, Items.d2o, etc.)
  - `data/i18n/i18n_fr.d2i`
  - `content/maps/maps0.d2p`, `content/maps/elements.ele` (pour les maps)

## Usage

```bash
# Depuis la racine du projet OneAir
./scripts/giny-d2o-sync/run.sh
```

Le script :
- Lit `.env` pour `MYSQL_ROOT_PASSWORD`
- Build l'image `giny-d2o-sync:latest` (~3 min la première fois, cache ensuite)
- Demande confirmation (le sync **DROP** les tables avant de recréer)
- Lance le sync (logs `Starting synchronization...` puis ligne par ligne)
- Affiche le rappel de redémarrer auth/world pour recharger les records

Variables d'environnement supportées (override) :

| Var | Défaut | Description |
|---|---|---|
| `DATA_DIR` | `./OneAir.app/Contents/Resources` | dossier client (doit contenir `data/`) |
| `DB_HOST` | `mysql` | hôte MySQL vu depuis le container |
| `DB_NAME` | `${MYSQL_WORLD_DB}` ou `giny_world` | base cible |
| `DB_USER` | `root` | utilisateur MySQL |
| `DB_PASSWORD` | `${MYSQL_ROOT_PASSWORD}` | mot de passe |
| `NETWORK` | `dofus-giny_giny-net` | réseau Docker |

## Tables impactées (DROP + recreate)

Le synchronizer drop puis remplit ces tables (cf. `Program.cs` upstream) :

- `monsters`, `monster_static_spawns` (drop côté Giny mais pas re-rempli — ce sont
  des spawns dynamiques, pas des records statiques)
- `npcs`
- `dungeons`
- `items`, `weapons`
- `spells`, `spell_levels`, `spell_variants`, `spell_states`, `spell_bombs`
- `quests`, `quest_steps`, `quest_objectives`, `quest_step_rewards`
- `achievements`, `achievement_objectives`, `achievement_rewards`
- `recipes`, `skills`
- `breeds`, `experiences`, `heads`, `effects`
- `subareas`, `areas`
- `map_positions`, `map_references`, `map_scroll_actions`
- `item_sets`
- `living_objects`
- `emotes`, `ornaments`, `titles`
- `challenges`
- + `maps` (volumineuse — peut prendre 10+ min)

Les tables de **données joueur** (`accounts`, `characters`, `character_items`,
`world_servers`, etc.) sont **NON touchées**.

## Désactiver MapSynchronizer (si tu n'as pas les `.d2p` maps)

Le `Program.cs` a deux flags : `SYNC_D2O` et `SYNC_MAPS`. Si tu veux skip
les maps (gros), édite avant le build :

```bash
# Option 1 : passer SYNC_MAPS = false en env (à patcher dans le Dockerfile)
# Option 2 : commenter MapSynchronizer.Synchronize() dans Program.cs
```

(Pas exposé en argument CLI par le tool upstream — un patch sed peut l'ajouter.)

## Si la build échoue

Le code upstream a parfois des warnings/erreurs spécifiques à des versions
SDK. En cas de souci :

```bash
# Build manuelle pour debug
docker build --progress=plain -t giny-d2o-sync:latest scripts/giny-d2o-sync 2>&1 | tail -50
```

Erreurs fréquentes :
- **Path "/data" inexistant pendant le build** : normal, le path n'est utilisé
  qu'au runtime. Le build doit passer.
- **Compilation error sur des fichiers C#** : l'upstream peut avoir bougé.
  Cherche dans le tree `Skinz3/Giny.NETCore@2.68` ce qui a changé.

## Licence

`Giny.NETCore` (le code synchronisé) est un projet open-source maintenu par
Skinz3. Ce wrapper Docker l'utilise tel quel. Les données Ankama parsées au
runtime sont la propriété d'Ankama Studio — usage strictement local,
serveur privé personnel.
