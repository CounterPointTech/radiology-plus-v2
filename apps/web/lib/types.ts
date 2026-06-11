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
}

export interface ReconciliationNote {
  kind: string;
  message: string;
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
  lineItems: ReconciliationLineItem[];
  notes: ReconciliationNote[];
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

export interface UnmappedServiceCode {
  code: string;
  year: number;
  kind: string; // "non_cpt_service_code" | "cpt_missing_from_master"
  description: string | null;
  reportCount: number;
  serviceLineCount: number;
  looksLikeCpt: boolean;
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
}

export interface BulkImportRowResult {
  serviceCode: string;
  outcome: "inserted" | "updated" | "skipped" | "error";
  error: string | null;
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
