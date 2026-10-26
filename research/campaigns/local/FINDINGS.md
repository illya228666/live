
## 2026-10-24T08:12:18.0726027+00:00 — local

Подготовка завершена: существующие исследования внесены в BASELINE.md; инструменты кампании и отдельная исследовательская ветка готовы. Научные гипотезы для новой длинной кампании ещё не выбирались.

Next: По следующему запросу восстановить CONTEXT, задать бюджет сессии, выбрать исследовательский вопрос со свободой подходов, проверить сложность сценария и зарегистрировать дешевый pilot до оценки.

## 2026-10-25T10:53:34.5301776+00:00 — local

Исправлен температурный сценарий: thermal-v2 — новая база. Параметры ambient28.1/F32/r6/P240/A0.65; физический код не менялся. Проверены пассивные, лучшие фиксированные, случайные и активные сенсорные контроли на пяти seed; active comfort99.855%, fixed best≈41.47%, аналитическая минимальная неизбежная пиковая ошибка стационарного тела3.81°C. 163 теста прошли. Pilot/full теперь требуют passing review всех факторов; дефекты сценария надо исправлять, не только описывать.

Next: Начинать новые сравнения с configs/research-baseline.json и research/baseline.json. Перед добавлением голода, угроз или новых факторов проверить их пассивные/активные контроли и исправить вырожденные условия. Исторические многопотребностные конфигурации не считать новой валидированной базой.

## 2026-10-26T11:22:06.7426055+00:00 — local

Repository consolidation: campaign moved from codex/research-campaign-infrastructure to master after integrating research infrastructure and legacy fix branch histories. Study registrations, recorded evidence and past journal entries are unchanged.

Next: Continue on master; recover context, check the current baseline and obtain a new explicit session budget before launching research.

## 2026-10-26T13:10:20.7549985+00:00 — local

Контекст согласован с ecology-v2: master f4f279c содержит origin/master b89b4f2; научных studies в local и результатов/активных workers нет. Старый thermal-v2 совет в FINDINGS исторический: текущая база configs/research-ecology-v2.json, review AD148C10A11E01AF388780A8F4E461BD74340E37ABCCB1D1C4898A2ED692DC30, три фактора проверены. Исходное пользовательское изменение Godot packages.lock.json сохранено. Начальный вопрос: уменьшает ли нелинейное усиление наибольшего нормированного дефицита провалы конкурирующих потребностей при неизменном learner? Вычисления ожидают явного бюджета сессии.

Next: Прочитать механизм нелинейной конкуренции в Keramati & Gutkin 2014; проверить p=1 против p=4 weighted power mean при общей reward mapping; аналитические тесты и одинаковая физика, затем зарегистрировать pilot после задания бюджета.

## 2026-10-26T13:14:37.1768342+00:00 — power-mean-pilot-01

До оценки зарегистрирован power-mean-pilot-01: 2 мотивации x3 seed, 20k training, 5x12k evaluation, неизменный DQN. 179/179 fast tests прошли; p1 совпадает с legacy rewards и физика p1/p4 идентична на фиксированных действиях. При отсутствии численного лимита применён сохранённый sessionMinutes=30, дедлайн 13:44 UTC без автопродления. Первый запуск не начал trials из-за консервативной оценки RAM: доступно 3.55 GiB, reserve2+worker2. Установлен один worker с оценкой1 GiB, reserve2 сохранён; проверить реальное потребление первого trial. Это техническая остановка, не отрицательный научный результат.

Next: Выполнить pilot одним CPU worker, проверить память и per-seed результаты; применить предзаписанное правило p4, не выбирать checkpoint задним числом.

## 2026-10-26T13:23:00.6997534+00:00 — power-mean-pilot-01

Pilot завершён 6/6 без ошибок, по3 training seed и5 evaluation worlds x12k. p4 не прошёл gate: temperature MAE p1=5.958+/-0.022, p4=2.869+/-1.790; hunger .95275 vs .94554; proximity .27426 vs .53799; contact proxy .07563 vs .20696. Худший criterion ratio p1 [9.478,9.322,9.334], p4 [21.802,17.008,23.277] для42/73/101: улучшений0/3. Все6 финальных политик провалили критерии. Это обмен терморегуляции на угрозу, не освоение потребностей. Короткий20k horizon не опровергает p4 вообще. Compact evidence research/campaigns/local/results/power-mean-pilot-01-summary.json; исходные результаты и frozen bundle сохранены в artifacts. Исследование p4 не продвигать в full.

Next: Отдельная learning-only гипотеза: exploration с capped zeta(2) repeats max32 вместо одношагового шума, исходная homeostasis, прежние DQN updates/gamma/replay. Сначала тесты baseline RNG, легальности и изоляции frozen evaluation, затем зарегистрировать paired pilot. Учесть nominal occupancy correction и реальные прерывания у границ.

## 2026-10-26T13:50:15.2066978+00:00 — persistent-exploration-pilot-01

Второй pilot завершён6/6 без ошибок в13:31:53 UTC, до session deadline13:44:01. Все6 failed behavior. Persistent против step: hunger .94860+/-.00675 vs .95275+/-.00867; MAE5.9037 vs5.9581; proximity .31342 vs.27426 (+14.3%); contact.07689 vs.07563. Худший criterion ratio для42/73/101=9.175/9.292/9.139:10%-улучшений0/3, снижений hunger>=.05 тоже0/3. Не продвигать в full. Movement .795 vs.436 и reversal .794 vs.656 не означают полезного поиска. Actual exploratory fraction78.56-79.16% при nominal81%, illegal terminations449-477: полная каузальная изоляция temporal persistence от границ не достигнута. 183 fast tests прошли. Все3 duration1 контроли точно воспроизвели training/evaluation action hashes и все метрики первого p1 (maxdiff0), это репликация, не добавочные независимые seed. Результаты, источники, отрицательные выводы и compact JSON сохранены; отчёт docs/research-local-2026-10-26.md. При возвращении к анализу дедлайн уже истёк, новые вычисления не запускать, CLI workers нет.

Next: После нового бюджета зарегистрировать отдельный learning-only pilot PER alpha0/.6 при исходной weighted-homeostasis, beta.4 и step exploration; не сочетать с p4/persistence. Schaul etal arXiv1511.05952v4 разделы3.1-3.4/Algorithm1/4-5 прочитаны26.10.2026; учитывать постоянную beta и batch-normalization Live. Старые слабые PER результаты сохранены; новая20k проверка оправдана только ecology-v2. Если снова отрицательно, исследовать факторизованное предсказание изменений нужд и model-based/линейный controller вместо очередного перебора DQN. Не считать main goal достигнутой: освоения нескольких нужд пока нет.
