# Campaign ecology-audit

Branch: master. Current: master.

Read research/RESEARCHER.md, research/BASELINE.md, campaign.json, FINDINGS.md (if present) and the most recent journal entries before acting.

Compute defaults: 2 workers, 1 threads each; slice 10 min; reserve 2 GiB free RAM.

Last session deadline (UTC): 10/26/2026 14:22:40. An expired deadline needs a new explicit session budget.

Stop requested: True.

Current comparison baseline: ecology-v2, configs/research-ecology-v2.json. Scope: Temperature (unchanged thermal-v2), hunger/food and avoidable wildlife proximity. Safety is a spatial/contact proxy, not harm or survival. Review: research/scenarios/ecology-v2-review.json. Additional factors require their own passing review; repair trivial scenarios before scientific pilot/full runs.

| Study | Stage | Status | Hypothesis | Full artifacts |
|---|---|---|---|---|
| ecology-v2-admission-only | pilot | registered | The validated ecology-v2 baseline passes the existing scientific suitability gate and survives freezing without a learner trial. | artifacts\research\ecology-audit\studies\ecology-v2-admission-only\results |

## Recent decisions/events

{"utc":"2026-10-26T11:52:40.8093401+00:00","data":{"branch":"codex/ecology-scenario-audit","next":"Read research/BASELINE.md and choose a question; no full campaign has started."},"kind":"initialized"}
{"utc":"2026-10-26T11:52:40.9209422+00:00","data":{"minutes":90.0},"kind":"session-started"}
{"data":{"study":"","next":"Выполнить независимую валидацию Hunger/Food и Wildlife/Safety, затем совместный аудит и точное повторение. При провале ремонтировать и регистрировать новую проверку; не объявлять прохождение по калибровочным данным.","text":"Калибровка до held-out: один кабан оставляет углы пассивно безопасными; шесть независимых кабанов с широким радиусом чрезмерно мешают терморегуляции. Частое (.4) сближение также отвергнуто. Выбран wildlife1.1.0: шесть, .65 speed, .1 редких неточных поворотов к агенту, радиус оценки6. Избыточная еда100/8 делает поиск ненужным при избегании; выбран интервал160/max3/minDistance6, неизменная физиология .001 и .2/яблоко. Параметры thermal-v2 сохранены. Протокол docs/ecology-v2-protocol.md зафиксирован до seed61001/62003/63011/64009/65003; по12000 шагов,19контролей,3сценария,без обучения."},"kind":"decision","utc":"2026-10-26T12:03:43.6411503+00:00"}
{"data":{"stage":"pilot","next":"Run a bounded compute slice; inspect baselines and per-seed behavior.","study":"ecology-v2-admission-only","hypothesis":"The validated ecology-v2 baseline passes the existing scientific suitability gate and survives freezing without a learner trial.","rule":"Admission/freeze smoke only. Do not execute this study or infer learner results."},"kind":"registered","utc":"2026-10-26T12:26:27.0240426+00:00"}
{"data":{"next":"Resume explicitly; incomplete trials restart from their seed.","force":false},"kind":"stop-requested","utc":"2026-10-26T12:26:27.0957827+00:00"}
{"utc":"2026-10-26T12:36:40.6712186+00:00","data":{"study":"","text":"Аудит завершён. Новая база ecology-v2 и отдельные hunger-v2/safety-v2 допущены; thermal-v2 сохранён. Проверка19контролей×5held-out seed×12000 шагов в каждом из3сценариев, заключительная пара одной сборкой8F33D1D7...:285совпавших пар,действия/состояния сRNG/метрикиточносовпали. Совместный контроль:голод.18876,голод>.8=0,близость<6=.07938,контактныйпрокси<3=.000783,MAEтемпературы.7942,комфорт78.19%. Абляции подтверждают необходимость каждогофактора. 178тестов прошли,Godot inference1000шагов,горячаязамена и старыйRNG совместимы. Safety не является реальнымуроном/выживанием. Устранено повреждение SHAстарогоthermalJSONиз-заGitLFконвертации; исходные данные неизменны,подписанныебайты защищены. Полные и отрицательные результаты сохранены; см.docs/ecology-v2-report.md.","next":"Не запускать ecology-v2-admission-only: это завершённая проверка регистрации, learner trials отсутствуют, STOP сохранён. Дальнейшую исследовательскую кампанию начинать только по запросу пользователя на ветке, включающей этот baseline, с новым study/бюджетом. Использовать registry research/baseline.json; при изменении условий/горизонта/оценки переаудитировать все факторы и исправлять дефекты, не ограничиваться caveat."},"kind":"decision"}
{"kind":"decision","data":{"study":"","next":"Продолжать работу в master. Не создавать ветку автоматически при новой задаче или кампании. Audit admission-only не запускать.","text":"По указанию пользователя обычная работа и исследования ведутся в master; отдельная ветка нужна только при конкретной необходимости изоляции. Аудит b89b4f2 перенесён в master fast-forward. Записи прошлых запусков и зафиксированные артефакты не изменены; кампания теперь закреплена за master."},"utc":"2026-10-26T12:43:50.8025892+00:00"}

## Resume in Codex

Continue campaign 'ecology-audit' on branch 'master'. Read this file, research/RESEARCHER.md, research/BASELINE.md, the immutable study registrations and referenced evidence. Reconcile suite.json/result.json with journal before launching anything. Preserve completed runs; do not treat reward as success. Check the session deadline. Record the next decision and next action before ending the session.