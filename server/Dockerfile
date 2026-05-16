# Giny.NETCore — émulateur Dofus 2.x en .NET 6.
#
# Le source est vendoré dans server/giny/ (cf. server/giny/UPSTREAM.md).
# Plus de clone + sed à la build : on édite directement les .cs.
#
# Targets disponibles :
#   runtime       → release (défaut, optimisé)
#   runtime-debug → debug   (avec vsdbg, pour attacher VSCodium)
#
# Usage debug :
#   docker compose --profile debug up --build
ARG DOTNET_VERSION=6.0

# ---------------------------------------------------------------------------
# Stage 1 — Build Release
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS builder

WORKDIR /src
COPY giny/ /src/

# Build Auth + World uniquement. Les outils WPF Giny ne sont pas vendorés.
RUN dotnet publish Sources/Servers/Giny.Auth/Giny.Auth.csproj \
        -c Release -o /out/auth --nologo /p:WarningLevel=0 \
    && dotnet publish Sources/Servers/Giny.World/Giny.World.csproj \
        -c Release -o /out/world --nologo /p:WarningLevel=0

# ---------------------------------------------------------------------------
# Stage 2 — Build Debug (symbols conservés, pas d'optimisations JIT)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS builder-debug

WORKDIR /src
COPY giny/ /src/

RUN dotnet publish Sources/Servers/Giny.Auth/Giny.Auth.csproj \
        -c Debug -o /out/auth --nologo /p:WarningLevel=0 \
        /p:Deterministic=false /p:DebugType=full /p:DebugSymbols=true \
    && dotnet publish Sources/Servers/Giny.World/Giny.World.csproj \
        -c Debug -o /out/world --nologo /p:WarningLevel=0 \
        /p:Deterministic=false /p:DebugType=full /p:DebugSymbols=true

# ---------------------------------------------------------------------------
# Stage 3 — Runtime Release (target: runtime)
# ---------------------------------------------------------------------------
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

# ---------------------------------------------------------------------------
# Stage 4 — Runtime Debug (target: runtime-debug)
#   - Binaires compilés en Debug (PDB inclus)
#   - vsdbg installé dans /vsdbg
#   - Variable DOTNET_JitOptimizations=0 pour des breakpoints fiables
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-bookworm-slim AS runtime-debug

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates tini netcat-openbsd default-mysql-client gettext-base \
        curl unzip \
    && curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v 17.0.10712.2 -l /vsdbg \
    && curl -Lo /tmp/netcoredbg.tar.gz https://github.com/Samsung/netcoredbg/releases/download/3.1.2-1054/netcoredbg-linux-amd64.tar.gz \
    && tar -xzf /tmp/netcoredbg.tar.gz -C /usr/local/bin --strip-components=1 \
    && chmod +x /usr/local/bin/netcoredbg \
    && rm /tmp/netcoredbg.tar.gz \
    && rm -rf /var/lib/apt/lists/*

ENV DOTNET_JitOptimizations=0 \
    DOTNET_TieredCompilation=0 \
    DOTNET_EnableDiagnostics=1 \
    COMPlus_ZapDisable=1

WORKDIR /opt/giny
COPY --from=builder-debug /out/auth  /opt/giny/auth/
COPY --from=builder-debug /out/world /opt/giny/world/

COPY SWF/ /opt/giny/auth/SWF/
COPY SWF/ /opt/giny/world/SWF/

COPY config/ /etc/giny/config/

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

EXPOSE 443 5555 800 9000 9001
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
CMD ["auth"]