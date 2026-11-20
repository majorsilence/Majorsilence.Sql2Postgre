# PostgreSQL with PostGIS, for a version postgis/postgis has no image of yet (19): the PGDG
# package the image's repository already has. The package is named by the image's own PG_MAJOR,
# as in postgres-cron/Dockerfile.
ARG PG_VERSION=19
FROM postgres:${PG_VERSION}
RUN apt-get update \
    && apt-get install -y --no-install-recommends "postgresql-${PG_MAJOR}-postgis-3" \
    && rm -rf /var/lib/apt/lists/*
