# Client OneAir — `.app` macOS native

Le client tourne **sans Wine** : on part du Dofus 2.68.0.0 darwin officiel
(distribué par Ankama dans Cytrus à l'époque) et on l'augmente d'un
pré-launcher Swift, d'un faux Zaap server, et d'un SWF patché.

## `build.sh`

Point d'entrée unique. Lance le menu interactif sans arg, ou passe une cible :
`darwin`, `windows`, `all`. Flags : `--no-zip`, `--native` (macOS hôte).
Lit `.env` à la racine pour `SERVER_HOST` / `PUBLIC_AUTH_PORT` / `SERVER_DISPLAY_NAME`.

```bash
./client/build.sh                    # menu
./client/build.sh darwin             # client/build/OneAir.app via Docker
./client/build.sh windows            # client/build/OneAir-Windows/
```

Étapes du build darwin :

1. Copie `.cache/dofus-darwin-2.68/.../Dofus.app` → `build/OneAir.app`
2. Merge le fragment `lang_fr` (gfx + i18n + config-lang-fr.xml)
3. Override `Contents/Resources/config.xml` avec `giny-config.xml` (config Giny
   de base), puis patch `connection.host` aux valeurs `.env`
4. Remplace `DofusInvoker.swf` par notre version patchée
5. Génère `Contents/Resources/credentials.json` (params zaap pour le SWF)
6. Restaure les symlinks Adobe AIR.framework cassés par cytrus-downloader
7. Substitue `Contents/MacOS/Dofus` par notre launcher Swift cross-compilé
   depuis `launcher/macos/`, et déplace le binaire AIR original vers `dofus-real`
8. Intègre `zaap-server` (Go) dans `Contents/MacOS/`
9. Patch Info.plist (CFBundleName=OneAir, etc.)
10. Strip xattrs + signature ad-hoc (`rcodesign`)

## Sources de ce dossier

| Fichier / dossier | Rôle |
|---|---|
| `build.sh` | Build script (point d'entrée unique macOS+Windows) |
| `Dockerfile.{darwin,windows}` | Images builder Linux |
| `giny-config.xml` | Base `config.xml` Giny — patché à chaque build avec les valeurs `.env` |
| `DofusInvoker-patched.swf` | SWF Giny custom, voir « Patches SWF » |
| `launcher/macos/` | Pré-launcher Swift (sources + Package.swift) |
| `launcher/windows/` | Pré-launcher WPF .NET 8 (sources + .csproj) |
| `zaap-server/` | Faux Zaap server en Go (port DivaZaap → Thrift Apache) |
| `.cache/` (gitignoré) | Assets Dofus officiels (~5 GB chacun) + Swift SDK Darwin |
| `build/` (gitignoré) | Bundles assemblés et zips servis par `/download/{macos,windows}` |

## Patches SWF appliqués

Le `DofusInvoker-patched.swf` contient les patches suivants par rapport au
SWF officiel Ankama 2.68.0.0 :

| Patch | Site | Effet |
|---|---|---|
| **`BUILD_TYPE = RELEASE`** | `BuildInfos.get BUILD_TYPE` retourne 0 | UI debug masquée |
| **Skip host signature** | `AuthentificationFrame.process` (LoginValidationAction) — `if(false)` | Plus besoin que `connection.host.signature` corresponde à un host signé Ankama. On peut donc utiliser n'importe quel `host:port`. |
| **Skip RawData signature** | `ServerControlFrame.process` (RawDataMessage) — `Loader.loadBytes` direct | Le client charge le SWF reçu en `RawDataMessage` sans vérifier la signature → permet à Giny d'envoyer son `RawPatch.swf` non-signé. |
| **`hasZaapArguments` lit toujours `credentials.json`** | `ZaapConnectionHelper.hasZaapArguments` | Bypass du check `BUILD_TYPE==DEBUG` qui bloquait la branche credentials.json. Adobe AIR sur macOS reçoit mal `NativeApplication.arguments` cold-start, on utilise un fichier à la place. |
| **`ZaapConnectionHelper.connect` lit toujours `credentials.json`** | idem | Idem |
| **`_hostChosenByUser = strKey`** | `AuthentificationFrame.process` ligne 269 | Bug Ankama : le fallback mettait l'IP au lieu de la clé du dict, causant un TypeError à la connexion. |
| **`Server.name` override** | `Server.get name` | Pour `id == 291` retourne `"One Air"` au lieu de l'i18n Ankama qui pointait vers `"Imagiro"`. |
| **`.ui` chat hook** | `ChatFrame.process` (ChatTextOutputAction case) | Si `content == ".ui"` et `PlayerManager.hasRights == true`, ouvre un panneau admin Sprite custom (méthode statique inline `showOneAirUI`). |

Pour rebuild le SWF de zéro, on utilise [JPEXS Free Flash Decompiler](https://github.com/jindrapetrik/jpexs-decompiler).
Les sources AS3 patchées vivent (temporairement) dans `/tmp/giny-as3` après
décompilation, et l'import via `ffdec.jar -importScript` produit le SWF final.

## launcher/macos (Swift)

Pré-launcher cliquable lancé par macOS. Avant `exec` du vrai binaire AIR :
- charge les comptes saisis (multi-comptes via dropdown stylé)
- patche `Contents/Resources/config.xml` avec IP/port saisis dans Options avancées
- écrit `Contents/Resources/credentials.json` (params zaap synchronisés avec le
  zaap-server qu'on lance ensuite)
- spawn `zaap-server` en arrière-plan sur 127.0.0.1:4242 avec
  `--login=<user> --game-token=<password>`
- exec `dofus-real` → AIR captif charge `DofusInvoker.swf` qui lit le credentials.json

Build : passe par `./client/build.sh darwin` qui cross-compile via SwiftPM +
le Swift SDK Darwin assemblé dans `.cache/`. Pour un dev natif rapide sur Mac :

```bash
cd client/launcher/macos && swiftc -O -o OneAirLauncher OneAirLauncher.swift \
    -framework AppKit -framework Network -framework CoreImage
```

## zaap-server (Go)

Serveur Thrift TCP minimal qui simule l'Ankama Zaap pour le client Dofus 2.68.
Répond aux méthodes : `connect`, `settings_get`, `userInfo_get`,
`auth_getGameToken`, `zaapMustUpdate_get`. Le `gameToken` retourné est le
**mot de passe en clair** (convention Giny — voir
`Giny.Zaap.MessagesHandler.HandleAuthGetGameToken`).

Recompile :

```bash
cd client/zaap-server && go build -o zaap-server .
```

## Format `credentials.json`

```json
{"port":4242,"name":"dofus","release":"main","instanceId":1,"hash":"<uuid>"}
```

Le SWF Giny patché lit ce fichier (au lieu des CommandLineArguments qu'AIR
sur macOS ne reçoit pas correctement) et se connecte au zaap-server local
sur `port`. Le `hash` doit être identique au flag `--hash=<uuid>` passé à
`zaap-server`. OneAirLauncher.swift génère un UUID frais à chaque clic JOUER
et synchronise les deux.

## Logs (debug)

| Fichier | Contenu |
|---|---|
| `client/build/OneAir.app/Contents/Resources/oneair-debug.log` | Traces AS3 (chat input, intercept `.ui`, événements connexion) |
| `~/Library/Logs/OneAir/zaap-server.log` | Logs du faux Zaap |
| `~/Library/Logs/OneAir/launcher.log` | Logs du launcher Swift |

```bash
tail -f client/build/OneAir.app/Contents/Resources/oneair-debug.log
```
