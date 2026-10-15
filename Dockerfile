# Queueify.Web: build from the repository root, `docker build -t queueify-web .`
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# The bracket globs copy the shared build files when the repository has them.
COPY global.json Queueify.slnx Directory.Build.prop[s] Directory.Packages.prop[s] ./
COPY Queueify.Core/ Queueify.Core/
COPY Queueify.Web/ Queueify.Web/
RUN dotnet publish Queueify.Web/Queueify.Web.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS run
# tesseract reads the photos; curl is only for the health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng tesseract-ocr-spa curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown 1654:1654 /data
WORKDIR /app
COPY --from=build /out .

# Everything the server keeps (accounts, queues, keys, models) lives in /data: mount a volume there and back it up whole.
ENV Queueify__DataDir=/data \
    Ocr__TessdataPath=/usr/share/tesseract-ocr/5/tessdata \
    ASPNETCORE_HTTP_PORTS=8080
VOLUME /data
EXPOSE 8080
# The image's built-in non-root user (uid 1654).
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl -fsS http://localhost:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Queueify.Web.dll"]
