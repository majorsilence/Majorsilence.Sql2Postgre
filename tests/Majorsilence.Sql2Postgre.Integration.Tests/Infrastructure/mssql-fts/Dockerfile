# SQL Server 2022 with Full-Text Search, which the stock image leaves out: Microsoft's
# mssql-server-fts package from Microsoft's package repository (whose key the image already
# trusts). The image's engine is not a dpkg package, so the package brings mssql-server of its own
# version with it, and the two match.
FROM mcr.microsoft.com/mssql/server:2022-latest
USER root
RUN echo "deb [arch=amd64] https://packages.microsoft.com/ubuntu/22.04/mssql-server-2022 jammy main" > /etc/apt/sources.list.d/mssql-server-2022.list \
    && apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends mssql-server-fts \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*
USER mssql
