# Upstream

Cet arbre source est vendoré depuis **Giny.NETCore**, l'émulateur Dofus 2.x
en .NET 6 maintenu par [Skinz3](https://github.com/Skinz3/Giny.NETCore).

- Repo : https://github.com/Skinz3/Giny.NETCore
- Commit pinné : `30dc4415a7b0cae081144fdc94c78d0b01062bc6`
- Date de vendoring : 2026-05-12

## Périmètre vendoré

Strict minimum pour builder `Giny.Auth` + `Giny.World` :

- `Sources/Giny.Core/`
- `Sources/Giny.IO/`
- `Sources/Giny.ORM/`
- `Sources/Giny.Protocol/`
- `Sources/Servers/Giny.Auth/`
- `Sources/Servers/Giny.World/`

Pas inclus : `Modules/`, `Tools/` (WPF), `Sync/`, `Zaap/`, `Ressources/` (94 MB
d'icônes/SDK).

## Modifications OneAir

Le tree contient à la fois le code Giny original et nos modifications
(préfixe `OneAir`/`_oneAir`). Pour identifier nos ajouts :

```bash
# Fichiers OneAir entièrement nouveaux
find server/giny -name "OneAir*.cs"

# Hooks injectés dans le code Giny
grep -rn "Giny.World.Managers.Chat.OneAir" server/giny --include='*.cs'
```

Un seul fichier Giny a été remplacé en entier :
`Sources/Servers/Giny.World/Handlers/Roleplay/HavenBag/HavenBagHandler.cs`
(version OneAir route les 11 messages haven-bag vers `OneAirHavenBagPatch`).

## Resync depuis upstream

L'upstream est peu actif. Si besoin de rebaser sur une nouvelle version :

1. Cloner upstream au nouveau commit dans `/tmp/giny-new`.
2. `diff -ruN /tmp/giny-new/Sources server/giny/Sources` pour repérer les
   conflits avec nos hooks `OneAir`.
3. Merger manuellement (les hooks sont reconnaissables par leur préfixe).
4. Mettre à jour le commit hash ci-dessus.
