# Live: протокол Drive Motivation v1 — 24 октября 2026

Зафиксирован до pilot и до основной оценки. Исполнение через существующие CLI
`experiment` и `compare`; новые физические механики и алгоритмы не вводятся.

## Мотивации

Общее расстояние новых вариантов: `D = sqrt(sum(((Current-Target)/Scale)^2))`
по всем активным drives. Без Weight, clipping и деления на число drives.
Drive Reduction: `D(before)-D(after)`; Drive Level: `-(1-0.97)*D(after)`.
Параметр `discountFactor=0.97` согласован с gamma общего DQN и не подбирается.
Weighted Homeostasis — существующий пакет с improvementWeight 0.2 и
maximumDriveWeight 10. Его формула и веса остаются прежними.

Для ограниченного D и непрерывного обучения с gamma < 1:
`G_reduction = D(initial) + G_level`. На конечном отрезке длины T остаётся
граничный член `-gamma^T * D(final)`. Оценочные эпизоды ограничены по времени,
но обучение не получает terminal на границах фаз/эпизодов. Это сравнение
сигналов обучения при аппроксимации и конечном бюджете, не доказательство
разных оптимальных целей. Основание: [Keramati & Gutkin, 2014,
уравнения 1–5](https://elifesciences.org/articles/04811).

## Запуски

- Общая физика и две фазы взяты из `configs/experiments-pilot.json` без изменений:
  Temperature + Wildlife, затем реальная активация Hunger/Food в той же жизни.
- Один learner для всех мотиваций: настройки `configs/continual.json` с текущими
  defaults, неизменная сеть; все параметры явно записаны в новых JSON.
- Pilot: `configs/drive-motivation-pilot.json`, seed 17, оценка 10017/20017,
  2000+4000 шагов, оценка каждые 1000 шагов по 1000 шагов. Две полные серии,
  6 trials; повтор сравнивается с допуском 1e-9 штатным compare.
- Основная серия: `configs/drive-motivation.json`, seed 42/73/101/211/509,
  оценка 61001/62003/63013/64019/65029, 100000+200000 шагов,
  оценка каждые 25000 шагов по 4000 шагов. 15 trials, 4.5 млн переходов.
- В обоих случаях один эпизод на оценочный seed. Старые сценарии продолжают
  оцениваться после активации. Random/untrained/initial/frozen и политики
  сохраняются штатным runner. Основные оценочные seed не используются в pilot.

## Метрики и анализ

`physical`: temperature.mae, temperature.outside (target 36.6, tolerance 1),
hunger.mean, hunger.above (>0.8), wildlife.distance и wildlife.proximity (<10).
Близость кабана — прокси; ущерба и смерти в данной механике нет.

`stability-v1` получает только RuntimeFrame/HomeostasisReport:

- temperature.temporal-sd и hunger.temporal-sd: population SD внутри эпизода;
- temperature.outside-longest, hunger.above-longest,
  wildlife.proximity-longest: максимальная непрерывная серия нарушений, в шагах;
- movement.reversal: доля углов >90° среди пар соседних ненулевых наблюдаемых
  перемещений (dot product <0). Стационарный шаг разрывает соседство; первое
  перемещение до первого наблюдения недоступно. Это описательный прокси,
  а не самостоятельный критерий качества.

Отсутствующая механика даёт null/Samples=0. SD и длины серий интерпретируются
вместе со средними: постоянно высокий голод может иметь малый SD.
Длины серий ограничены горизонтом оценки, без отбрасывания warm-up.

Сначала усреднить оценочные эпизоды внутри обучающего seed, затем mean и
выборочное SD по пяти обучающим seed. Показать отдельные seed, парные разности,
baseline, learning curves/AUC, adaptation-change и retention-change. Последнее —
конечное поведение в старом сценарии минус поведение на конце его первой фазы.
Дополнительно проверить старые навыки в новом мире с голодом.

Reward — только диагностика. Criteria пусты; BehaviorOutcome остаётся not-assessed.
Техническое completed не означает освоение навыков. Не вводить общий рейтинг,
не объявлять победителя, не добавлять старые результаты к новой выборке.

## Контроль исполнения

Полный набор тестов, аналитические награды, нормализация и независимость от весов,
одинаковая физика/метрики при одинаковых действиях, отсутствие reward за саму
активацию, проверка временных метрик. Pilot должен воспроизводить действия,
счётчики и метрики. В основной серии проверить 300000 шагов/возраст/полученные
переходы, одинаковые update counts, активации и загрузку каждой экспортированной
политики. Низкое качество поведения не является техническим сбоем.

Обе конфигурации и этот протокол сохраняются до pilot. После технического
исправления pilot повторяется. После просмотра основной оценки настройки,
список seed и правило выбора финального checkpoint не меняются.

## Воспроизведение

```powershell
./tools/build.ps1 -Headless
./tools/test.ps1
. ./tools/common.ps1
Initialize-Dotnet
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/drive-motivation-pilot.json --output artifacts/experiments/drive-pilot-a
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/drive-motivation-pilot.json --output artifacts/experiments/drive-pilot-b
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- compare --inputs 'artifacts/experiments/drive-pilot-a,artifacts/experiments/drive-pilot-b' --output artifacts/experiments/drive-pilot-comparison
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/drive-motivation.json --output artifacts/experiments/drive-main
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- compare --inputs artifacts/experiments/drive-main --output artifacts/experiments/drive-comparison
```

Основная выполненная серия использовала штатную фильтрацию по варианту,
с отдельными каталогами (три команды experiment исполнялись параллельно):

```powershell
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/drive-motivation.json --output artifacts/experiments/drive-main/weighted-homeostasis --variant weighted-homeostasis--dqn-continual
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/drive-motivation.json --output artifacts/experiments/drive-main/drive-reduction --variant drive-reduction--dqn-continual
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- experiment --config configs/drive-motivation.json --output artifacts/experiments/drive-main/drive-level --variant drive-level--dqn-continual
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- compare --inputs 'artifacts/experiments/drive-main/weighted-homeostasis,artifacts/experiments/drive-main/drive-reduction,artifacts/experiments/drive-main/drive-level' --output artifacts/experiments/drive-comparison
```

Этот раздел фиксирует фактические команды исполнения после серии;
определения метрик, конфигурации и правила анализа выше не менялись.
Результаты: [отчёт](drive-motivation-report.md) и
[сводный JSON](drive-motivation-results.json).
