# Campaign scenario-admission-smoke

Branch: codex/research-campaign-infrastructure. Current: codex/research-campaign-infrastructure.

Read research/RESEARCHER.md, research/BASELINE.md, campaign.json, FINDINGS.md (if present) and the most recent journal entries before acting.

Compute defaults: 2 workers, 1 threads each; slice 10 min; reserve 2 GiB free RAM.

Stop requested: True.

Current comparison baseline: thermal-v2, configs/research-baseline.json. Scope: Temperature only. Additional factors must pass their own passive/active checks before scientific comparison. Review: research/scenarios/temperature-v2-review.json. Additional factors require their own passing review; repair trivial scenarios before scientific pilot/full runs.

| Study | Stage | Status | Hypothesis | Full artifacts |
|---|---|---|---|---|
| stable-baseline-admission | pilot | registered | Admission accepts the audited thermal baseline and rejects old trivial physics before scientific experiments. | artifacts\research\scenario-admission-smoke\studies\stable-baseline-admission\results |
| validated-admission | pilot | registered | Admission accepts the audited thermal baseline and rejects old trivial physics before scientific experiments. | artifacts\research\scenario-admission-smoke\studies\validated-admission\results |

## Recent decisions/events

{"utc":"2026-10-25T10:45:30.9228404+00:00","data":{"next":"Read research/BASELINE.md and choose a question; no full campaign has started.","branch":"codex/research-campaign-infrastructure"},"kind":"initialized"}
{"utc":"2026-10-25T10:45:38.1890351+00:00","data":{"hypothesis":"Admission accepts the audited thermal baseline and rejects old trivial physics before scientific experiments.","rule":"Accept only matching independently audited physical conditions; no algorithm trial is required for this admission test.","next":"Run a bounded compute slice; inspect baselines and per-seed behavior.","stage":"pilot","study":"validated-admission"},"kind":"registered"}
{"utc":"2026-10-25T10:45:38.2571540+00:00","data":{"next":"Resume explicitly; incomplete trials restart from their seed.","force":false},"kind":"stop-requested"}
{"kind":"compute-started","utc":"2026-10-25T10:53:33.9881291+00:00","data":{"threads":1,"parallel":1,"minutes":1.0}}
{"kind":"stop-requested","utc":"2026-10-25T10:53:34.3966999+00:00","data":{"force":false,"next":"Resume explicitly; incomplete trials restart from their seed."}}
{"kind":"decision","utc":"2026-10-25T10:53:34.4505623+00:00","data":{"study":"validated-admission","text":"Проверка admission: старый тривиальный сценарий отклонён; неизменный thermal-v2 принят с копией review/evidence. Изменённый review отклонён перед исполнением; тестовая копия восстановлена. Ни одного learner trial не запущено.","next":"Сохранить этот smoke остановленным; текущая рабочая база — thermal-v2. Для новых факторов требовать отдельный аудит и ремонт до сравнения подходов."}}
{"data":{"stage":"pilot","study":"stable-baseline-admission","next":"Run a bounded compute slice; inspect baselines and per-seed behavior.","rule":"Accept unchanged validated temperature-only physics after deterministic build metadata stabilization; do not start a learner.","hypothesis":"Admission accepts the audited thermal baseline and rejects old trivial physics before scientific experiments."},"kind":"registered","utc":"2026-10-25T11:09:28.3873920+00:00"}
{"data":{"next":"Resume explicitly; incomplete trials restart from their seed.","force":false},"kind":"stop-requested","utc":"2026-10-25T11:09:28.4644897+00:00"}
{"data":{"text":"Повторный admission на окончательной стабильной сборке прошёл; новый fingerprint соответствует неизменной физике. Проверено отсутствие запуска learner. Предыдущий admission сохранён как историческая проверка сборки с Git-метаданными.","study":"stable-baseline-admission","next":"Сохранять этот smoke остановленным; использовать thermal-v2 registry для будущих научных сравнений."},"kind":"decision","utc":"2026-10-25T11:09:28.5083729+00:00"}

## Resume in Codex

Continue campaign 'scenario-admission-smoke' on branch 'codex/research-campaign-infrastructure'. Read this file, research/RESEARCHER.md, research/BASELINE.md, the immutable study registrations and referenced evidence. Reconcile suite.json/result.json with journal before launching anything. Preserve completed runs; do not treat reward as success. Check the session deadline. Record the next decision and next action before ending the session.