CREATE EXTENSION IF NOT EXISTS postgis;

CREATE TABLE IF NOT EXISTS osm_nodes (
    id    BIGSERIAL PRIMARY KEY,
    geom  GEOMETRY(Point, 4326) NOT NULL
);

CREATE INDEX IF NOT EXISTS osm_nodes_geom_idx
    ON osm_nodes USING GIST (geom);

CREATE TABLE IF NOT EXISTS import_meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
