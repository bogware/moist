# moist — headless encode server.
#
#   docker build -t moist .
#   docker run -p 7788:7788 -v /media:/media -e MOIST_TOKEN=secret moist
#
# The container serves 0.0.0.0 so the port can be published, which means it is
# reachable by anything that can reach the container. An encode names its own input
# and output paths, so that is a filesystem-level trust boundary: mount only what
# you intend it to touch, and keep the token secret.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files alone, so a source-only change doesn't
# re-download every package.
COPY src/Moist.sln ./
COPY src/Directory.Build.props ./
COPY src/Moist.Core/Moist.Core.csproj              Moist.Core/
COPY src/Moist.Filters/Moist.Filters.csproj        Moist.Filters/
COPY src/Moist.Services/Moist.Services.csproj      Moist.Services/
COPY src/Moist.Cli/Moist.Cli.csproj                Moist.Cli/
RUN dotnet restore Moist.Cli/Moist.Cli.csproj

COPY src/ ./
RUN dotnet publish Moist.Cli/Moist.Cli.csproj \
        --configuration Release \
        --no-restore \
        --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime

# FFmpeg: the same pinned, SHA-256-verified static build moist's own installer uses (see
# ToolDiscovery.KnownTools), not the distribution package. Ubuntu 24.04 ships FFmpeg 6.1 with
# dozens of unpatched CVEs and without libvmaf; this build is current (9.0.2), statically
# linked, and has x264, x265, SVT-AV1, VP9 and libvmaf — so --target-vmaf works here too.
# BtbN keeps month-end builds; to update, change both values together.
ARG FFMPEG_URL=https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n9.0.2-39-g7adfbe40dc-linux64-gpl-9.0.tar.xz
ARG FFMPEG_SHA256=73e29a56974ae5a651133a04584c7483b21afe941e17dc91d79814a758b608d3
RUN apt-get update \
    && apt-get upgrade -y \
    && apt-get install --no-install-recommends -y ca-certificates curl xz-utils \
    && curl -fsSL -o /tmp/ffmpeg.tar.xz "$FFMPEG_URL" \
    && echo "$FFMPEG_SHA256  /tmp/ffmpeg.tar.xz" | sha256sum -c - \
    && mkdir -p /opt/ffmpeg \
    && tar -xJf /tmp/ffmpeg.tar.xz -C /opt/ffmpeg --strip-components=1 \
    && ln -s /opt/ffmpeg/bin/ffmpeg /usr/local/bin/ffmpeg \
    && ln -s /opt/ffmpeg/bin/ffprobe /usr/local/bin/ffprobe \
    && rm -f /tmp/ffmpeg.tar.xz \
    && apt-get purge -y curl xz-utils \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Encoding as root would write output owned by root into mounted volumes. The service
# user is uid 1000, as it was on the earlier Debian-based image, so volumes written by
# either keep working. Ubuntu images already give uid 1000 to "ubuntu" — with sudo and adm
# among its groups — so that account is removed, not renamed, and moist starts clean.
RUN if id ubuntu >/dev/null 2>&1; then userdel --remove ubuntu; fi     && useradd --create-home --uid 1000 --user-group moist

WORKDIR /app
COPY --from=build /app .

# Settings, profiles and the queue live here; mount it to keep them across restarts.
# These have to exist and be owned before the VOLUME declaration — Docker creates a
# missing volume directory as root, and the container does not run as root, so moist
# could not write its own settings.
ENV XDG_CONFIG_HOME=/config
RUN mkdir -p /config /media && chown -R moist:moist /config /media /app
VOLUME ["/config", "/media"]

# The entrypoint runs whatever subcommand it is given, so `docker run moist tools`
# and `docker run moist health /media` work as well as serving. The token is NOT put
# on the command line: `moist serve` reads MOIST_TOKEN itself, because argv is
# visible to every process on the host (ps, /proc/<pid>/cmdline).
RUN printf '%s\n' \
        '#!/bin/sh' \
        'set -e' \
        'exec /app/moist "$@"' \
        > /usr/local/bin/entrypoint.sh \
    && chmod +x /usr/local/bin/entrypoint.sh

USER moist
EXPOSE 7788

# Unset makes moist generate a token and print it to the log, which is fine for a
# quick run and wrong for anything lasting.
ENV MOIST_TOKEN=""

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
# --root confines every request's input/output paths to the mounted media volume;
# serving a non-loopback address refuses to start without one.
CMD ["serve", "--host", "0.0.0.0", "--port", "7788", "--root", "/media"]
