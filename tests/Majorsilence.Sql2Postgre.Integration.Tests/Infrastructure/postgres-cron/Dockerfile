# PostgreSQL with pg_cron, which the stock image leaves out: the PGDG package the image's
# repository already has. pg_cron is loaded at start (shared_preload_libraries) and runs jobs in
# the database cron.database_name names; the test passes both as server options.
ARG PG_VERSION=18
FROM postgres:${PG_VERSION}
ARG PG_VERSION=18
RUN apt-get update \
    && apt-get install -y --no-install-recommends "postgresql-${PG_VERSION}-cron" \
    && rm -rf /var/lib/apt/lists/*
