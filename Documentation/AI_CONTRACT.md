# AI Development Contract

> Нормативный процессный контракт для AI-driven разработки GlobalFront.

## Purpose

AI помогает выполнять утверждённый [Master Game Plan](MASTER_GAME_PLAN.md), но не является владельцем Product Scope, Architecture или release decisions. Этот контракт применяется ко всем AI-задачам в репозитории.

## Non-Negotiable Rules

1. **Scope:** AI не выдумывает утверждённый scope. Отсутствующие требования маркируются `TBD / Owner Decision Required`.
2. **Architecture:** AI не меняет approved architecture без ADR и owner approval. Локальная реализация не может молча создавать новый architectural precedent.
3. **Tests:** AI не пропускает обязательные тесты и не скрывает failed, skipped или unavailable checks.
4. **Performance:** AI не заявляет performance, scale или reliability без воспроизводимого benchmark и сохранённых evidence.
5. **Phase completion:** AI не объявляет phase или milestone `COMPLETE` без выполненного Definition of Done, review evidence и соответствующего commit.
6. **Documentation conflicts:** при противоречии между approved documents AI останавливает затронутую работу и запрашивает Owner Decision.
7. **Evidence:** roadmap, комментарий или намерение не считаются доказательством реализации.
8. **Authority:** AI не выполняет commit/push/tag или внешнее release/deployment действие без явного разрешения.

## Status Model

| Status | Meaning | Required Evidence |
|---|---|---|
| `PLANNED` | scope утверждён, implementation не начата | approved source и dependencies |
| `IN_PROGRESS` | работа начата, DoD ещё не выполнен | active task, observable changes и текущие checks |
| `COMPLETE` | DoD выполнен и результат зафиксирован | implementation, required tests, review, documentation и commit |
| `BLOCKED` | продолжение невозможно без решения или зависимости | blocker evidence, attempted alternatives и escalation |

Допустимые переходы: `PLANNED → IN_PROGRESS → COMPLETE`; любой активный статус может перейти в `BLOCKED`. Возврат из `BLOCKED` требует зафиксированного решения или снятой зависимости. Обнаруженный regression возвращает affected work из `COMPLETE` в новый tracked issue/task, но исторический commit не переписывается.

## Review Gates

Работа проходит gates последовательно; пересечение фаз не отменяет gate:

1. **Scope Gate** — есть approved requirement, phase boundary и явный out-of-scope.
2. **Architecture Gate** — изменение соответствует действующим ADR и [Architecture](ARCHITECTURE.md); новый direction получил approval.
3. **Integration Contract Gate** — затронутые межфазовые interfaces/adapters определены по [Integration Contracts](IntegrationContracts/README.md).
4. **Implementation Gate** — изменение ограничено approved scope и не содержит скрытого архитектурного расширения.
5. **Test Gate** — обязательные unit/integration/regression/benchmark checks выполнены; отклонения перечислены.
6. **Review Gate** — blocking findings закрыты или имеют explicit owner waiver.
7. **Documentation Gate** — current state, status, decisions, risks и affected phase docs синхронизированы.
8. **Commit Gate** — commit существует и однозначно связывает accepted result с milestone; сам AI создаёт его только при явном разрешении.

Gate считается пройденным только по evidence. Формулировки «должно работать» или «вероятно прошло» недостаточны.

## Escalation Triggers

AI обязан остановить затронутую часть работы и выполнить escalation, если:

- approved documents противоречат друг другу;
- требуемая деталь отсутствует и влияет на scope, architecture, data/protocol compatibility или DoD;
- изменение ломает предыдущую phase/contract либо требует incompatible migration;
- обязательные tests/benchmarks недоступны или не проходят;
- performance claim не имеет agreed benchmark;
- Critical/High risk не имеет mitigation или owner;
- запрашиваемое действие требует полномочий на commit, release, deployment, external service или необратимое изменение;
- обнаружен security, licensing, data-loss или production-operations риск вне утверждённого решения.

## Escalation Procedure

1. Остановить только affected work; сохранить безопасный проверяемый state.
2. Указать конфликт или blocker с точными документами, файлами, тестами и evidence.
3. Сформулировать минимальный `Owner Decision Required` без подстановки собственного scope.
4. Перечислить допустимые alternatives и последствия, если они подтверждены анализом.
5. Обновить status на `BLOCKED`, когда критерии blocked действительно выполнены.
6. Возобновить работу только после явного решения и обновления authoritative documentation/ADR.

AI не обходит escalation временным «предположением», ослаблением теста или незафиксированным архитектурным исключением.

## Completion Report

Финальный отчёт AI должен различать: implemented, verified, not verified, TBD, blocked и pre-existing changes. Он обязан сообщать test/benchmark evidence, affected documents, unresolved risks и `git status`.

## Related Documents

- [Development Standard](DEVELOPMENT_STANDARD.md)
- [Decisions](DECISIONS.md)
- [Risk Register](RISK_REGISTER.md)
- [Current State](CURRENT_STATE.md)