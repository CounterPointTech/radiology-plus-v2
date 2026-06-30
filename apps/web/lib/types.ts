// Mirror of the .NET DTOs documented in
// .claude/state/tech-validation-api-contract.md. Keep in sync when the
// contract changes.

// Role is serialized as the C# enum name ("NRS" | "Admin" | "Tech" | "Radiologist").
// LoginResponse maps user.Role via .ToString() — see RadiologyPlus.API/Endpoints/AuthEndpoints.cs.
export type Role = "NRS" | "Admin" | "Tech" | "Radiologist";

export function roleLabel(role: Role | string): string {
  switch (role) {
    case "NRS":
      return "NRS";
    case "Admin":
      return "Admin";
    case "Tech":
      return "Tech";
    case "Radiologist":
      return "Radiologist";
    default:
      return role;
  }
}

export function isNrs(role: Role | string): boolean {
  return role === "NRS";
}

export function canAccessTechValidation(role: Role | string): boolean {
  return role === "NRS" || role === "Admin" || role === "Tech";
}

export function canAccessBilling(role: Role | string): boolean {
  return role === "NRS" || role === "Admin";
}

export function canManageTemplates(role: Role | string): boolean {
  return role === "NRS" || role === "Admin";
}

export function canAccessAdmin(role: Role | string): boolean {
  return role === "NRS" || role === "Admin";
}

// ---------------------------------------------------------------------------
// Status banner (core.status_banners) — admin-authored app-wide notice.
// Mirror of RadiologyPlus.Core/Announcements/AnnouncementModels.cs.
// severity: 1 info · 2 maintenance · 3 warning · 4 critical.
// ---------------------------------------------------------------------------

export const BannerSeverity = {
  Info: 1,
  Maintenance: 2,
  Warning: 3,
  Critical: 4,
} as const;

export interface StatusBanner {
  bannerId: number;
  tenantId: string;
  message: string;
  severity: number;
  isAnimated: boolean;
  marqueeSpeed: number; // 1-10, scroll speed when animated
  isActive: boolean;
  startsAt: string | null; // ISO; null = show immediately
  endsAt: string | null; // ISO; null = no auto-expire
  facilityId: number | null; // null = all facilities (v1 always null)
  isDismissible: boolean;
  createdByUserId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface BannerCreateRequest {
  message: string;
  severity: number;
  isAnimated: boolean;
  isActive: boolean;
  marqueeSpeed?: number; // 1-10; defaults to 5
  startsAt?: string | null;
  endsAt?: string | null;
}

export interface BannerUpdateRequest {
  message: string;
  severity: number;
  isAnimated: boolean;
  marqueeSpeed?: number; // 1-10; defaults to 5
  startsAt?: string | null;
  endsAt?: string | null;
}

// ---------------------------------------------------------------------------
// Billing (Phase 2)
// ---------------------------------------------------------------------------

export interface CptCode {
  year: number;
  code: string;
  description: string;
  workRvu: number;
  notes: string | null;
  isActive: boolean;
  importedFromImportId: number | null;
  createdAt: string;
  updatedAt: string;
}

export interface CptImportError {
  row: number;
  cpt: string | null;
  message: string;
}

export interface CptImport {
  importId: number;
  fileName: string;
  sheetName: string;
  year: number;
  parsedRows: number;
  insertedRows: number;
  updatedRows: number;
  skippedRows: number;
  errors: CptImportError[];
  ranByUserId: string;
  ranAt: string;
}

// ---------------------------------------------------------------------------
// CMS RVU source-of-truth (billing.rvu_values) + manual overrides — item 1.2
// Mirror of RadiologyPlus.Core/Billing/BillingModels.cs. `quarter` is a single
// char ("A"|"B"|"C"|"D"); date/decimal/timestamps serialize as string/number/string.
// ---------------------------------------------------------------------------

export type RvuQuarter = "A" | "B" | "C" | "D";

export interface RvuValue {
  year: number;
  quarter: RvuQuarter;
  hcpcs: string;
  modifier: string; // "" global · "26" professional · "TC" technical
  description: string | null;
  workRvu: number;
  peRvuNonFac: number | null;
  peRvuFac: number | null;
  mpRvu: number | null;
  totalNonFac: number | null;
  totalFac: number | null;
  statusCode: string | null; // CMS status indicator: "A" active, etc.
  globalDays: string | null;
  effectiveFrom: string | null; // yyyy-MM-dd
  sourceImportId: number | null;
  createdAt: string;
  updatedAt: string;
}

export interface RvuImport {
  importId: number;
  fileName: string;
  year: number;
  quarter: RvuQuarter;
  parsedRows: number;
  insertedRows: number;
  updatedRows: number;
  skippedRows: number;
  errors: CptImportError[];
  ranByUserId: string;
  ranAt: string;
}

// ── M*Modal RVU write-back (project-ffi-rvu-writeback) ──────────────────────

// One code's diff vs what M*Modal currently stores. action: "update" (RVU differs —
// will be written) | "unchanged" (already equal) | "missing" (no active M*Modal row).
export interface RvuSyncDiff {
  hcpcs: string;
  currentRvu: number | null;
  newRvu: number;
  matchedRows: number;
  action: "update" | "unchanged" | "missing";
}

export interface RvuSyncPreview {
  configured: boolean;
  year: number;
  quarter: RvuQuarter;
  total: number;
  updatable: number;
  unchanged: number;
  missing: number;
  diffs: RvuSyncDiff[];
}

export interface RvuSyncResult {
  configured: boolean;
  year: number;
  quarter: RvuQuarter;
  matched: number;
  updated: number;
  unchanged: number;
  missing: number;
  success: boolean;
  error: string | null;
  ranAt: string;
}

export interface RvuSyncRun {
  syncRunId: number;
  year: number;
  quarter: RvuQuarter;
  dryRun: boolean;
  matchedRows: number;
  updatedRows: number;
  unchangedRows: number;
  missingRows: number;
  success: boolean;
  errorMessage: string | null;
  ranByUserId: string;
  ranAt: string;
}

export interface RvuSyncStatus {
  configured: boolean;
  lastRun: RvuSyncRun | null;
}

export interface RvuOverride {
  overrideId: number;
  year: number;
  code: string; // single HCPCS or ";"-delimited bundle (backend-canonicalized for bundles)
  facilityId: number | null; // vestigial (always null); superseded by siteCode
  siteCode: string | null; // null = tenant-wide; set = site-specific (raw Novarad site_code)
  overrideWorkRvu: number;
  note: string | null;
  createdByUserId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface RvuOverrideRequest {
  year: number;
  overrideWorkRvu: number;
  note?: string | null;
  siteCode?: string | null; // null/omitted = tenant-wide; set = site-specific
}

// Verdict comparing a CPT-master row to CMS. Singles: matches | differs |
// not_in_cms | status_gated. Bundles: matches_sum | differs_sum | partial.
export type CmsCheckVerdict =
  | "matches"
  | "differs"
  | "not_in_cms"
  | "status_gated"
  | "matches_sum"
  | "differs_sum"
  | "partial";

export interface CptMasterCmsRow {
  year: number;
  code: string;
  isBundle: boolean;
  description: string;
  masterWorkRvu: number; // the CPT master / imported sheet's work RVU
  cmsWorkRvu: number | null; // single: CMS work RVU · bundle: sum of components
  cmsStatus: string | null; // single only
  bundleParts: number | null; // bundle only
  bundleMatched: number | null; // bundle only
  overrideWorkRvu: number | null;
  effectiveWorkRvu: number; // what reconciliation actually credits
  verdict: CmsCheckVerdict;
}

// Reconciliation — mirror of RadiologyPlus.Core/Billing/IBillingRepository.cs.
// `cptCode` is a bundle string (e.g. "70492;71270;74178") when the line credits
// a bundle row; otherwise a single CPT.

export interface ReconciliationLineItem {
  lineId: number;
  novaradPhysicianId: number;
  physicianDisplayName: string;
  siteCode: string;
  facilityId: number | null;
  cptCode: string;
  cptDescription: string | null;
  reportCount: number;
  units: number;
  workRvuPerUnit: number;
  workRvuTotal: number;
  novaradRvuWork: number | null;
  rvuMismatch: boolean;
  novaradReportIds: number[];
  // STAT subset of novaradReportIds — drives the per-radiologist STAT subtotal and
  // the STAT badge in the drill-down. Older runs return [] (additive run-time snapshot).
  novaradStatReportIds: number[];
}

export interface ReconciliationNote {
  kind: string;
  message: string;
}

// Per-facility rollup for a run: distinct credited reports at that site and how
// many were flagged STAT in Novarad. Subtotals reconcile to the run totals.
export interface ReconciliationFacilitySummary {
  facilityId: number | null;
  siteCode: string;
  totalReports: number;
  statReportCount: number;
}

export interface ReconciliationRun {
  runId: number;
  periodStart: string;
  periodEnd: string;
  facilityId: number | null;
  runKind: number;
  totalReports: number;
  totalRadiologists: number;
  totalWorkRvu: number;
  statReportCount: number;
  lineItems: ReconciliationLineItem[];
  notes: ReconciliationNote[];
  facilitySummaries: ReconciliationFacilitySummary[];
  generatedByUserId: string;
  generatedAt: string;
}

export interface RunReconciliationRequest {
  from: string;
  to: string;
  site?: string | null;
  facilityId?: number | null;
}

// One per-report detail row backing a reconciliation line (drill-down).
// Patient/study fields are nullable because the orders→studies accession join
// can miss when the study row was never imaged or was soft-deleted.
export interface ReconciliationDetailRow {
  reportId: number;
  signedAt: string;
  orderId: number;
  siteCode: string;
  accession: string | null;
  studyUid: string | null;
  studyDate: string | null;
  modality: string | null;
  novaradPatientId: number | null;
  patientPid: string | null;
  patientLastName: string | null;
  patientFirstName: string | null;
  patientBirthDate: string | null;
  patientGender: string | null;
}

export interface ReconciliationLineDetailResponse {
  runId: number;
  physicianId: number;
  cptCode: string;
  siteCode: string;
  reportCount: number;
  rows: ReconciliationDetailRow[];
}

export interface UnmappedFacilityBreakdown {
  siteCode: string;
  facilityId: number | null;
  reportCount: number;
  serviceLineCount: number;
}

export interface UnmappedServiceCode {
  code: string;
  year: number;
  kind: string; // "non_cpt_service_code" | "cpt_missing_from_master"
  description: string | null;
  reportCount: number;
  serviceLineCount: number;
  looksLikeCpt: boolean;
  facilities: UnmappedFacilityBreakdown[];
  suggestedCpt: string | null;
  suggestedCptDescription: string | null;
  suggestedWorkRvu: number | null;
  suggestionHitKind: string | null; // "exact_code" | "description"
}

export interface UnmappedCodesResponse {
  from: string;
  to: string;
  site: string | null;
  totalCodes: number;
  totalReportsUncredited: number;
  codes: UnmappedServiceCode[];
}

// ============================================================================
// Phase 2 — service_code → CPT crosswalk
// ============================================================================

export type CrosswalkStatus = 1 | 2; // 1=approved, 2=suppressed
export type CrosswalkSource = 1 | 2 | 3; // 1=manual, 2=suggested, 3=bulk

export interface ServiceCodeMapping {
  crosswalkId: number;
  serviceCode: string;
  cptCode: string;
  status: CrosswalkStatus;
  siteCode: string | null;     // null = tenant-wide default; set = site-specific (raw Novarad site_code)
  source: CrosswalkSource;
  note: string | null;
  approvedForDescription: string | null;
  appliedCount: number;
  lastUsedAt: string | null;
  createdByUserId: string;
  createdByDisplayName: string | null;
  updatedByUserId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CrosswalkListResponse {
  totalCodes: number;
  rows: ServiceCodeMapping[];
}

export interface CrosswalkUpsertRequest {
  serviceCode: string;
  cptCode: string;
  source?: CrosswalkSource;
  status?: CrosswalkStatus;
  note?: string | null;
  approvedForDescription?: string | null;
  siteCode?: string | null; // null = tenant-wide default; set = site-specific
}

export interface CrosswalkSuggestion {
  cptCode: string;
  description: string;
  workRvu: number;
  score: number;       // 0..1 — 1.0 for exact-code, similarity() for description hits
  hitKind: "exact_code" | "description";
}

export interface CrosswalkSuggestionsResponse {
  serviceCode: string;
  suppressed: boolean;
  existing?: ServiceCodeMapping | null;
  candidates: CrosswalkSuggestion[];
}

export interface BulkImportRow {
  serviceCode: string;
  cptCode: string;
  note?: string | null;
  siteCode?: string | null; // null = tenant-wide default; set = site-specific
}

export interface BulkImportRowResult {
  serviceCode: string;
  outcome: "inserted" | "updated" | "skipped" | "error";
  error: string | null;
  siteCode: string | null;
}

export interface BulkImportResult {
  inserted: number;
  updated: number;
  skipped: number;
  errors: number;
  rows: BulkImportRowResult[];
}

export interface CrosswalkBulkImportRequest {
  rows: BulkImportRow[];
  onConflict?: "skip" | "update";
}

export interface AuthUser {
  userId: string;
  username: string;
  displayName: string | null;
  email: string | null;
  role: Role;
  facilityIds: number[];
}

export interface AuthResponse {
  accessToken: string;
  refreshToken: string;
  expiresInSeconds: number;
  user: AuthUser;
}

export interface LoginRequest {
  facility: string;
  username: string;
  password: string;
}

export interface ReadyStudy {
  novaradStudyId: number;
  facilityId: number;
  studyUid: string;
  accession: string | null;
  studyDate: string | null;
  modality: string | null;
  custom3: string | null;
  studyDescription: string | null;
  novaradPatientId: number;
  patientPid: string | null;
  patientLastName: string | null;
  patientFirstName: string | null;
  patientBirthDate: string | null;
  patientGender: string | null;
  lastImageProcessedDate: string | null;
  projectedAt: string;
  inProgressValidationId: string | null;
  inProgressStatus: number | null;
  inProgressStartedBy: string | null;
  inProgressStartedByDisplay: string | null;
  inProgressStartedAt: string | null;
}

export const ValidationStatus = {
  Open: 1,
  InProgress: 2,
  Submitted: 3,
  Completed: 4,
  Failed: 5,
  Cancelled: 6,
} as const;

// What the tech chose to do about the study's patient on step 1.
export const PatientAction = {
  None: 0,
  EditInPlace: 1,
  Reassign: 2,
} as const;

export interface PatientCorrection {
  lastName: string | null;
  firstName: string | null;
  middleName: string | null;
  birthDate: string | null; // ISO yyyy-MM-dd
  gender: string | null;
}

export interface ValidationRecord {
  validationId: string;
  tenantId: string;
  novaradStudyId: number;
  novaradOrderId: number | null;
  novaradPatientId: number | null;
  currentStep: number;
  status: number;
  reason: string | null;
  techNotes: string | null;
  referringPhysicianId: number | null;
  comparisonStudyIds: number[];
  createOrderRequested: boolean;
  startedByUserId: string;
  startedAt: string;
  completedAt: string | null;
  patientAction: number;
  correction: PatientCorrection | null;
  reassignTargetPatientId: number | null;
}

export interface PatientSearchResult {
  novaradPatientId: number;
  pid: string | null;
  lastName: string | null;
  firstName: string | null;
  middleName: string | null;
  birthDate: string | null;
  gender: string | null;
  recentStudyDate: string | null;
}

export interface StartValidationRequest {
  novaradStudyId: number;
  novaradPatientId: number;
}

export interface StepRequest {
  novaradOrderId?: number | null;
  novaradPatientId?: number | null;
  reason?: string | null;
  techNotes?: string | null;
  referringPhysicianId?: number | null;
  comparisonStudyIds?: number[] | null;
  createOrderRequested?: boolean | null;
  patientAction?: number | null;
  patientCorrection?: {
    lastName?: string | null;
    firstName?: string | null;
    middleName?: string | null;
    birthDate?: string | null;
    gender?: string | null;
  } | null;
  reassignTargetPatientId?: number | null;
}

export interface CandidateOrder {
  orderId: number;
  patientId: string;
  accessionNumber: string;
  status: string;
  description: string | null;
  physicianReason: string | null;
  notes: string | null;
  referringPhysicianId: number | null;
  creationDate: string | null;
}

export interface ReportContent {
  reportId: number;
  signedAt: string | null;
  signingPhysicianId: number | null;
  signingPhysicianName: string | null;
  accession: string | null;
  studyDate: string | null;
  modality: string | null;
  novaradPatientId: number | null;
  patientPid: string | null;
  patientLastName: string | null;
  patientFirstName: string | null;
  patientBirthDate: string | null;
  patientGender: string | null;
  reportFormat: string | null;
  reportText: string | null;
}

export interface PatientJacketEntry {
  novaradStudyId: number;
  studyUid: string;
  accession: string | null;
  studyDate: string | null;
  modality: string | null;
  description: string | null;
  score: number;
  suggested: boolean;
}

export interface ComparisonCandidate {
  novaradStudyId: number;
  studyUid: string;
  accession: string | null;
  studyDate: string | null;
  modality: string | null;
  novaradPatientId: number;
  patientLastName: string | null;
  patientFirstName: string | null;
  patientBirthDate: string | null;
}

export interface TechNotesTemplate {
  templateId: number;
  label: string;
  body: string;
  sortOrder: number;
}

export interface TemplateUpsertRequest {
  label: string;
  body: string;
  sortOrder?: number | null;
}

// DICOM-level study merge (re-point pacs.series.study + soft-delete losing pacs.studies).
export interface StudyMergeRequest {
  winningStudyId: number;
  losingStudyIds: number[];
  reason?: string | null;
}

export interface StudyMergeRowResult {
  losingStudyId: number;
  success: boolean;
  seriesRePointed: number;
  errorMessage: string | null;
}

export interface StudyMergeOutcome {
  winningStudyId: number;
  mergedCount: number;
  failedCount: number;
  rows: StudyMergeRowResult[];
  preflightError: string | null;
}

export interface DoTheDoOutcome {
  validationId: string;
  success: boolean;
  completedSteps: number;
  totalSteps: number;
  failureMessage: string | null;
}

export const DoTheDoRunStatus = {
  Started: 1,
  Succeeded: 2,
  Failed: 3,
} as const;

export interface DoTheDoProgressEvent {
  validationId: string;
  stepIndex: number;
  stepCount: number;
  stepKey: string;
  description: string;
  status: number;
  errorMessage: string | null;
}
