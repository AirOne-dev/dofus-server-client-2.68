# OneAir web — interface unifiée (landing publique + login joueur + panel
# admin + reverse-proxy dbgate). Anciennement nommée "admin".
FROM golang:1.22-bookworm AS build
WORKDIR /src
COPY . .
RUN go mod tidy
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/web .

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates default-mysql-client gzip \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/web /usr/local/bin/web
EXPOSE 3000
ENTRYPOINT ["/usr/local/bin/web"]
