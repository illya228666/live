
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

## 2026-10-26T14:12:26.1061062+00:00 — local

Пользователь26.10.2026 явно разрешил сколь угодно длительное автономное исследование до удовлетворительного освоения нескольких нужд, сохранил свободу методов. Прежний30min лимит больше не ограничивает кампанию; выставлена техническая24h сессия, вычисления по-прежнему bounded slices10min и с памятью после циклов. Master содержит origin/master; исходный пользовательский Godot lockfile сохранён. Оба прошлых suite completed, активных workers нет. Новый replay pilot увеличивается до100k, поскольку20k заканчивался при epsilon.62 и не проверял освоение после падения exploration к.05. Это проверка недостаточного бюджета одновременно с контролируемым сравнением uniform/PER, каждый learner получает одинаковые100k.

Next: Зарегистрировать replay-pilot-01:3 paired training seeds,5 оценочных миров x12k, frozen final100k и кривые каждые25k. Анализировать каждую нужду; при провале перейти от scalar critic к структурному обучению модели/факторизованным предсказаниям, не подгонять ecology.

## 2026-10-26T14:22:39.9822223+00:00 — replay-pilot-01

6/6 trials completed100k, без ошибок. Uniform/PER: MAE1.04885/.87975, hunger.91230/.93011, hunger>.8 .85839/.88832, proximity.19059/.18197, contact.01886/.02022. Ни один trial не прошёл все критерии. PER hunger улучшается лишь в seed42 на.018 (<.05), ухудшается в73 и101; worst ratio улучшений>=10%0/3. Не продвигать PER в full. Увеличение20k->100k явно улучшает температуру/угрозу, но не формирует устойчивого поиска еды: примерно7-11 яблок за12k против необходимых60 для компенсации расхода. Нельзя приписывать провал только20k бюджету или только exploration. Summary research/campaigns/local/results/replay-pilot-01-summary.json. Существующая PER beta.4/batch normalization ограничивает обобщение на всю семью. Физика и мотивация общие.

Next: Новый learning-only механизм: relational linear true-online Sarsa, λ0 против.9, одинаковые признаки/alpha/gamma/epsilon. Признаки декодируют доступные направления/дистанции всех appearance типов одинаково, не присваивают им положительную/отрицательную полезность. Нулевые начальные веса; нет teacher/privileged state. Проверить sparse acquisition credit и чувствительность к представлению, затем независимая confirmation только при достаточном поведении.

## 2026-10-26T14:29:38.6150351+00:00 — relational-sarsa-pilot-01

6/6 completed100k; все failed behavior. λ0/.9: hunger.92951/.95804, MAE1.81196/.79555, proximity.20763/.14822, contact.00443/.00632, food13.67/.20. Trace улучшает терморегуляцию, но практически прекращает питание. Ни full gate, ни partial foraging gate не пройдены.187 fast tests прошли,4 новых аналитических/интерфейсных случая. Кривая λ0 seed42 имела transient hunger.648 при75k, но финальные100k хуже; checkpoint не заменять. Найден конкретный дефект априорной репрезентации: L2-нормирование всего featurevector делает bias x0 зависимым от действия/состояния, поэтому одна intercept weight не может представлять постоянный reward offset. Это математический факт, но причина поведенческого провала ещё требует абляции. Новый fixed режим оставляетx0=1 и клипирует/масштабирует остальные компоненты, L2 default сохраняется для старых сборок/контролей;5 целевых тестов прошли.

Next: Зарегистрировать normalization-pilot-01: L2λ0 vs fixedλ0 (чистая абляция normalization), дополнительный fixedλ.9;100k,3 paired seed, прежний reward/physics. Не объявлять успех по transient food или thermal-only improvements.

## 2026-10-26T14:35:41.3327477+00:00 — normalization-pilot-01

9/9 trials completed100k; все failed full criteria. L2λ0/fixedλ0/fixedλ.9: hunger.92951/.15236/.60990, MAE1.81196/3.77562/1.02647, proximity.20763/.05679/.08896, contact.00443/.00218/.00267, food13.67/72.13/37.53. Fixedλ0 приобрёл питание на3/3 seeds (hunger .123/.131/.203) и избегание, но ухудшил температуру в>2раза: prereg partial non-regression gate не пройден; full не продвигать. Fixed-trace питание хорошее лишьseed73 (.126),42/101 .807/.897; это не multi-seed success. Нормирование оказывает большой причинный эффект на итоговое поведение, но интерпретацию intercept отдельно от scale пока не выделить. L2λ0 контроль совпадает с прежним по метрикам; проверить action hashes при итоговом анализе. Полная память и summary сохранены.

Next: Новая гипотеза недостаточной экспозиции: те же fixedλ0/.9 на1M transitions, без настройки reward/alpha/gamma/epsilon. Оценивать каждые100k, финал1M заранее; никаких замен seed/checkpoint. Если баланс не возникнет, исследовать временной горизонт/average-reward или модельные предсказания нужд, а не объявлять food-only success.

## 2026-10-26T14:45:23.7269711+00:00 — long-exposure-pilot-01

6/6 completed1M, все failed gate. Fixedλ0 robust на3 seeds: hunger.14664+/-.01091, hunger>.8=0, food72.93, proximity.07725, contact.00219; MAE1.1575, outside.43797 (все3 outside>.35). Fixedλ.9: MAE.7061/outside.2315, но hunger.38435+/-.47506: seed42/73 .112/.109, seed101 .9329. Proximity.12093:42/73 чуть>.12,101.1093. Третий seed опроверг впечатление устойчивого баланса; не выбирать первые2. Нет full promotion и не продлевать этот learner ещё раз без новой гипотезы. Точка100k на всех6 trials совпала с предыдущим pilot по evaluation action hashes/метрикам, maxdiff0: замороженная оценка не мешает обучению. Long summary сохранён.

Next: Изолированное исследование мотивации при зафиксированном robust fixedλ0 learner и1M budget: температурный вес1/1.5/2, прочие1. Это попытка исправить единственный систематический провал robust варианта (outside.438), а не менять критерии/физику. Новый reweighted-homeostasis пакет с default identity и аналитическим тестом готов; выбрать кандидата по заранее зафиксированному полному gate всех3 seed, затем новые confirmation seeds.

## 2026-10-26T15:00:32.0224958+00:00 — thermal-weight-pilot-01

9/9 completed1M; все failed; w1/1.5/2: MAE1.1575/1.01024/.95589, outside.43797/.38553/.36574, hunger.14664/.15565/.45686, proximity.07725/.09201/.11356. Weight2 возвращает сильное голодание наseed101;42 outside.3500167,73 .3878167. Ни кандидат1.5, ни2 не прошёл prereg gate; не объявлять успех округлением, не выбирать первые2 seeds. Identityw1 совпадает с прежним по training и evaluation hashes на3/3.189 fast tests прошли. Простое усиление температуры сокращает discomfort, но не устраняет ограничение всей репрезентации/политики. Summary сохранён.

Next: Новый learning-only pilot: self-supervised one-step thermal predictor из2000 собственных наблюдаемых переходов, затем freeze predictor и linearSarsa с дополнительными признаками прогнозируемой температуры. Reward вернуть исходный, λ0/fixed/1M общие. Контроли no-model и frozen-zero-model; все имеют2000 warmup без TD updates. Источник Dosovitskiy/Koltun2017 sections3-3.2, но это наша prediction-augmented Sarsa, не реплика DFP. Аналитические RLS/clone/freeze тесты прошли; зарегистрировать до world evaluation.

## 2026-10-26T15:14:40.0190791+00:00 — predictive-features-pilot-01

9/9 completed1M, все failed. Base/zero совпадают training+evaluation hashes на3/3. Base vs learned-model: MAE1.15989/.29416, outside.44291/.06246, hunger.13612/.94046, food72.93/5.8, proximity.07531/.12108, contact.00186/.00509. Предсказательная модель помогает температуре на3/3, но приводит к почти полному голоданию; full и partial gate не пройдены. Это локальная оптимизация одного навыка, не balanced success. RLS2000 samples/seed coefficients сохранены:42 [.89198,.21133,-.02218],73 [.91300,.15732,-.01757]; clipping наблюдений ограничивает точность линейной модели.191 fast tests прошли. Новая реализация persistent option для Sarsa после общего warmup готова;13 целевых тестов прошли, включая невмешательство frozen/cached on-policy action и одинаковую модель до изменения exploration. Campaign default sessionMinutes приведён к1440 по снятому пользователем лимиту; поправлено только UTC отображение дедлайна в CONTEXT (расчёт не менялся).

Next: Пререгистрировать predictive-exploration-pilot-01: тот же learned-model, max repeat1 vs32,1M и те же3 pilot seeds; до warmup2000 поведение идентично, coefficients должны совпадать. Проверить перенос опыта за пределы лёгкой thermal equilibrium, actual exploration fractions/illegal endings, final all-needs gate. Старый DQN persistent negative сохраняется и не является опровержением этой новой комбинации.

## 2026-10-26T15:24:39.7535708+00:00 — predictive-exploration-pilot-01

6/6 completed1M, full gate failed for every trial. Однако model-persistent на3/3 приобретает питание и температуру: hunger.21690+/-.04799 (42/73/101=.2672/.2118/.1716), above.01671, MAE.31545, outside.08114, food67.2. Единственный общий провал proximity.13404 (.1353/.1356/.1312)>.12; contact.00742<.01. Model-step hunger.94046. Исходные coefficients одинаковы междуparity variants по matched warmup; step control reproduces previous training/eval hashes на3/3. Actual repeat exploratory fractions6.62-6.74% (998001 choices includes pending),1151-1218 illegal ends; nominal amount not exactly matched, so temporal correlation alone not fully identified.13 targeted tests passed after extension; предыдущая191 fullfast зелёная. Это существенная synergy learning mechanisms, но не satisfactory all-needs result.

Next: Новая motivation-only safety-weight-pilot-01 при этом зафиксированном model-persistent learner: safety1/1.5/2, остальное1,1M. Все3 seed уже питаются и регулируют температуру с запасом, теперь проверить устранение proximity excess без их потери. Preregister candidate1.5 preferred else2 only if all6 criteria pass on all3. Затем fresh independent full confirmation; не ослаблять пороги.

## 2026-10-26T16:23:45.0654291+00:00 — safety-weight-pilot-01

9/9 completed1M. Safetyw1 fails3/3 proximity, w1.5 andw2 pass all6 criteria on3/3 training seeds. Selectw1.5 exactly by preregistered preference, not metric ranking. w1.5 means+/-SD: hunger.19247+/-.03953, MAE.44149+/-.05814, outside.12154, above.00213, proximity.10652+/-.00074, contact.00455, food69.4. Individual evaluation worlds14/15 joint pass; one prox failure(seed42/world92003), all food criteria pass. Identityw1 reproduces prior hashes on3/3. Pilot is not independent confirmation; no final success yet. Full control/candidate to use entirely fresh training/evaluation seeds and unchanged physics/profile/horizon.

Next: Register balanced-confirmation-01 full: safety1 vs selected1.5, learner fixed predictive+persistent,1M final, train1009/2027/3037/4051/5059, eval131101/132103/133111/134109/135121. Success requires all5 candidate training averages pass all6,>=20/25 joint evaluation worlds pass and all25 food criteria pass, improved hunger/thermal vs frozen on5/5. Keep every seed/checkpoint, no tuning after held-out results. If passing deploy first prespecified training seed1009 final policy; if failing new hypothesis/new seed pool.

## 2026-10-26T16:26:36.2988261+00:00 — balanced-confirmation-01

Пользователь во время независимого подтверждения спросил об устройстве и риске заточки под3 критерия. Дано явное ограничение: модульный мир и мотивация расширяемы, но текущий RelationalSarsa явно читает hunger/body/safety и специальный thermal predictor; новые appearance объекты автоматически получают kernels, новая потребность без расширения представления не становится полноценным фактором. Успех наecology-v2 не докажет универсальность. Это существенный научный приоритет, не косметическая доводка.

Next: Не прерывать frozen independent confirmation. После неё приоритетно исследовать generic encoding всех наблюдаемых внутренних каналов и автоматически создаваемых предсказателей/взаимодействий вместо имён3 механик; затем отдельная validated новая потребность/изменённые условия. Не менять физику, не выдавать specialized proof-of-concept за обобщённый мозг.

## 2026-10-26T16:37:08.1993751+00:00 — balanced-confirmation-01

Full completed10/10 наfresh train1009/2027/3037/4051/5059 иfresh eval131101/132103/133111/134109/135121. Candidatew1.5 passed4/5 training averages;4051 fails proximity.1204667>.12. Joint per-world pass18/25 (<20 gate), один мир не проходит food criterion, поэтому независимое подтверждение ОТРИЦАТЕЛЬНОЕ по3 условиям, не только rounding edge. Candidate mean+/-SD: hunger.20587+/-.02964, MAE.50900+/-.04260, outside.14916, above.01053, proximity.11281+/-.00525, contact.00499,food67.48. Controlw1 passed0/5,2/25 worlds, hunger.29170, MAE.33466, proximity.12993. Не заменять seed4051 или плохой evaluation world, не ослаблять gate, не использовать best checkpoint. Raw/compact full results сохранены research/campaigns/local/results/balanced-confirmation-01-summary.json. Пользовательский вопрос обобщения остаётся приоритетом: specialized controller не объявлять универсальным. Goal service returned usageLimited (198434 tokens observed), хотя пользователь снял wall-clock cap; новых вычислений после этого не запускать, не обходить ограничение автоматизацией/новой целью.

Next: После разрешённого возобновления: новая independent full confirmation для ранее prespecified sensitivity safetyw2, прошедшейpilot3/3, при неизменном model-persistent learner1M. Новые train6007/7001/8009/9001/10007, eval231101/232103/233111/234109/235121; controlw1.5, candidatew2. Прежняя full серия остаётся failed и теперь development evidence; не pooling с новой. Сохранить тот же полный gate5/5 averages,>=20/25 joint worlds,all25 food,vs frozen improvements. Затем generic observable-channel representation вместо hard-coded hunger/temperature/safety и отдельная проверка добавления механики; это ещё не реализовано. Registered next study пока НЕТ. Read current CONTEXT/FINDINGS, actual suite and source status before resuming.

## 2026-10-26T16:47:38.4838148+00:00 — interim-report

Оформлен единый промежуточный отчёт docs/research-local-interim-report.md: 11 завершённых studies, 82 trials, 51.34M обучающих переходов; статусы suite и все result.json сверены. Независимое подтверждение остаётся отрицательным: 4/5 training averages,18/25 joint worlds, один food failure; без объявления надёжного или универсального решения. Добавлена CSV-таблица всех50 финальных миров контроля и кандидата; исходные регистрации и JSON не изменены. Перед публикацией Release build без ошибок/предупреждений и193/193 fast tests прошли. Пользовательский Godot lockfile сохранён и исключён из коммита. Публикация не является новым научным циклом.

Next: Продолжение по прежнему решению: новая регистрация независимой проверки safety2 против1.5 на новых seed при том же полном gate; прежний full сохраняется failed. Затем generic encoding внутренних наблюдений и автоматические predictors с отдельной validated новой механикой. Новый full пока не зарегистрирован и не запущен; цель не достигнута. Для восстановления читать сводный отчёт, CONTEXT/FINDINGS и реальные suite/result, соблюдать доступный бюджет.
