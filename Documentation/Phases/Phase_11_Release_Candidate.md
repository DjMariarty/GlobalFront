# Phase 11 — Release Candidate

> Status: planned • release gate

## Goal

Подготовить проверяемый Release Candidate после feature freeze.

## Dependencies

- M7 Beta;
- approved feature freeze и release gates;
- deployment/server infrastructure decisions;
- compatibility matrix — TBD / Owner Decision Required.

## Scope

- feature freeze;
- critical bug fixing;
- final optimization;
- compatibility;
- deployment;
- server infrastructure;
- release validation.

## Key Systems

- release branch/build process — конкретная модель TBD;
- dedicated server infrastructure;
- deployment and rollback planning;
- compatibility validation;
- release checklist и critical defect gate.

## Milestones

- feature freeze;
- Release Candidate build;
- go/no-go evidence package.

## Acceptance Criteria

- feature freeze действует;
- critical bugs закрыты или имеют explicit owner waiver;
- final optimization и compatibility checks пройдены;
- deployment/server infrastructure проверены;
- release validation завершена.

## Tests

- full release regression;
- installation/deployment/server validation;
- compatibility matrix;
- upgrade/reconnect/replay compatibility where applicable;
- final performance/stability/soak/stress runs.

## Risks

- поздний critical defect;
- deployment/server infrastructure failure;
- protocol/build incompatibility;
- feature creep после freeze;
- incomplete rollback/recovery decisions.

## Out of Scope

- новые features;
- новый faction/content scope;
- подфракции и post-1.0 systems;
- balance changes вне critical release need.

## Definition of Done

Feature freeze соблюдён, critical issues закрыты, compatibility/deployment/server infrastructure и release validation подтверждены; Release Candidate готов к owner go/no-go.