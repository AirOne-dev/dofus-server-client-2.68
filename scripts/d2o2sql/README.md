# d2o2sql — convertisseur Dofus `.d2o` → SQL Giny

Outil CLI standalone pour parser les archives `.d2o` du client Dofus 2.x
local et générer des fichiers `.sql` d'import dans la stack Giny.

## Pourquoi c'est limité

- Les fichiers `.d2o` officiels Ankama sont la propriété de leur éditeur.
  Cet outil ne contient AUCUNE donnée Ankama — il **lit** les fichiers
  que tu possèdes déjà sur ta machine et **convertit** vers SQL.
- Beaucoup de tables Giny stockent leurs colonnes complexes en **blobs
  protobuf** (effets d'items, sorts, monstres). Ces blobs sont produits
  par le code C# de Giny lui-même au runtime — ce script ne peut donc pas
  les régénérer fidèlement. Les mappings dans `mappings.go` couvrent
  les tables où les colonnes sont primitives (Areas, SubAreas,
  MapPositions, Waypoints, Titles, Ornaments, etc.).
- Pour les blobs, il faut soit :
  1. Extraire les SQL d'un dump Giny existant (forks publics, communauté).
  2. Lancer un import via les outils internes Giny côté serveur.

## Usage

```bash
# 1. Liste tous les .d2o et leurs classes (utile pour ajouter un mapping)
go run ./scripts/d2o2sql --list \
    --data ./dist/OneAir.app/Contents/Resources/data/common

# 2. Génère les .sql pour les mappings connus
go run ./scripts/d2o2sql \
    --data ./dist/OneAir.app/Contents/Resources/data/common \
    --i18n ./dist/OneAir.app/Contents/Resources/data/i18n/i18n_fr.d2i \
    --out  ./server/init-sql

# 3. Pour ne traiter qu'un fichier précis
go run ./scripts/d2o2sql \
    --data ./dist/OneAir.app/Contents/Resources/data/common \
    --i18n ./dist/OneAir.app/Contents/Resources/data/i18n/i18n_fr.d2i \
    --only Areas.d2o,SubAreas.d2o \
    --out  ./server/init-sql

# 4. Avec dump JSON pour debug
go run ./scripts/d2o2sql --json --data ... --i18n ... --out ...
```

Les fichiers générés sont nommés `10-areas.sql`, `11-subareas.sql`, …
(à partir du numéro `--start`, défaut 10) — ils passent donc **après**
les dumps existants `00-03` lors de l'init MySQL.

## Réimporter dans MySQL

Si tu veux ré-importer (et écraser) les tables ciblées :

```bash
# Wipe + reset complet
docker compose down -v       # ⚠ supprime mysql-data
docker compose up -d mysql   # remonte avec init-sql/* exécuté

# Ou ponctuellement, sans wipe
docker exec -i giny-mysql mysql -u root -p$MYSQL_ROOT_PASSWORD giny_world < server/init-sql/10-areas.sql
```

## Ajouter un mapping

Édite `mappings.go`. Format :

```go
"Foo.d2o": {
    Table: "foo",
    Columns: []ColMap{
        {"Id", "$id"},                  // ID du record
        {"Name", "$i18n:nameId"},       // résout via i18n_fr.d2i
        {"Field1", "fieldName"},         // mapping direct
        {"Json", "$json:complexField"},  // sérialise en JSON
        {"Bool", "$str:visible"},        // converti en chaîne ('True'/'False')
    },
},
```

Pour découvrir les noms de champs réels d'un fichier d2o, utilise
`--list` puis ajoute un mapping minimal et lance avec `--json` pour voir
la structure complète.

## Format d2o (référence rapide)

```
[0..2]   "D2O" (magic)
[3..6]   int32 BE indexOffset
[7..]    records (binaire, sérialisation par classe)
@indexOffset:
   int32 BE indexLength
   pairs (int32 id, int32 offset)
   int32 BE classCount
   pour chaque classe : (id, name utf-8, package utf-8, fieldCount, fields)
```

Types de champs :
- `-1` int32, `-2` bool, `-3` string utf-8, `-4` float64, `-5` i18n key,
- `-6` uint32, `-99` vector, `>=0` sous-objet polymorphique.

Cf. `d2o.go` pour l'implémentation complète.
