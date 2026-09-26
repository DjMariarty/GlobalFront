# Adversarial Audit Packet: Phase 3, Step 3.3 (UnitViewBinder & Object Pooling)

> **Auditor Target:** DeepSeek v4.1 Flash (Subphase Auditor)  
> **Target Scope:** Phase 3, Step 3.3 (`UnitView`, `UnitViewPool`, `UnitViewBinder`, `UnitViewBinderTests`)  
> **Base Commit:** `5e4bb27` (HEAD of `main`)  
> **Normative Standards:** ADR-012, OD-23, OD-25, OD-26 in `Documentation/DECISIONS.md`, `DEVELOPMENT_STANDARD.md`  
> **Verification Status:** 711/711 EditMode passed (100%), 6/6 PlayMode passed (100%), Unity 6000.6.2f1  

---

## 1. Инструкция для аудитора DeepSeek v4.1 Flash

Проведите бескомпромиссный, враждебный аудит (Adversarial Code Audit) реализации **Шага 3.3 (UnitViewBinder & Object Pooling)** в проекте GlobalFront (детерминированная RTS на Unity 6).

### Категории дефектов:
* **P0 (Блокер):** Нарушение детерминизма, утечка презентации в симуляцию, падение рендеринга, бесконечный цикл или дедлок.
* **P1 (Критический):** Скрытые аллокации памяти (GC.Alloc) в горячем цикле кадра (`Render`, `Apply`), рассинхрон идентичности слотов (перепутывание юнитов при рецикле), утечка объектов из пула.
* **P2 (Минорный / Техдолг):** Неоптимальности, пограничные случаи, недостаточная валидация аргументов.
* **Вердикт:** `[APPROVE: ZERO DEFECTS]` либо перечень найденных дефектов с точным указанием строк кода и способа исправления.

---

## 2. Ключевые архитектурные инварианты для проверки

1. **Строгая граница Presentation / Simulation:**
   * Презентация (`UnitView`, `UnitViewBinder`, `UnitViewPool`) является строго односторонним потребителем данных. Ни один метод презентации не должен мутировать `ClientReplicationWorld` или вызывать команды симуляции.
2. **Zero-GC в горячем цикле (`OD-26`, `OD-28`):**
   * Методы `UnitView.Apply`, `UnitViewBinder.Render`, `UnitViewPool.Acquire` и `UnitViewPool.Release` должны выполняться с **0 байт аллокаций в куче (Zero-GC)**. Проверить отсутствие замыканий, boxing, LINQ, создания временных массивов и вызовов `new`.
3. **Контракт пулинга (`OD-26`):**
   * Запрещены вызовы `Object.Instantiate` и `Object.Destroy` во время боя. Все экземпляры должны выделяться на стадии `Warmup` либо при явном расширении блока (`growBlock`).
   * Защита от двойного возврата в пул (`double release`).
   * Корректная обработка потолка пула (`MaximumViews`).
4. **Идентичность слотов и защита от призраков:**
   * Слоты `ClientReplicationWorld` переиспользуются после гибели юнитов. Должна быть исключена ситуация, когда новому заспавненному юниту достаётся неотвязанный или некорректно спозиционированный трансформ старого погибшего юнита.
5. **Математическая безопасность (Zero-Division & Bounds):**
   * Проверить конвертацию `MillimetresToMetres` (`0.001f`), расчет `HealthFraction`, защиту от NaN / деления на 0 при расчётах поворота башни `TurretYawDegrees`.
6. **Соответствие иерархии рендеринга (`OD-26`):**
   * Полное отсутствие компонентов `Animator` (Mecanim) на массовых юнитах. Только статический корпус и дочерний трансформ башни.
