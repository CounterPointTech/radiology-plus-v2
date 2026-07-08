// Thin typed wrappers over the API. Keeps endpoint strings out of components.

import { apiClient, get, getBlob, post } from "./api-client";
import type {
  BannerCreateRequest,
  BannerUpdateRequest,
  BulkImportResult,
  CandidateOrder,
  ComparisonCandidate,
  CptCode,
  CptImport,
  CptMasterCmsRow,
  CrosswalkBulkImportRequest,
  CrosswalkListResponse,
  CrosswalkSuggestionsResponse,
  CrosswalkUpsertRequest,
  DoTheDoOutcome,
  PatientJacketEntry,
  PatientSearchResult,
  ReportContent,
  ReadyStudy,
  ReconciliationLineDetailResponse,
  ReconciliationRun,
  RunReconciliationRequest,
  MModalConnectionInfo,
  MModalConnectionRequest,
  MModalIssuer,
  RvuConnectionTest,
  RvuImport,
  RvuOverride,
  RvuOverrideRequest,
  RvuRestoreResult,
  RvuSyncPreview,
  RvuSyncResult,
  RvuSyncRun,
  RvuSyncStatus,
  RvuValue,
  RvuWriteBackSnapshot,
  StudyMergeOutcome,
  StudyMergeRequest,
  ServiceCodeMapping,
  StartValidationRequest,
  StatusBanner,
  StepRequest,
  TechNotesTemplate,
  TemplateUpsertRequest,
  UnmappedCodesResponse,
  ValidationRecord,
} from "./types";

export const techApi = {
  worklist(params?: { facility?: number; limit?: number }) {
    return get<ReadyStudy[]>("/tech-validation/worklist", params);
  },

  start(req: StartValidationRequest) {
    return post<ValidationRecord>("/tech-validation/start", req);
  },

  get(validationId: string) {
    return get<ValidationRecord>(`/tech-validation/${validationId}`);
  },

  step(validationId: string, step: 1 | 2 | 3 | 4 | 5 | 6, body: StepRequest) {
    return post<ValidationRecord>(
      `/tech-validation/${validationId}/step/${step}`,
      body,
    );
  },

  patientJacket(
    novaradPatientId: number,
    params: { currentStudyId: number; description?: string | null; modality?: string | null; limit?: number },
  ) {
    return get<PatientJacketEntry[]>(`/tech-validation/jacket/${novaradPatientId}`, {
      currentStudyId: params.currentStudyId,
      description: params.description ?? undefined,
      modality: params.modality ?? undefined,
      limit: params.limit,
    });
  },

  ordersByPatient(patientPid: string) {
    return get<CandidateOrder[]>(
      `/tech-validation/orders/by-patient/${encodeURIComponent(patientPid)}`,
    );
  },

  comparisons(novaradPatientId: number, lastName?: string, limit = 25) {
    return get<ComparisonCandidate[]>(
      `/tech-validation/comparisons/${novaradPatientId}`,
      { lastName, limit },
    );
  },

  patientSearch(q: string, limit = 20) {
    return get<PatientSearchResult[]>("/tech-validation/patients/search", {
      q,
      limit,
    });
  },

  templates() {
    return get<TechNotesTemplate[]>("/tech-validation/templates");
  },

  createTemplate(req: TemplateUpsertRequest) {
    return post<TechNotesTemplate>("/tech-validation/templates", req);
  },

  async updateTemplate(templateId: number, req: TemplateUpsertRequest) {
    const res = await apiClient.put<TechNotesTemplate>(
      `/tech-validation/templates/${templateId}`,
      req,
    );
    return res.data;
  },

  async deleteTemplate(templateId: number) {
    await apiClient.delete(`/tech-validation/templates/${templateId}`);
  },

  finalize(validationId: string) {
    return post<DoTheDoOutcome>(`/tech-validation/${validationId}/finalize`);
  },

  cancel(validationId: string) {
    return post<void>(`/tech-validation/${validationId}/cancel`);
  },

  devRefresh() {
    return post<{ projected: number }>("/tech-validation/dev/refresh");
  },

  mergeStudies(req: StudyMergeRequest) {
    return post<StudyMergeOutcome>("/tech-validation/studies/merge", req);
  },
};

export const billingApi = {
  listCptMaster(params: { year?: number; q?: string; limit?: number }) {
    return get<CptCode[]>("/billing/cpt-master", params);
  },

  listImports(limit = 25) {
    return get<CptImport[]>("/billing/cpt-master/imports", { limit });
  },

  async patchCptCode(
    code: string,
    body: { year: number; workRvu?: number; description?: string; notes?: string | null },
  ) {
    const res = await apiClient.patch<CptCode>(
      `/billing/cpt-master/${encodeURIComponent(code)}`,
      body,
    );
    return res.data;
  },

  /**
   * Multipart upload. Bypasses the JSON-body get/post wrappers because axios
   * needs the raw FormData to set the boundary header correctly.
   */
  async importCptMaster(file: File, year?: number, sheet?: string) {
    const form = new FormData();
    form.append("file", file);
    const params: Record<string, string | number> = {};
    if (year != null) params.year = year;
    if (sheet) params.sheet = sheet;
    const res = await apiClient.post<CptImport>(
      "/billing/cpt-master/import",
      form,
      { params, headers: { "Content-Type": "multipart/form-data" } },
    );
    return res.data;
  },

  // ------------------------------------------------------------------------
  // CMS RVU values (billing.rvu_values) + manual overrides — item 1.2
  // ------------------------------------------------------------------------

  listRvuValues(params: {
    year?: number;
    quarter?: string;
    q?: string;
    limit?: number;
  }) {
    return get<RvuValue[]>("/billing/rvu", params);
  },

  listRvuImports(limit = 5) {
    return get<RvuImport[]>("/billing/rvu/imports", { limit });
  },

  /**
   * Multipart upload of a CMS PPRRVU zip (or bare csv). Raw FormData so axios sets
   * the multipart boundary; mirrors importCptMaster.
   */
  async importRvu(file: File, year: number, quarter: string) {
    const form = new FormData();
    form.append("file", file);
    const res = await apiClient.post<RvuImport>("/billing/rvu/import", form, {
      params: { year, quarter },
      headers: { "Content-Type": "multipart/form-data" },
    });
    return res.data;
  },

  // Returns both tenant-wide (siteCode null) and site-specific overrides for the year.
  listRvuOverrides(params?: { year?: number }) {
    return get<RvuOverride[]>("/billing/rvu/overrides", params);
  },

  async upsertRvuOverride(code: string, body: RvuOverrideRequest) {
    const res = await apiClient.put<RvuOverride>(
      `/billing/rvu/overrides/${encodeURIComponent(code)}`,
      body,
    );
    return res.data;
  },

  // siteCode null/omitted targets the tenant-wide override; a value targets that site's.
  async deleteRvuOverride(code: string, year: number, siteCode?: string | null) {
    await apiClient.delete(`/billing/rvu/overrides/${encodeURIComponent(code)}`, {
      params: siteCode ? { year, siteCode } : { year },
    });
  },

  /** Every distinct Novarad site_code the customer books orders at (~166) — the facility
   *  universe for the crosswalk facility-scoping picker and the per-site RVU override picker. */
  listSites() {
    return get<string[]>("/billing/sites");
  },

  /** CPT master joined to CMS truth for the management view's "CMS check" surface. */
  cptMasterCmsCheck(params: { year: number; limit?: number }) {
    return get<CptMasterCmsRow[]>("/billing/cpt-master/cms-check", params);
  },

  // ── M*Modal connection settings (in-app provisioning) ─────────────────────

  /** The tenant's M*Modal connection config (password redacted), or configured=false. */
  getMModalConnection() {
    return get<{ configured: boolean; connection: MModalConnectionInfo | null }>(
      "/billing/rvu/sync/connection",
    );
  },

  /** Create or update the M*Modal connection (password omitted = keep existing). */
  async saveMModalConnection(body: MModalConnectionRequest) {
    const res = await apiClient.put<MModalConnectionInfo>("/billing/rvu/sync/connection", body);
    return res.data;
  },

  /** Open the configured connection and report reachability + issuer count. */
  async testMModalConnection() {
    const res = await apiClient.post<RvuConnectionTest>("/billing/rvu/sync/connection/test", null);
    return res.data;
  },

  async deleteMModalConnection() {
    await apiClient.delete("/billing/rvu/sync/connection");
  },

  // ── M*Modal RVU write-back (project-ffi-rvu-writeback) ────────────────────

  /** Is the M*Modal write-back configured for this tenant + the most recent run. */
  rvuSyncStatus() {
    return get<RvuSyncStatus>("/billing/rvu/sync/status");
  },

  /** The M*Modal issuers (facilities) with active exam codes — the sync-target picker. */
  listSyncIssuers() {
    return get<MModalIssuer[]>("/billing/rvu/sync/issuers");
  },

  /** Recent M*Modal write-back run history (newest first). */
  listRvuSyncRuns(limit = 10) {
    return get<RvuSyncRun[]>("/billing/rvu/sync/runs", { limit });
  },

  /**
   * Dry run: diff our effective RVUs against what M*Modal stores (no write). Scope is
   * exactly one facility ({ issuerKey }) or every facility ({ allIssuers: true }).
   */
  async rvuSyncPreview(
    year: number,
    quarter: string,
    scope: { issuerKey?: string; allIssuers?: boolean },
  ) {
    const res = await apiClient.post<RvuSyncPreview>("/billing/rvu/sync/preview", null, {
      params: { year, quarter, issuerKey: scope.issuerKey, allIssuers: scope.allIssuers },
    });
    return res.data;
  },

  /** Apply the diff: write the changed RVUs into M*Modal (transactional, audited). */
  async rvuSyncApply(
    year: number,
    quarter: string,
    scope: { issuerKey?: string; allIssuers?: boolean },
  ) {
    const res = await apiClient.post<RvuSyncResult>("/billing/rvu/sync", null, {
      params: { year, quarter, issuerKey: scope.issuerKey, allIssuers: scope.allIssuers },
    });
    return res.data;
  },

  // ── M*Modal RVU backups / restore points ──────────────────────────────────

  /** Recent backups (restore points), newest first. */
  listSnapshots(limit = 25) {
    return get<RvuWriteBackSnapshot[]>("/billing/rvu/sync/snapshots", { limit });
  },

  /** Capture a backup of a facility's (or all facilities') current RVUs right now. */
  async backupNow(scope: { issuerKey?: string; allIssuers?: boolean }) {
    const res = await apiClient.post<RvuWriteBackSnapshot>("/billing/rvu/sync/backup", null, {
      params: { issuerKey: scope.issuerKey, allIssuers: scope.allIssuers },
    });
    return res.data;
  },

  /** Restore a backup: write its exact values back into M*Modal (transactional, audited). */
  async restoreSnapshot(id: number) {
    const res = await apiClient.post<RvuRestoreResult>(
      `/billing/rvu/sync/snapshots/${id}/restore`,
      null,
    );
    return res.data;
  },

  async deleteSnapshot(id: number) {
    await apiClient.delete(`/billing/rvu/sync/snapshots/${id}`);
  },

  /** Download a backup as CSV ({ blob, filename }). */
  exportSnapshot(id: number) {
    return getBlob(`/billing/rvu/sync/snapshots/${id}/export`);
  },

  /** Upload a CSV of (IssuerKey, Code, RelativeValueUnit) as a new backup. */
  async importSnapshot(file: File) {
    const form = new FormData();
    form.append("file", file);
    const res = await apiClient.post<RvuWriteBackSnapshot>(
      "/billing/rvu/sync/snapshots/import",
      form,
      { headers: { "Content-Type": "multipart/form-data" } },
    );
    return res.data;
  },

  runReconciliation(req: RunReconciliationRequest) {
    return post<ReconciliationRun>("/billing/reconciliation/run", req);
  },

  reconciliationLineDetail(params: {
    runId: number;
    physicianId: number;
    cptCode: string;
    siteCode: string;
  }) {
    return get<ReconciliationLineDetailResponse>(
      `/billing/reconciliation/${params.runId}/detail`,
      {
        physicianId: params.physicianId,
        cptCode: params.cptCode,
        siteCode: params.siteCode,
      },
    );
  },

  unmappedCodes(params: { from?: string; to?: string; site?: string }) {
    return get<UnmappedCodesResponse>("/billing/reconciliation/unmapped", params);
  },

  exportReconciliation(
    runId: number,
    params?: { physicianId?: number | null; siteCode?: string | null },
  ) {
    return getBlob(`/billing/reconciliation/${runId}/export`, {
      physicianId: params?.physicianId ?? undefined,
      siteCode: params?.siteCode ?? undefined,
    });
  },

  reportContent(reportId: number) {
    return get<ReportContent>(`/billing/reconciliation/report/${reportId}`);
  },

  // ------------------------------------------------------------------------
  // Phase 2 — service_code → CPT crosswalk
  // ------------------------------------------------------------------------

  listCrosswalk(params?: { status?: 1 | 2 }) {
    return get<CrosswalkListResponse>("/billing/crosswalk", params);
  },

  crosswalkSuggestions(params: {
    serviceCode: string;
    description?: string;
    year?: number;
    limit?: number;
  }) {
    return get<CrosswalkSuggestionsResponse>("/billing/crosswalk/suggestions", params);
  },

  createCrosswalk(req: CrosswalkUpsertRequest) {
    return post<ServiceCodeMapping>("/billing/crosswalk", req);
  },

  async updateCrosswalk(serviceCode: string, req: CrosswalkUpsertRequest) {
    const res = await apiClient.put<ServiceCodeMapping>(
      `/billing/crosswalk/${encodeURIComponent(serviceCode)}`,
      req,
    );
    return res.data;
  },

  bulkImportCrosswalk(req: CrosswalkBulkImportRequest) {
    return post<BulkImportResult>("/billing/crosswalk/bulk", req);
  },
};

// ---------------------------------------------------------------------------
// Admin — status banner (core.status_banners)
// ---------------------------------------------------------------------------

export const adminApi = {
  /** The poll target: the single active banner for this tenant, or null. Authenticated. */
  activeBanner() {
    return get<StatusBanner | null>("/announcements/active");
  },

  /** Admin: list all banners (active + inactive), newest first. */
  listBanners(params?: { limit?: number }) {
    return get<StatusBanner[]>("/announcements/banners", params);
  },

  createBanner(req: BannerCreateRequest) {
    return post<StatusBanner>("/announcements/banners", req);
  },

  async updateBanner(bannerId: number, req: BannerUpdateRequest) {
    const res = await apiClient.put<StatusBanner>(
      `/announcements/banners/${bannerId}`,
      req,
    );
    return res.data;
  },

  async setBannerActive(bannerId: number, isActive: boolean) {
    const res = await apiClient.patch<StatusBanner>(
      `/announcements/banners/${bannerId}/active`,
      { isActive },
    );
    return res.data;
  },

  async deleteBanner(bannerId: number) {
    await apiClient.delete(`/announcements/banners/${bannerId}`);
  },
};
