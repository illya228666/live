# Campaign local

Branch: codex/research-campaign-infrastructure. Current: codex/research-campaign-infrastructure.

Read research/RESEARCHER.md, research/BASELINE.md, campaign.json, FINDINGS.md (if present) and the most recent journal entries before acting.

Compute defaults: 2 workers, 1 threads each; slice 10 min; reserve 2 GiB free RAM.

Stop requested: False.

Current comparison baseline: thermal-v2, configs/research-baseline.json. Scope: Temperature only. Additional factors must pass their own passive/active checks before scientific comparison. Review: research/scenarios/temperature-v2-review.json. Additional factors require their own passing review; repair trivial scenarios before scientific pilot/full runs.

| Study | Stage | Status | Hypothesis | Full artifacts |
|---|---|---|---|---|

## Recent decisions/events

{"kind":"initialized","data":{"branch":"codex/research-campaign-infrastructure","next":"Read research/BASELINE.md and choose a question; no full campaign has started."},"utc":"2026-10-24T07:49:31.3658492+00:00"}
{"kind":"decision","data":{"study":"","next":"По следующему запросу восстановить CONTEXT, задать бюджет сессии, выбрать исследовательский вопрос со свободой подходов, проверить сложность сценария и зарегистрировать дешевый pilot до оценки.","text":"Подготовка завершена: существующие исследования внесены в BASELINE.md; инструменты кампании и отдельная исследовательская ветка готовы. Научные гипотезы для новой длинной кампании ещё не выбирались."},"utc":"2026-10-24T08:12:18.0663318+00:00"}
{"kind":"decision","utc":"2026-10-25T10:53:34.5294736+00:00","data":{"study":"","text":"Исправлен температурный сценарий: thermal-v2 — новая база. Параметры ambient28.1/F32/r6/P240/A0.65; физический код не менялся. Проверены пассивные, лучшие фиксированные, случайные и активные сенсорные контроли на пяти seed; active comfort99.855%, fixed best≈41.47%, аналитическая минимальная неизбежная пиковая ошибка стационарного тела3.81°C. 163 теста прошли. Pilot/full теперь требуют passing review всех факторов; дефекты сценария надо исправлять, не только описывать.","next":"Начинать новые сравнения с configs/research-baseline.json и research/baseline.json. Перед добавлением голода, угроз или новых факторов проверить их пассивные/активные контроли и исправить вырожденные условия. Исторические многопотребностные конфигурации не считать новой валидированной базой."}}

## Resume in Codex

Continue campaign 'local' on branch 'codex/research-campaign-infrastructure'. Read this file, research/RESEARCHER.md, research/BASELINE.md, the immutable study registrations and referenced evidence. Reconcile suite.json/result.json with journal before launching anything. Preserve completed runs; do not treat reward as success. Check the session deadline. Record the next decision and next action before ending the session.