// Verified worker identity association: types mirroring the API and the
// labels every identity view uses. A Worker ID appears only for a VERIFIED,
// currently valid association; raw RFID card UIDs never reach the browser
// (cards are shown as "…C3D4").

import type { BadgeTone } from "@/components/ui/Badge";
import type { MonLine, MonRef, MonZone } from "@/lib/monitoring";

export type IdentityStatus = "PENDING" | "VERIFIED" | "AMBIGUOUS" | "UNVERIFIED" | "EXPIRED" | "REVOKED" | "MANUAL_REVIEW";

export type IdentityAssociation = {
  id: string;
  code: string;
  factoryId: string;
  factoryCode: string;
  camera: MonRef;
  trackId: string;
  status: IdentityStatus;
  isCurrent: boolean;
  worker?: MonRef | null;
  workstation?: MonRef | null;
  zone?: MonZone | null;
  line?: MonLine | null;
  verificationMethod?: string | null;
  reasonCode: string;
  reasonText: string;
  summary: string;
  isSimulated: boolean;
  reviewRequired: boolean;
  candidateCount: number;
  startAt: string;
  endAt?: string | null;
  expiresAt: string;
  updatedAt: string;
  version: number;
};

export type IdentityEvidence = { id: number; kind: string; effect: "SUPPORTS" | "CONTRADICTS" | "CONTEXT"; refType?: string | null; refId?: string | null; detail: string; observedAt?: string | null };
export type IdentityHistory = { id: number; action: string; fromStatus?: string | null; toStatus: string; worker?: MonRef | null; reason: string; isManual: boolean; actorEmail: string; actorRole?: string | null; at: string };
export type CandidateWorker = { worker: MonRef; workstation?: string | null; assignedSince?: string | null };

export type RfidRead = {
  id: number;
  factoryId: string;
  deviceId: string;
  readerCode: string;
  card: string;
  cardLabel?: string | null;
  worker?: MonRef | null;
  source: string;
  isSimulated: boolean;
  occurredAt: string;
  receivedAt: string;
  outcome: "ACCEPTED" | "REJECTED";
  outcomeCode?: string | null;
  evaluation?: string | null;
  evaluationCode?: string | null;
  evaluationText?: string | null;
  evaluatedAt?: string | null;
};

export type IdentityDetail = {
  association: IdentityAssociation;
  evidence: IdentityEvidence[];
  history: IdentityHistory[];
  read?: RfidRead | null;
  candidates: CandidateWorker[];
  candidateTracks: string[];
  trackActive: boolean;
  trackLastSeenAt?: string | null;
  canReview: boolean;
  allowedDecisions: string[];
};

export type TrackIdentity = {
  cameraId: string;
  cameraCode: string;
  trackId: string;
  lastSeenAt: string;
  zone?: MonZone | null;
  isSimulated: boolean;
  identity: IdentityStatus;
  worker?: MonRef | null;
  workstation?: MonRef | null;
  associationId?: string | null;
  associationCode?: string | null;
  action: string;
};

export type ReaderMapping = {
  id: string;
  deviceId: string;
  precision: string;
  workstationLevel: boolean;
  departmentId?: string | null;
  departmentName?: string | null;
  floorId?: string | null;
  floorName?: string | null;
  line?: MonLine | null;
  workstation?: MonRef | null;
  cameraZoneId?: string | null;
  zoneName?: string | null;
  zoneCameraCode?: string | null;
  location?: string | null;
  effectiveFrom: string;
  effectiveTo?: string | null;
  createdByEmail: string;
};

export type RfidReader = {
  deviceId: string;
  code: string;
  name: string;
  factoryId: string;
  factoryCode: string;
  isSimulated: boolean;
  adminStatus: string;
  health: string;
  lastSeenAt?: string | null;
  mapping?: ReaderMapping | null;
  workstationZones: MonZone[];
  cameraCodes: string[];
  readsLastHour: number;
  lastReadAt?: string | null;
  canConfigure: boolean;
};

export type AssociationChange = {
  id: number;
  associationId: string;
  associationCode: string;
  cameraCode: string;
  trackId: string;
  action: string;
  fromStatus?: string | null;
  toStatus: string;
  reason: string;
  isManual: boolean;
  actorEmail: string;
  at: string;
  isSimulated: boolean;
};

export type IdentityOverview = {
  asOf: string;
  counts: { verifiedNow: number; unverifiedTracks: number; ambiguousTracks: number; pendingReview: number; pendingReads: number; readersOnline: number; readers: number };
  tracks: TrackIdentity[];
  reviewQueue: IdentityAssociation[];
  recentChanges: AssociationChange[];
  readers: RfidReader[];
  recentReads: RfidRead[];
  note: string;
};

export type RfidReadResult = { outcome: string; read: RfidRead; associations: IdentityAssociation[] };
export type RfidSimulationResult = { scenario: string; reads: RfidReadResult[]; notes: string[] };

export const IDENTITY_TONE: Record<string, BadgeTone> = {
  VERIFIED: "success",
  AMBIGUOUS: "warning",
  MANUAL_REVIEW: "warning",
  PENDING: "info",
  UNVERIFIED: "neutral",
  EXPIRED: "neutral",
  REVOKED: "danger"
};

export const IDENTITY_LABEL: Record<string, string> = {
  VERIFIED: "Verified",
  AMBIGUOUS: "Ambiguous",
  MANUAL_REVIEW: "Manual review",
  PENDING: "Pending",
  UNVERIFIED: "Unverified",
  EXPIRED: "Expired",
  REVOKED: "Revoked"
};

export const PRECISIONS = [
  { id: "WORKSTATION", label: "One workstation", hint: "Can place a worker at that workstation." },
  { id: "ZONE", label: "One camera zone", hint: "Places a worker only if the zone belongs to a workstation." },
  { id: "LINE", label: "A production line", hint: "Never proof of a workstation." },
  { id: "FLOOR", label: "A production floor", hint: "Never proof of a workstation." },
  { id: "AREA", label: "Another area", hint: "Never proof of a workstation." }
] as const;

export const SIM_SCENARIOS = [
  { id: "READ", label: "Card read", needs: ["worker"] },
  { id: "DUPLICATE", label: "Duplicate read (same idempotency key twice)", needs: ["worker"] },
  { id: "MULTIPLE", label: "Two workers' cards at one reader", needs: ["worker", "second"] },
  { id: "INVALID", label: "Unregistered card", needs: [] },
  { id: "INACTIVE", label: "Deactivated card", needs: ["worker"] },
  { id: "CONFLICT", label: "Same card at two readers (conflicting location)", needs: ["worker", "other"] }
] as const;

export const IDENTITY_DISCLAIMER =
  "A Worker ID is shown only when the identity is VERIFIED and still valid: an active RFID card read at a reader mapped to the worker's assigned workstation, with exactly one camera track in that workstation's zone (or a reviewer's confirmation of such evidence). Track ids are temporary camera numbers. No face, appearance or biometric data is used, and identity is never a basis for automatic discipline or pay.";
