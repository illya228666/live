# Эксперименты Motivation × Learning

Основной путь — CLI и `RuntimeSession`; Godot для запусков не нужен.
`configs/experiments-pilot.json` содержит пример 2 × 2 с тремя обучающими seed,
двумя фазами и отдельными оценочными мирами.

## Запустить и сравнить

Из корня репозитория:

```powershell
./tools/build.ps1 -Headless
. ./tools/common.ps1
Initialize-Dotnet
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/experiments-pilot.json --output artifacts/experiments/pilot-a
# Один вариант, все его seed:
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/experiments-pilot.json --output artifacts/experiments/one --variant homeostasis-level--dqn-uniform
# Независимое повторение всей серии:
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/experiments-pilot.json --output artifacts/experiments/pilot-b
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- compare --inputs 'artifacts/experiments/pilot-a,artifacts/experiments/pilot-b' --output artifacts/experiments/comparison
```

Пути `packageRoot` в конфигурации эксперимента разрешаются относительно JSON-файла.
Пути CLI разрешаются относительно текущего каталога. Идентификаторы вариантов и фаз
содержат латинские буквы, цифры, `_` и одиночные `-`; имя комбинации —
`MOTIVATION--LEARNER`. Runner перебирает все комбинации и пары обучающего seed / seed
мира. `worldSeeds`, если указан, имеет ту же длину, что `trainingSeeds`.
Без него seed мира равен обучающему seed. Оценочные seed должны быть независимыми;
для эпизода `i` используется `seed + i * 10000`.

Каждый trial работает в отдельном процессе последовательно, с собственными RNG.
Бюджет определяется `phases[].steps`, интервал оценки — `evaluateEvery`.
`learning.trainingSteps` не меняет бюджет серии.
Повторный запуск в тот же каталог пропускает завершённые trials с совпадающими
конфигурацией и сборкой; незавершённые начинает заново. Иные настройки или исходники
требуют нового каталога. Ошибки сохраняются, остальные trials продолжаются;
неполная серия возвращает ненулевой код.

## Добавить мотивацию

1. Реализовать существующий `IMotivationPolicy.Evaluate(before, after)` в пакете
   `IRuntimeModule`; в `Register` присвоить `builder.Motivation = this`.
2. Читать параметры через `builder.Settings`. Drives и именованные сигналы уже
   доступны через `MotivationInput`; физические механики изменять не требуется.
3. Опубликовать пакет с точной версией по [правилам runtime](runtime-modules.md).
4. Добавить запись в `motivations`: `id`, `package: {id, version}`, `settings`.

Runner заменяет пакет `motivationSlot` (по умолчанию `homeostasis`) выбранным пакетом.
Должна регистрироваться ровно одна мотивация. Настройки мотивации независимы от
`learners`. Разные реализации можно публиковать под одним id с разными версиями.
Горячая замена использует прежний `Activate`; активные пакеты не удаляются.
Для смены установленного пакета нужны новая версия и настройки её манифеста,
а не изменение опубликованной версии на месте.

## Добавить алгоритм обучения

1. Реализовать существующий `IAgentController`: выбор допустимых действий,
   обработку переходов, уведомления о vocabulary/условиях, экспорт/импорт политики.
2. Реализовать `IAgentControllerFactory`: `Algorithm`, `Architecture`, раскрытие и
   проверку параметров в `ResolveParameters`, создание с seed, проверку типа
   контроллера в `Supports`.
3. Зарегистрировать фабрику через `ControllerRegistry.Register` в composition root.
   Регистрация должна выполняться и в дочернем CLI-процессе до обработки команд;
   простой вариант — запись в реестре рядом с DQN.
4. Добавить `learners[].controller: {algorithm, parameters}` в JSON.

Фабрика раскрывает все defaults, проверяет параметры и использует переданный seed.
DQN принимает `parameters.network` и `parameters.learning` — существующие
`NetworkOptions`/`LearningOptions`; неизвестные поля отклоняются.
В явной конфигурации контроллера прежние поля `RuntimeOptions.Network/Learning`
не дополняют его настройки. `ControllerSeed` задаёт seed алгоритма; `WorldSeed` —
мира. Старые runtime-конфигурации без `controller` используют прежние
`Network/Learning` и `Learning.Seed`.

`CreateInferenceController` возвращает независимые временную память и RNG.
Параметры можно разделять для чтения: обучение при оценке приостановлено.
Оценка не вызывает `Receive`. Новый алгоритм не обязан иметь replay или DQN;
неиспользуемые поля `ControllerStatus` могут оставаться нулевыми.
`Steps` отражает число полученных обучающих переходов.

Checkpoint v3 хранит алгоритм, архитектуру, эффективные параметры и inference state;
v1/v2 DQN по-прежнему читаются. Политики открываются в Godot.
Экспорт не сохраняет optimizer, replay или текущее состояние мира.

## Оценка, фазы и результаты

Каждая фаза создаёт именованный оценочный сценарий. По умолчанию это её реальные
условия; `evaluationRuntime` позволяет задать отдельную полную runtime-конфигурацию.
Перед фазой применяется `activate`, обучение продолжается в той же жизни.
В каждой контрольной точке политика оценивается во всех уже введённых сценариях,
включая прежние условия. Номера логических наблюдений/действий сохраняются.
Внешний activation inbox не читается.

Для нового сценария фиксируются random, исходная необученная политика и политика
до дальнейшего обучения (`initial` для первой фазы, `frozen` для последующих).
Каждый оценочный эпизод начинает новый мир. Warm-up оценки не отбрасывается.

`IBehaviorEvaluator` получает только неизменяемый `RuntimeFrame` и
`HomeostasisReport`, без reward. Он возвращает именованные `BehaviorMetric` с
единицей, направлением улучшения, числом наблюдений и признаком proxy.
Новый оценщик регистрируется через `BehaviorEvaluators.Register` и выбирается в
`behavior.evaluators` по id с отдельными параметрами. Меняйте ключ/версию протокола,
если изменяется смысл метрики.

Встроенные `drives` дают статистику по произвольным ключам потребностей;
`diagnostic.balance` остаётся диагностикой. `physical` измеряет температуру, голод
и близость кабана. Defaults профиля: `temperatureTarget=36.6`,
`temperatureTolerance=1`, `hungerThreshold=0.8`, `boarRadius=10`.
Пороги независимы от мотивации. Расстояние до кабана и drive safety — **прокси
близости**, не измерение ущерба или выживания. Отсутствующая механика даёт
`Value: null, Samples: 0`; отсутствующие динамические drive-ключи также считаются N/A.

Успех задаётся явными критериями по финальной оценке. Например:

```json
"criteria": [
  {"scenario": "with-hunger", "metric": "hunger.mean", "maximum": 0.4},
  {"scenario": "thermal-wildlife", "metric": "temperature.mae", "maximum": 0.3}
]
```

Без критериев статус поведения — `not-assessed`. Reward не превращает его в
`passed`. Технический `completed` означает выполнение протокола.
Короткий пилот намеренно не задаёт порогов научного успеха.

Серия сохраняет исходный JSON, эффективные runtime-параметры в `job.json`, архив
пакетов, Git revision/diff и новые исходные файлы, версии среды и хеши сборок,
включая native-библиотеки. Каждый trial содержит `events.jsonl`, атомарный
`result.json`, `worker.log` и политики на входе/выходе каждой фазы.
Точные версии зависимостей закреплены lock-файлами проекта.

`compare` пишет `comparison.json`, `comparison.csv` и `comparison.md`:

- `quality`: финальное поведение в каждом сценарии;
- `curve@STEP`, `learning-auc`: кривая и трапецеидальная площадь, делённая на длину фазы;
- `adaptation-change`: результат конца фазы минус frozen в новых условиях;
- `retention-change`: финальный результат минус конец обучения прежнему сценарию;
- `Pairs`: парные различия вариантов на одинаковых обучающих seed/seed мира;
- `Trials`: статус критериев, счётчики, отдельные времена обучения/оценки и reward.

Для lower-is-better метрик отрицательная adaptation-change означает улучшение,
положительная retention-change — ухудшение. Среднее и выборочное SD считаются по
обучающим seed после усреднения оценочных эпизодов. При одном seed SD не определено.
Недоступные значения не заменяются нулями. Для proxy и диагностик учитывайте их смысл.

Разные физические условия, расписания, seed или профили оценки создают отдельные
protocol-группы. Мотивация и параметры обучения не входят в идентичность протокола.
Повторное использование имени варианта с другими параметрами/сборкой получает
отдельный `@`-суффикс. Общего ранжирования несовместимых протоколов нет.

Для повторений одного trial сравниваются SHA256 последовательностей действий,
счётчики и числовые метрики с абсолютным допуском `1e-9`. Время, пути и случайные
имена поколений checkpoint исключаются. Повторы не увеличивают N в статистике.
Гарантия относится к зафиксированной среде; перенос между платформами проверяется
отдельно. Полные тесты: `./tools/test.ps1`; скрипт использует встроенный xUnit runner.
