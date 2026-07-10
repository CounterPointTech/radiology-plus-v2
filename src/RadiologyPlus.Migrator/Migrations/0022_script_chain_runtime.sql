-- Radiology Plus v2.1 — Script chain runtime
--
-- scripting.script_chains + script_chain_links existed since 0001 but nothing
-- consumed them: chains couldn't be scheduled (no cron), there was no chain run
-- history, and step executions couldn't be tied back to the chain run that
-- spawned them (executions only carry triggered_by='chain'). This adds the
-- runtime surface the ChainRunner + Chains console need:
--
--   script_chains.cron_expression      — chains schedulable like scripts (UTC)
--   script_chains.notify_on_failure_*  — optional email on chain failure, via
--                                        the notifications queue (recipient +
--                                        optional Handlebars template)
--   script_chain_links.continue_on_failure — per-step "okay to fail" override:
--                                        on a stop-on-failure chain, this step's
--                                        failure doesn't abort the run (and
--                                        doesn't fail the chain)
--   scripting.chain_runs               — one row per chain run (audit header,
--                                        mirrors scripting.executions shape)
--   scripting.executions.chain_run_id  — links each step execution to its run;
--                                        SET NULL on run deletion so execution
--                                        history survives chain cleanup
--
-- The 'branch' on_failure token from 0001 stays unsupported (no branch-target
-- columns exist); the runner and UI only offer stop | continue.

ALTER TABLE scripting.script_chains
    ADD COLUMN cron_expression TEXT,
    ADD COLUMN notify_on_failure_recipient TEXT,
    ADD COLUMN notify_on_failure_template UUID REFERENCES notifications.templates(template_id) ON DELETE SET NULL;

COMMENT ON COLUMN scripting.script_chains.cron_expression IS
    'Cron schedule (UTC), same dual format as scripts. NULL = on demand only.';
COMMENT ON COLUMN scripting.script_chains.notify_on_failure_recipient IS
    'Email recipient for a failure notification. NULL = no notification.';
COMMENT ON COLUMN scripting.script_chains.notify_on_failure_template IS
    'Optional notifications.templates row (email channel) rendered for the failure mail; NULL = built-in message.';

ALTER TABLE scripting.script_chain_links
    ADD COLUMN continue_on_failure BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN scripting.script_chain_links.continue_on_failure IS
    'When TRUE this step may fail without aborting a stop-on-failure chain or failing the run.';

-- ============================================================================
-- Chain run history
-- ============================================================================
CREATE TABLE scripting.chain_runs (
    chain_run_id      BIGSERIAL PRIMARY KEY,
    chain_id          UUID NOT NULL REFERENCES scripting.script_chains(chain_id) ON DELETE CASCADE,
    tenant_id         UUID NOT NULL,
    triggered_by      TEXT NOT NULL CHECK (triggered_by IN ('schedule', 'manual')),
    triggered_by_user UUID,
    status            TEXT NOT NULL CHECK (status IN ('pending', 'running', 'success', 'failed', 'cancelled')),
    started_at        TIMESTAMPTZ,
    completed_at      TIMESTAMPTZ,
    duration_ms       INTEGER,
    steps_total       INTEGER NOT NULL DEFAULT 0,
    steps_succeeded   INTEGER NOT NULL DEFAULT 0,
    steps_failed      INTEGER NOT NULL DEFAULT 0,
    error_summary     TEXT,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_chain_runs_chain_time ON scripting.chain_runs(chain_id, created_at DESC);
CREATE INDEX idx_chain_runs_tenant_time ON scripting.chain_runs(tenant_id, created_at DESC);
COMMENT ON TABLE scripting.chain_runs IS
    'One row per script-chain run. Step-level detail lives in scripting.executions via chain_run_id.';

ALTER TABLE scripting.executions
    ADD COLUMN chain_run_id BIGINT REFERENCES scripting.chain_runs(chain_run_id) ON DELETE SET NULL;
CREATE INDEX idx_executions_chain_run ON scripting.executions(chain_run_id) WHERE chain_run_id IS NOT NULL;

-- ============================================================================
-- RLS — same tenant isolation the 0001 loop applied to the scripting schema
-- (that loop ran once at 0001 time; new tables add the policies explicitly).
-- ============================================================================
ALTER TABLE scripting.chain_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE scripting.chain_runs FORCE  ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON scripting.chain_runs
    USING (tenant_id = core.current_tenant())
    WITH CHECK (tenant_id = core.current_tenant());
CREATE POLICY system_bypass ON scripting.chain_runs
    AS PERMISSIVE FOR ALL TO PUBLIC
    USING (core.current_tenant() IS NULL);

-- ============================================================================
-- Record migration
-- ============================================================================
INSERT INTO core.schema_migrations (version, checksum) VALUES ('0022_script_chain_runtime', 'manual')
ON CONFLICT (version) DO NOTHING;
