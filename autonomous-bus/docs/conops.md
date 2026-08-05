# Concept of Operations (ConOps)

Status: DRAFT — to be developed jointly with Qbuzz and in dialogue with RDW.

## 1. Purpose

Describe how the autonomous 8-passenger shuttle will operate, to support the
RDW approval process (individual approval / Prototyperegeling or successor
exemption route — confirm current mechanism with RDW before finalizing).

## 2. Vehicle

- Base vehicle: [Qbuzz-provided 8-person bus — model/spec TBD]
- Vehicle category for type approval: TBD — clarify with RDW early. Prior
  precedent (WEpod) was classified M1 despite functioning as a shuttle;
  category determines which requirements and exemption route apply.

## 3. Automation level & human oversight

- Target automation: fully automated driving (system performs the full
  dynamic driving task within its ODD).
- Human oversight: TBD. Current RDW-approved precedent (Rotterdam
  Meijersplein–RTHA shuttle, 2025) retains a trained safety driver onboard
  even though the ADS drives. Decide early whether the near-term target is
  "driverless software with a safety driver present" (has precedent) or
  fully unmanned (no current NL precedent for a shuttle of this type).

## 4. Operational Design Domain (ODD)

See `odd.md`. To define: route(s), speed range, road types, weather limits,
lighting conditions, interaction with pedestrians/cyclists, stopping
behavior.

## 5. Safety approach

See `safety-case/`. To include: hazard analysis, fallback/minimal-risk
maneuver behavior, remote monitoring plan, data logging (event data
recorder equivalent), cybersecurity approach.

## 6. Testing plan

- Closed-track testing (precedent: RDW Lelystad test facility) before any
  public-road operation.
- Staged public-road testing plan, coordinated with Qbuzz route(s).

## 7. Open questions for RDW

- Which approval route applies to this vehicle/use case?
- Vehicle category classification.
- Required redundancy (compute, braking, steering) for the chosen category.
- Data/logging requirements for approval and ongoing operation.
