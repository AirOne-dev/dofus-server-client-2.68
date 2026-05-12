# Giny.NETCore — émulateur Dofus 2.x en .NET 6.
#
# Le source est vendoré dans server/giny/ (cf. server/giny/UPSTREAM.md).
# Plus de clone + sed à la build : on édite directement les .cs.
ARG DOTNET_VERSION=6.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS builder

WORKDIR /src
COPY giny/ /src/

# Build Auth + World uniquement. Les outils WPF Giny ne sont pas vendorés.
RUN dotnet publish Sources/Servers/Giny.Auth/Giny.Auth.csproj \
        -c Release -o /out/auth --nologo /p:WarningLevel=0 \
    && dotnet publish Sources/Servers/Giny.World/Giny.World.csproj \
        -c Release -o /out/world --nologo /p:WarningLevel=0

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
