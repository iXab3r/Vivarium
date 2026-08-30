FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble

RUN apt-get update \
    && apt-get install --yes --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

ARG VIVARIUM_VERSION=0.0.0
ARG VIVARIUM_SOURCE_SHA=unknown
LABEL org.opencontainers.image.title="Vivarium Server" \
      org.opencontainers.image.source="https://github.com/iXab3r/Vivarium" \
      org.opencontainers.image.version="$VIVARIUM_VERSION" \
      org.opencontainers.image.revision="$VIVARIUM_SOURCE_SHA"

WORKDIR /app
COPY . .

RUN chmod 0755 /app/viv-server \
    && mkdir --parents /var/lib/vivarium \
    && chown app:app /var/lib/vivarium

USER app
ENV VIVARIUM_DATA=/var/lib/vivarium
EXPOSE 8443
VOLUME ["/var/lib/vivarium"]

ENTRYPOINT ["/app/viv-server"]
