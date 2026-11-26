# Build image for the InfiniFrame CEF runtime on Linux (linux-x64 and linux-arm64, Ubuntu 24.04 "noble").
# The companion is built natively for the image's architecture: on an ARM64 host or runner for linux-arm64
# (or under QEMU emulation with --platform linux/arm64). The runtime libraries below are needed only so the linker
# can resolve libcef.so's dependencies; the resulting payload targets glibc 2.39 or older.
ARG BASE=ubuntu:24.04
FROM ${BASE}
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update -qq \
 && apt-get install -y -qq --no-install-recommends \
      g++ make cmake ninja-build binutils file bzip2 ca-certificates git python3 \
      libx11-6 libglib2.0-0t64 libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 libcups2t64 libdbus-1-3 \
      libxcomposite1 libxdamage1 libxext6 libxfixes3 libxrandr2 libgbm1 libexpat1 libxcb1 libxkbcommon0 \
      libcairo2 libpango-1.0-0 libudev1 libasound2t64 libatspi2.0-0t64 \
 && rm -rf /var/lib/apt/lists/*
# The build needs no privileges; outputs are written to the bind-mounted work folder.
RUN useradd --create-home --uid 1001 builder
USER builder
WORKDIR /src
