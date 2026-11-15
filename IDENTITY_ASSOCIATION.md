# Verified worker identity association

How BunonBrain connects a camera's **temporary track** to a **permanent
worker**, only through independently verified evidence. The backend decides;
the computer-vision service never names anyone, and nothing is based on how
a person looks.

| Identifier | Meaning |
|---|---|
| Worker ID | The permanent employee identity (`worker_profiles`) |
| RFID card | A registered card of one worker (`worker_cards`, with issue / deactivation history) |
| Reader (device) | An RFID reader: a device of category `RfidReader` |
| Camera ID | A camera |
| Track ID | A temporary, camera-local number the tracker gives a moving person. **Never a worker ID.** The same person gets different track ids on different cameras and after a restart. |
| Workstation ID | A production workstation, to which workers are assigned |

No face recognition, facial embeddings, biometrics, clothing or appearance
matching exists anywhere in the platform.

## 1. Architecture

```text
worker ── active RFID card ── read at a reader ── reader mapping (WORKSTATION / ZONE precision)
                                                        │
                       worker's active assignment ──────┤  same workstation?
                                                        │
          camera zone(s) of that workstation ───────────┤
                                                        │
  CV service: ZONE_ENTERED / PERSON_LOST (monitoring events, track ids only)
                                                        ▼
                         IdentityEvidenceEvaluator  (facts as of the read time)
                                                        ▼
                         AssociationRuleEngine      (pure rules, unit-tested)
                                                        ▼
         VERIFIED / AMBIGUOUS / MANUAL_REVIEW / PENDING / UNVERIFIED  → worker_track_associations
                         + evidence (append-only) + history (append-only)
                                                        ▼
            SignalR "identityAssociation" → Monitoring room · Identity page · camera view
```

Backend components (`BunonBrain.Infrastructure/Identity/`):

| Component | Role |
|---|---|
| `RfidEventProcessor` | Validates and stores RFID reads (readers, integrations, simulator) |
| `TrackObservationProcessor` | Reacts to camera track events: a zone entry re-evaluates waiting reads; a lost track ends its identity |
| `IdentityEvidenceEvaluator` | Gathers the facts for a read, as they were at that moment |
| `AssociationRuleEngine` (`Application/Identity/IdentityRules.cs`) | Decides, from the facts only |
| `IdentityAssociationService` | Applies decisions transactionally, maintains validity, answers queries |
| `AssociationReviewService` | Manual decisions |
| `RfidReaderService` | Reader location mappings |
| `RfidSimulatorService` | Development scenarios through the same pipeline |
| `IdentityAssociationWorker` | Every 30 s: closes pending reads, expires associations |

**Reused, not duplicated:** workers, cards and their history, devices (readers
and their health), workstation assignments and transfer history, cameras,
coverage and zones, monitoring events (the CV track observations), the audit
log, RBAC and the monitoring SignalR hub. **New tables:**
`rfid_reader_mappings`, `rfid_read_events`, `worker_track_associations`,
`identity_association_evidence`, `identity_association_history` (migration
`AddIdentityAssociation`).

## 2. RFID read contract

```http
POST /api/identity/rfid-reads
Authorization: Bearer <token with devices.manage>   (a reader gateway's integration account)
```

```json
{
  "factoryId": "3f1c…",
  "deviceId": "7a9e…",            // or "deviceCode": "RF-L1-T025"
  "cardUid": "04:A1:B2:C3:D4",     // separators ignored; never stored or returned
  "occurredAt": "2026-11-15T08:00:02Z",
  "idempotencyKey": "rf-l1-t025/2026-11-15T08:00:02Z/04A1B2C3D4"
}
```

| Check | Result |
|---|---|
| Reader exists in the factory and is an `RfidReader` | 404 `reader_not_found` / 400 `device_not_reader` |
| Real reader via the API, simulated reader via the simulator only | 409 `device_is_simulated` / `device_not_simulated` |
| Reader `Active` (not Maintenance / Disabled) | 409 `reader_not_active` |
| Time at most 2 min ahead, 7 days back | 400 `occurred_in_future` / `occurred_too_old` |
| Idempotency key repeated | 200 `Duplicate` with the first result |
| Card UID format (4–64 letters/digits) | 400 `card_uid_invalid` |
| Card registered + active, worker active, same factory | otherwise stored **REJECTED** (`card_unknown`, `card_inactive`, `card_unassigned`, `worker_not_active`), never evaluated |

Response `201`: `{ outcome: "Accepted" | "Rejected", read, associations }`.
The read shows the card as `…C3D4` (last four characters) and its printed
label; the database keeps only the card id, a SHA-256 hash and those four
characters. Cards are managed where they always were: **Workers → worker →
Cards** (issue, deactivate as Returned / Lost / Damaged / Replaced; a returned
card can later be issued to someone else, history kept).

## 3. Reader and camera mapping

```http
PUT /api/identity/readers/{deviceId}/mapping       (devices.manage, factory level)
{ "precision": "WORKSTATION", "workstationId": "…", "location": "under T-025" }
```

| Precision | Needs | Can place a worker at a workstation? |
|---|---|---|
| `WORKSTATION` | `workstationId` | **Yes** |
| `ZONE` | `cameraZoneId` | Yes, if the zone belongs to a workstation |
| `LINE` | `lineId` | No |
| `FLOOR` | `floorId` | No |
| `AREA` | `location` or `departmentId` | No |

A new mapping closes the current one (history: `GET …/mappings`); each read
is judged by the mapping in force when it happened. The cameras that matter
are those with an **active camera zone bound to the workstation** (Cameras &
devices → camera → zones → zone type Workstation, linked workstation). A
reader may read several cards in the same area; that is handled as ambiguity,
not assumed away.

## 4. Association rules

A read becomes **VERIFIED** for a track only when **all** hold:

1. accepted read of an active card of an active worker;
2. reader mapped at `WORKSTATION` (or `ZONE` of a workstation) precision at that time;
3. the worker actively assigned to that workstation at that time;
4. no other worker's card read at that location within the window, and (by
   default) no other worker assigned to that workstation;
5. an active camera zone of that workstation;
6. **exactly one** track of the same source (REAL with REAL, SIMULATED with
   SIMULATED) present in that zone between `WindowBeforeSeconds` before and
   `WindowAfterSeconds` after the read (from its `ZONE_ENTERED` to its
   `ZONE_EXITED` / `PERSON_LOST`);
7. no contradiction: the track not verified as someone else, the worker not
   verified on another track of that camera, the worker not verified at
   another workstation.

| Outcome | When |
|---|---|
| `VERIFIED` | All of the above (method `RFID_WORKSTATION`). A new read of the same worker on the same track **refreshes** it. |
| `AMBIGUOUS` | Several tracks in the zone (`MULTIPLE_TRACKS_IN_ZONE`), several workers' cards at the reader (`MULTIPLE_WORKERS_AT_READER`), or a shared workstation (`SHARED_WORKSTATION`). One case per candidate track, with candidate workers. Manual review required. |
| `MANUAL_REVIEW` | A contradiction (`TRACK_VERIFIED_AS_OTHER`, `WORKER_ON_OTHER_TRACK`, `CONFLICTING_LOCATION`). The contradicted VERIFIED association is **suspended** to MANUAL_REVIEW too. |
| `PENDING` | No track yet; the read waits for the window (a later `ZONE_ENTERED` re-evaluates it). |
| `UNVERIFIED` | Not enough evidence: no mapping, a broad mapping, no / another assignment, no workstation zone, no track in the window. No association is created. |
| `EXPIRED` | The track was lost (`TRACK_LOST`), the assignment ended (transfer: `ASSIGNMENT_ENDED`), or the validity ran out (`VALIDITY_ELAPSED`). |
| `REVOKED` | A reviewer revoked it. |

There are **no confidence scores or thresholds** in this engine: weaker
evidence never becomes VERIFIED. Position alone never picks between tracks,
an assignment is context and never visual proof, a broad reader is never
workstation proof, and two cameras seeing the same zone are each judged on
their own track (no cross-camera identity is inferred).

**Validity.** An association is valid until the earliest of: its track's
`PERSON_LOST`, its assignment ending, or `ValidityMinutes` after the read
(default 240). A Worker ID is shown only while it is VERIFIED and valid
(checked at query time too, so a transfer hides it immediately).

**Database guarantees.** Partial unique indexes allow one VERIFIED
association per (camera, track) and one per (worker, camera): two simultaneous
requests can never verify conflicting identities. Evaluations of a factory are
also serialised with an advisory lock; the loser of a race re-evaluates with
the winner as evidence. Evidence and history are append-only (triggers).

**Configuration** (`appsettings.json` section `Identity`, or env `Identity__…`):

| Setting | Default | |
|---|---|---|
| `WindowBeforeSeconds` | 120 | A track in the zone this long before the read counts |
| `WindowAfterSeconds` | 120 | …or this long after it (tap, then sit) |
| `ValidityMinutes` | 240 | Longest validity without a new read |
| `AllowSharedWorkstation` | false | Verify at workstations with several assigned workers |
| `RecentTrackMinutes` | 5 | "Currently visible" tracks for the dashboard |

## 5. Manual review

**Monitoring → Identity → Review queue** (or a track's card on the camera
view). The case shows its evidence (supports / contradicts / context), the
RFID read (card masked), the candidate workers with their assignments, the
candidate tracks and the full history.

```http
POST /api/identity/associations/{id}/review       (monitoring.events.review, in scope)
{ "decision": "CONFIRM", "workerId": "…", "reason": "Confirmed in person by the line supervisor", "expectedVersion": 3 }
```

| Status | Allowed decisions |
|---|---|
| AMBIGUOUS | CONFIRM, REJECT |
| MANUAL_REVIEW, PENDING | CONFIRM, REJECT, AMBIGUOUS |
| VERIFIED | REVOKE, AMBIGUOUS |
| UNVERIFIED, EXPIRED, REVOKED | none (closed; a new read starts a new case) |

A CONFIRM must name a **candidate** worker (one the evidence points to),
needs the track still visible, and still obeys the one-to-one rules (409
`identity_conflict`). Every decision needs a reason (≥ 5 characters), checks
the version (409 `association_changed`), and records reviewer, time,
decision, reason, previous and new status in the association's history and
the audit log. Earlier decisions are never overwritten. Reviewers never use
face recognition: they check in person or with the supervisor.

## 6. API

| Method | Path | Permission |
|---|---|---|
| POST | `/api/identity/rfid-reads` | `devices.manage` |
| GET | `/api/identity/reads?factoryId&deviceId&page&pageSize` | `monitoring.view` or `devices.view` |
| GET | `/api/identity/readers?factoryId` | `devices.view` or `monitoring.view` |
| PUT | `/api/identity/readers/{deviceId}/mapping` | `devices.manage` (factory level) |
| GET | `/api/identity/readers/{deviceId}/mappings` | `devices.view` or `monitoring.view` |
| GET | `/api/identity/overview?factoryId&lineId` | `monitoring.view` |
| GET | `/api/identity/tracks?cameraId` | `monitoring.view` |
| GET | `/api/identity/associations?factoryId&cameraId&trackId&workerId&status&lineId&reviewOnly&from&to&page&pageSize` | `monitoring.view` |
| GET | `/api/identity/associations/{id}` | `monitoring.view` (candidates, read details and decisions only with `monitoring.events.review`) |
| POST | `/api/identity/associations/{id}/review` | `monitoring.events.review` |
| POST | `/api/identity/simulator` | `devices.simulate` |

SignalR (`/hubs/monitoring`): `identityAssociation` `{ change, association }`
(change `created`, `refreshed`, `suspended`, `expired`, `reviewed`), sent only to
the VISION groups of the association's factory and line, i.e. people with
`monitoring.view` in that scope.

**Roles** (existing permissions, no new ones): Super Admin and Factory Admin:
everything in scope; Factory Manager and Monitoring Operator: view and
review; Production Supervisor: their lines; IT / Device Administrator:
readers, mappings, reads and the simulator; HR: worker cards (Workers module);
Workers: nothing of other people's identity.

## 7. RFID simulator (development)

**Monitoring → Identity → Simulator** (`devices.simulate`, simulated readers
only). Scenarios: card read, duplicate read (same idempotency key twice), two
workers' cards at one reader, unregistered card, deactivated card, the same
card at two readers (conflicting location). Every read goes through the same
ingestion and identity pipeline as a real reader and is stored **SIMULATED**.

A SIMULATED read only combines with SIMULATED track observations, so it can
never verify a real camera track. The option *"first send a SIMULATED track
into the reader's workstation zone"* triggers a SIMULATED `ZONE_ENTERED`
through the monitoring simulator; nothing ever writes an association directly.

### Walkthrough

1. **Workers** → register a worker → *Cards* → issue a card.
2. **Workstations / assignments** → assign the worker to a workstation.
3. **Cameras & devices** → a camera covering the line, with a zone of type
   *Workstation* linked to that workstation; register an RFID reader device
   (real, or simulated for the demo).
4. **Monitoring → Identity → RFID readers** → *Map* → precision *One
   workstation* → that workstation.
5. A track enters the zone: the computer-vision service sends `ZONE_ENTERED`
   (or, for the demo, the simulator's option above).
6. Send a read: the reader / integration posts `/api/identity/rfid-reads`
   (or the simulator's *Card read*).
7. The backend evaluates: **VERIFIED** only if everything agrees; otherwise
   AMBIGUOUS / UNVERIFIED with the reason.
8. Review ambiguous cases in the *Review queue*.
9. The monitoring room, the Identity page and the camera view show
   `Track ID · Identity · Worker · Workstation` (worker only when verified).

## 8. Security and privacy

* No face recognition, biometrics, embeddings, appearance or clothing
  matching; no permanent visual identity profiles. The CV service is refused
  any worker id (`cv_cannot_identify`).
* Raw card UIDs are never stored with reads or returned by identity APIs or
  logs (hash + last four characters). Card management stays with HR (`workers.manage`).
* Identity views need `monitoring.view` and respect factory / line scope in
  the API and in SignalR groups; candidate workers and read details only for
  reviewers.
* Evidence and decisions are append-only and audited; associations expire;
  rules are configurable and documented here.
* Identity association is for operational context. It is never used for
  automatic discipline, pay or performance decisions.

## 9. Physical RFID readers (later)

A reader (or its ESP32 / industrial gateway) registers as a device of
category `RfidReader` (real), sends heartbeats through the device API (health,
online / offline), and posts each card read to `/api/identity/rfid-reads` with
an integration account holding `devices.manage`, a stable idempotency key
(e.g. reader code + timestamp + UID) and the reader's UTC time. An MQTT bridge
can map `bunonbrain/{factory}/devices/{reader}/rfid` onto the same request.
Mount readers so each one serves one workstation (map them at WORKSTATION
precision); readers at gates or on floors remain useful for attendance but
can't verify a track.

## 10. Limitations

* Verification needs a workstation-level reader, an assignment and a camera
  zone per workstation; without them identities stay UNVERIFIED (by design).
* A track that moves to another workstation keeps its identity until it is
  lost or the validity ends; a person walking between cameras is a new,
  unverified track on each camera.
* A second track entering the zone *after* a read has already been verified
  does not reopen that verification.
* Track id switches in ByteTrack (crowding) end the identity with the old
  track; the new track needs a new read or a review.
* The database integration tests need Docker (Testcontainers); see the test
  section of the README.
