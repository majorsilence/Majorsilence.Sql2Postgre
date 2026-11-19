# PostgreSQL with pg_cron, which the stock image leaves out: the PGDG package the image's
# repository already has. pg_cron is loaded at start (shared_preload_libraries) and runs jobs in
# the database cron.database_name names; the test passes both as server options.
# The package is named by the image's own PG_MAJOR: the image's ENV PG_VERSION (16.16-1.pgdg13+1)
# overrides a build argument of that name in the classic builder Testcontainers uses.
ARG PG_VERSION=18
FROM postgres:${PG_VERSION}
RUN apt-get update \
    && apt-get install -y --no-install-recommends "postgresql-${PG_MAJOR}-cron" \
    && rm -rf /var/lib/apt/lists/*
