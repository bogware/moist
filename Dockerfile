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

# FFmpeg is not one program: what a build contains is decided when it is compiled.
# The .NET 10 images are Ubuntu 24.04, whose FFmpeg 6.1 has x264, x265, VP9 and SVT-AV1
# but NOT libvmaf — so --target-vmaf is unavailable in this image (the XPSNR, SSIM and
# PSNR targets still work) unless you install a build that has it. `moist tools`
# reports what was found.
RUN apt-get update \
    && apt-get install --no-install-recommends -y ffmpeg \
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
