-- 0025: nearest-neighbour index for CPT description suggestions.
--
-- The unmapped-code report suggests a CPT for every unmapped Novarad service code
-- by trigram similarity against the CPT master. The old query called similarity()
-- for every (code x master row) pair: 2,000 codes x 564 rows took 22 s on the test
-- server, past the 30 s request limit, so the page failed against the real Novarad.
-- A GiST trigram index supports the <-> distance operator's k-nearest-neighbour
-- scan, so each code costs one index probe instead of a full pass: 1.4 s for the
-- same 2,000 codes. The existing GIN index stays for the search box (LIKE / %).

CREATE INDEX IF NOT EXISTS idx_cpt_codes_descr_knn
    ON billing.cpt_codes USING gist (description gist_trgm_ops);
