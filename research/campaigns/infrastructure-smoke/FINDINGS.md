
## 2026-10-24T08:09:09.7299011+00:00 — pilot-02

Sequential reference saved. A deliberate stop request interrupted admission; immutable configs and completed evidence are retained.

Next: Resume the registered study and compare with the sequential reference.

## 2026-10-24T08:09:20.7861978+00:00 — pilot-02

Methodological hypothesis supported in this smoke: 4 matching pairs at 1e-9. No conclusion about behavioral superiority or adequacy of thermoregulation scenarios.

Next: Do not start a long campaign yet. On the next user request, read BASELINE.md, choose a research question, and preregister a discriminating scenario/pilot.

## 2026-10-24T08:11:07.1390601+00:00 — pilot-01

Уточнение аудита: автоматический admission по свободной RAM снизил фактический parallel до 1. Четыре пары этого smoke подтверждают stop/resume и память, но не параллельное исполнение. Данные сохранены; pilot-02 отдельно проверил два реальных worker.

Next: Для доказательства параллелизма использовать pilot-02 и его last-execution/smoke-proof; не объединять эту методологическую проверку с научной оценкой алгоритмов.

## 2026-10-24T08:11:07.2457994+00:00 — pilot-02

Финальная проверка: 4/4 пары совпали, фактический parallel=2, проверена force-остановка orphan worker по PID+времени+пути, completed result.json не переписались, новый процесс восстановил анализ и следующий шаг. Полный набор: 158 тестов без ошибок.

Next: Не начинать длинную кампанию до следующего запроса. В новой сессии прочитать BASELINE.md, выбрать вопрос, проверить различающую способность сценария и зарегистрировать pilot до оценки.

## 2026-10-24T08:12:17.9837650+00:00 — pilot-02

Проверен истёкший дедлайн сессии: вычислительный срез отклонён, новые trials не стартовали. Проверка backup во время занятой блокировки также отклоняет копирование.

Next: Для реальной следующей кампании использовать local; явно задать новый бюджет сессии и зарегистрировать выбранный pilot.
