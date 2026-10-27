# Hosted conversion engine: PDF/LPT -> TriStation PLCopen XML -> (PT2X | TIA package).
#
# Bundles the four out-of-process tools the server drives:
#   circuit-reader   PDF/LPT -> TRISTATION equation listing
#   BlLadderCli      equation workbook -> TriStation PLCopen XML   (BL.Core port, see tools/bl-core)
#   pt2conv          XML -> PT2X container
#   TriconexToTia.Server  HTTP front door and orchestrator
#
# /convert/project (a real .ap16, via Siemens Openness) is refused on Linux by design; the
# SimaticML package from /convert is the importable artifact this image produces for TIA.

FROM mcr.microsoft.com/dotnet/sdk:7.0-jammy AS build
WORKDIR /src

# Build for the architecture of the target image (amd64 on x64 hosts, arm64 on
# Graviton/ARM hosts). Docker BuildKit supplies TARGETARCH; default to amd64 for
# builders that do not.
ARG TARGETARCH=amd64
RUN echo "Building for linux-$TARGETARCH"

COPY engine/ engine/
COPY tools/ tools/
COPY pt2-conv/src/ pt2-conv/src/
COPY circuit-reader/ circuit-reader/

RUN dotnet publish engine/src/TriconexToTia.Server/TriconexToTia.Server.csproj \
        -c Release -r linux-$TARGETARCH --self-contained false -o /out/engine
RUN dotnet publish circuit-reader/src/CircuitReader.Cli/CircuitReader.Cli.csproj \
        -c Release -r linux-$TARGETARCH --self-contained false -o /out/circuit-reader
RUN dotnet publish pt2-conv/src/Pt2Conv/Pt2Conv.csproj \
        -c Release -r linux-$TARGETARCH --self-contained false -o /out/pt2conv
RUN dotnet publish tools/BlLadderCli.Portable/BlLadderCli.Portable.csproj \
        -c Release -r linux-$TARGETARCH --self-contained false -o /out/bl

# Ubuntu 22.04 on both stages: it is the newest base that still packages libgdiplus (Debian
# dropped it in bookworm), its mirrors are current, and building and running on the same
# libc avoids a glibc mismatch.
FROM mcr.microsoft.com/dotnet/aspnet:7.0-jammy AS runtime

# EPPlus 4 (used by the BL reader) can reach into System.Drawing on some code paths, so keep
# GDI+ available instead of discovering that at runtime.
#
# PdfSharpCore resolves the report templates' Arial/Times New Roman/Courier New through
# fontconfig on Linux. Liberation Sans/Serif/Mono are metric-compatible with those fonts and
# Ubuntu's fontconfig aliases the Windows names onto them, so the printed layout matches the
# Windows build instead of failing with "no appropriate font".
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgdiplus fontconfig fonts-liberation \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /out /opt

# BL resolves InstanceRuleset and BlankData as siblings of its own assembly.
RUN mkdir -p /opt/bl/InstanceRuleset

ENV MSW_CIRCUITREADER_PATH=/opt/circuit-reader/circuit-reader \
    MSW_LADDERCLI_PATH=/opt/bl/BlLadderCli \
    MSW_PT2CONV_PATH=/opt/pt2conv/pt2conv

WORKDIR /opt/engine
ENTRYPOINT ["dotnet", "/opt/engine/TriconexToTia.Server.dll"]
