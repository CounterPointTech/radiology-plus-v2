// Thin typed wrappers over the API. Keeps endpoint strings out of components.

import { apiClient, get, post } from "./api-client";
import type {
  BulkImportResult,
  CandidateOrder,
  ComparisonCandidate,
  CptCode,
  CptImport,
  CrosswalkBulkImportRequest,
  CrosswalkListResponse,
  CrosswalkSuggestionsResponse,
  CrosswalkUpsertRequest,
  DoTheDoOutcome,
  PatientSearchResult,
  ReadyStudy,
  ReconciliationLineDetailResponse,
  ReconciliationRun,
  RunReconciliationRequest,
  StudyMergeOutcome,
  StudyMergeRequest,
  ServiceCodeMapping,
  StartValidationRequest,
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

  step(validationId: string, step: 1 | 2 | 3 | 4 | 5, body: StepRequest) {
    return post<ValidationRecord>(
      `/tech-validation/${validationId}/step/${step}`,
      body,
    );
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
