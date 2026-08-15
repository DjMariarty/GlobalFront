# GlobalFront Integration Contracts

> Нормативный процессный слой для границ Phase 2 ↔ 3, Phase 3 ↔ 4 и Phase 4 ↔ 5.

## Core Principle

Завершённая или принятая предыдущая фаза является contract baseline. Следующая фаза не ломает её поведение, authority, compatibility или Definition of Done без отдельного approved change.

Любое несовместимое изменение требует:

```text
R&D → impact analysis → ADR / Owner Approval → migration plan
→ implementation → integration tests → documentation → commit
```

Молчаливое изменение contract запрещено.

## Contract Form

Интеграция проходит через явно определённые interfaces, adapters, messages/schemas или data boundaries. Конкретный механизм выбирается ADR; скрытые зависимости от internal implementation не считаются контрактом.

Для каждого integration contract фиксируются:

- producer и consumer;
- ownership/authority;
- inputs, outputs и invariants;
- versioning/compatibility expectations;
- failure/rejection behavior;
- migration path для approved incompatible changes;
- обязательные unit, integration и regression tests;
- evidence и commit, установивший baseline.

## Phase 2 ↔ Phase 3

**Phase 2 provides:** authoritative lifecycle, command-channel boundary, session/identity, transport, snapshot networking и reconnect/resync contracts по мере их принятия.

**Phase 3 consumes:** эти boundaries для RTS Core, не привязывая gameplay rules к конкретной transport implementation.

**Protection rule:** gameplay requirements Phase 3 не меняют Phase 2 protocol/authority молча. Новый command/state need оформляется как contract change с impact analysis и ADR.

**Required integration tests:** command submission, authoritative tick application, gameplay-state snapshot, ownership/rejection, disconnect/reconnect и deterministic regression для representative vertical slice.

## Phase 3 ↔ Phase 4

**Phase 3 provides:** утверждённые economy, building, production, movement, combat, Fog of War, capture, garrison, veterancy, general/ability, basic AI и map-framework contracts, а также benchmark baseline.

**Phase 4 consumes:** gameplay contracts и связывает их с real multiplayer через adapters Phase 2.

**Protection rule:** integration не переносит authority на Client, не дублирует simulation rules и не переписывает gameplay semantics без approved change.

**Required integration tests:** end-to-end 1v1, economy/production/combat/abilities/Fog of War, authoritative validation, snapshots, reconnect/resync, hidden-information boundaries и benchmark regression.

## Phase 4 ↔ Phase 5

**Phase 4 provides:** работающий multiplayer RTS prototype и стабильные networked gameplay extension points.

**Phase 5 consumes:** эти extension points для полного roster NATO, Russia, China, GLA и USA.

**Protection rule:** faction expansion не ломает accepted multiplayer prototype, protocol compatibility или authoritative validation без отдельного approved change. Новые faction systems подключаются через определённые interfaces/data contracts/adapters.

**Required integration tests:** all-five-faction roster/data validation, networked command/state coverage, deterministic regression, multiplayer parity, protocol/load regression и cross-faction gameplay integration.

## Change and Review Gate

Изменение contract принимается только когда:

1. scope и affected phases определены;
2. compatibility impact описан;
3. ADR/owner approval получен, если меняется approved architecture или baseline;
4. interfaces/adapters и migration path задокументированы;
5. integration tests добавлены и пройдены;
6. regressions предыдущей фазы отсутствуют либо явно приняты владельцем;
7. phase/status/risk documentation обновлена;
8. accepted result зафиксирован commit.

До выполнения gate contract status не может быть `COMPLETE`.

## Escalation

При конфликте фаз, невозможности сохранить compatibility, отсутствии interface owner или failed integration tests работа получает `BLOCKED` для affected scope и escalates по [AI Contract](../AI_CONTRACT.md).

## Related Documents

- [AI Contract](../AI_CONTRACT.md)
- [Architecture](../ARCHITECTURE.md)
- [Risk Register](../RISK_REGISTER.md)
- [Phase Index](../Phases/README.md)