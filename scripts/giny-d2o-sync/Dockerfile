# Wrapper Docker pour Giny.DatabaseSynchronizer (upstream).
#
# Le projet original est dans Skinz3/Giny.NETCore branche 2.68 :
#   Sources/Sync/Giny.DatabaseSynchronizer/
#
# On clone, on patche juste :
#   1) ClientConstants.ClientPath → /data (au lieu d'un chemin Windows hardcodé)
#   2) L'appel Initialize() qui contient des credentials MySQL en dur → lit les
#      variables d'environnement DB_HOST / DB_NAME / DB_USER / DB_PASSWORD.
#
# Aucun contenu de jeu n'est embarqué dans cette image — elle ne fait QUE
# compiler le synchronizer upstream. La data Dofus reste sur ton disque
# (montée en read-only dans le container).

ARG GINY_REPO=https://github.com/Skinz3/Giny.NETCore.git
ARG GINY_BRANCH=2.68

FROM mcr.microsoft.com/dotnet/sdk:6.0-bookworm-slim AS build
ARG GINY_REPO
ARG GINY_BRANCH

RUN apt-get update && apt-get install -y --no-install-recommends \
        git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN git init && git remote add origin "${GINY_REPO}" && \
    git fetch --depth=1 origin "${GINY_BRANCH}" && \
    git checkout FETCH_HEAD

# Patch 0 : import manquant dans HealHp.cs (bug upstream Giny 2.68).
# Sans ça la build de Giny.World échoue avec "Logger does not exist".
RUN sed -i '1iusing Giny.Core;' Sources/Servers/Giny.World/Managers/Fights/Effects/Heals/HealHp.cs

# Patch 1 : ClientConstants.ClientPath → /data
RUN sed -i 's|public const string ClientPath = .*;|public const string ClientPath = "/data";|' \
        Sources/Giny.IO/ClientConstants.cs

# Patch 2 : credentials MySQL via env vars + sortie console moins verbeuse.
# Le bloc Initialize cible la signature historique (host, db, user, pass).
RUN sed -i \
    -e 's|"127\.0\.0\.1", "giny_world", "root", ""|Environment.GetEnvironmentVariable("DB_HOST") ?? "mysql", Environment.GetEnvironmentVariable("DB_NAME") ?? "giny_world", Environment.GetEnvironmentVariable("DB_USER") ?? "root", Environment.GetEnvironmentVariable("DB_PASSWORD") ?? ""|' \
    Sources/Sync/Giny.DatabaseSynchronizer/Program.cs

# Fix éventuels imports manquants (sécurité — using System; déjà présent normalement)
RUN grep -q '^using System;' Sources/Sync/Giny.DatabaseSynchronizer/Program.cs || \
    sed -i '1iusing System;' Sources/Sync/Giny.DatabaseSynchronizer/Program.cs

# Build
RUN dotnet publish Sources/Sync/Giny.DatabaseSynchronizer/Giny.DatabaseSynchronizer.csproj \
        -c Release -o /out --nologo /p:WarningLevel=0

# ---------- runtime ---------------------------------------------------------

FROM mcr.microsoft.com/dotnet/runtime:6.0-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
        libicu-dev ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /opt/sync
COPY --from=build /out/ /opt/sync/

# Le synchronizer suppose que /data contient :
#   data/common/*.d2o
#   data/i18n/i18n_fr.d2i
# (chemins relatifs à /data, montés depuis client/build/OneAir.app/Contents/Resources)
ENV DB_HOST=mysql
ENV DB_NAME=giny_world
ENV DB_USER=root
ENV DB_PASSWORD=
ENTRYPOINT ["dotnet", "/opt/sync/Giny.DatabaseSynchronizer.dll"]
