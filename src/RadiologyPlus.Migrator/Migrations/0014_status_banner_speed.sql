-- Radiology Plus v2.1 - Status banner marquee speed
-- Adds core.status_banners.marquee_speed (1-10), used only when is_animated. The web
-- client normalizes it to a constant pixels-per-second scroll so the preview and the
-- live banner move at the same visual speed regardless of bar width. Default 5 (mid).

ALTER TABLE core.status_banners
    ADD COLUMN marquee_speed SMALLINT NOT NULL DEFAULT 5;

ALTER TABLE core.status_banners
    ADD CONSTRAINT chk_status_banners_marquee_speed CHECK (marquee_speed BETWEEN 1 AND 10);

INSERT INTO core.schema_migrations (version, checksum) VALUES ('0014_status_banner_speed', 'manual')
ON CONFLICT (version) DO NOTHING;
