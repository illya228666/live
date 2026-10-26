
## 2026-10-24T08:12:18.0726027+00:00 — local

Подготовка завершена: существующие исследования внесены в BASELINE.md; инструменты кампании и отдельная исследовательская ветка готовы. Научные гипотезы для новой длинной кампании ещё не выбирались.

Next: По следующему запросу восстановить CONTEXT, задать бюджет сессии, выбрать исследовательский вопрос со свободой подходов, проверить сложность сценария и зарегистрировать дешевый pilot до оценки.

## 2026-10-25T10:53:34.5301776+00:00 — local

Исправлен температурный сценарий: thermal-v2 — новая база. Параметры ambient28.1/F32/r6/P240/A0.65; физический код не менялся. Проверены пассивные, лучшие фиксированные, случайные и активные сенсорные контроли на пяти seed; active comfort99.855%, fixed best≈41.47%, аналитическая минимальная неизбежная пиковая ошибка стационарного тела3.81°C. 163 теста прошли. Pilot/full теперь требуют passing review всех факторов; дефекты сценария надо исправлять, не только описывать.

Next: Начинать новые сравнения с configs/research-baseline.json и research/baseline.json. Перед добавлением голода, угроз или новых факторов проверить их пассивные/активные контроли и исправить вырожденные условия. Исторические многопотребностные конфигурации не считать новой валидированной базой.

## 2026-10-26T11:22:06.7426055+00:00 — local

Repository consolidation: campaign moved from codex/research-campaign-infrastructure to master after integrating research infrastructure and legacy fix branch histories. Study registrations, recorded evidence and past journal entries are unchanged.

Next: Continue on master; recover context, check the current baseline and obtain a new explicit session budget before launching research.
