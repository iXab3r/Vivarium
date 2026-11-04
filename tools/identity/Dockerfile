FROM node@sha256:a5f4366e834695d5f363b920c104953a535d3a9864d1bdf74566a0ebe01daee5 AS frontend
WORKDIR /frontend
COPY src/Vivarium.Controller/Workbench/package.json src/Vivarium.Controller/Workbench/package-lock.json ./
RUN npm ci --no-audit --fund=false
COPY src/Vivarium.Controller/Workbench/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk@sha256:ec9c0a0dc5f60adc2065762050638dd5ed5facfc53716d4580a42d86027c8e80 AS issuer-runtime
FROM mcr.microsoft.com/dotnet/sdk@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
COPY --from=issuer-runtime /usr/share/dotnet/shared/Microsoft.NETCore.App/ /usr/share/dotnet/shared/Microsoft.NETCore.App/
COPY --from=issuer-runtime /usr/share/dotnet/shared/Microsoft.AspNetCore.App/ /usr/share/dotnet/shared/Microsoft.AspNetCore.App/
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-16 libnss3-tools openssl \
    && rm -rf /var/lib/apt/lists/* && useradd --uid 10001 --create-home vivarium
WORKDIR /workspace
COPY --chown=10001:10001 . .
COPY --from=frontend --chown=10001:10001 /frontend/dist/ /workspace/src/Vivarium.Controller/Workbench/dist/
ADD fixtures/issuer.tar.gz /opt/eyeauras-issuer/
# The owner archive is labelled linux-arm64; this contour exercises its unchanged
# managed entry on native Linux x64. This is not an upstream packaging/support claim.
ENV VIVARIUM_ISSUER_PATH=/opt/eyeauras-issuer/EyeAuras.Web.OidcFixture.dll \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 PLAYWRIGHT_BROWSERS_PATH=/opt/vivarium-browsers \
    NUGET_PACKAGES=/home/vivarium/.nuget/packages
USER vivarium
RUN dotnet build tools/Vivarium.Automation --configuration Release \
    && dotnet build tests/Vivarium.Acceptance --configuration Release -p:BuildWorkbench=false
USER root
RUN dotnet tools/Vivarium.Automation/bin/Release/net10.0/Vivarium.Automation.dll prepare-browser --with-deps
USER vivarium
ENV VIVARIUM_HOST_PATH=/workspace/src/Vivarium.Controller/bin/Release/net10.0/Vivarium.Controller.dll
ENV DOTNET_CLI_HOME=/home/vivarium/dotnet-cli
RUN dotnet tools/Vivarium.Automation/bin/Release/net10.0/Vivarium.Automation.dll list --suite identity --property BuildWorkbench=false --environment 'HOME={workDirectory}/home' \
    && mkdir -p artifacts/prepared && mv artifacts/automation/identity/*/build artifacts/prepared/identity
# Runtime has no network, mounts, published ports or Host credentials. HOME belongs to the run.
CMD ["dotnet", "tools/Vivarium.Automation/bin/Release/net10.0/Vivarium.Automation.dll", "test", "--suite", "identity", "--no-build", "--build-directory", "artifacts/prepared/identity", "--property", "BuildWorkbench=false", "--environment", "HOME={workDirectory}/home"]
