# Project instructions

Разрешено задавать пользователю уточняющие вопросы во время внепланового обсуждения,
если это поможет понять цель, ограничения или предпочтения. Если ответ не нужен
для продолжения, продолжай полезную работу параллельно с ожиданием ответа.

По умолчанию работать в главной ветке `master`, включая исследования и обычные
исправления. Отдельную ветку создавать только при конкретной необходимости
изоляции работы; новая задача или эксперимент сами по себе не являются причиной.

For autonomous research campaigns, first read `research/RESEARCHER.md` and
`research/BASELINE.md`, then recover the named campaign with
`./tools/research.ps1 -Action context -Campaign <id>`. The default campaign is `local`.
Research work belongs on the campaign's recorded Git branch. Use the existing
experiment runner, controller/motivation contracts and evaluation infrastructure.
The research procedure is a resumable working method, not a restriction to any
algorithm, architecture, motivation theory or predefined hypothesis list.

The current direction is `docs/research-direction.md`: study a continuously learning
inhabitant of an expanding world. Ecology-v2 is a preserved reference, not the goal.
Do not resume the superseded safety-weight sweep. Study autonomous exploration of
new interactions independently of homeostatic needs. Freeze controller/learning
code before authoring new generalization challenges; never specialize it to those
challenges after seeing results. Start with a minimal end-to-end experiment; full
life persistence, complex memory and any particular algorithm are not prerequisites.

Before scientific motivation/learning comparisons, use the registered baseline in
`research/baseline.json` or provide a passing scenario suitability review for every
active factor. If passivity already solves a factor (or adaptive control cannot
solve it), repair the common scenario parameters and rerun controls before the
comparison. Merely documenting the defect is insufficient. Diagnostic runs may
investigate broken scenarios; they are not scientific evidence for approach rankings.

Runtime architecture: all production simulation execution belongs to
RuntimeSession.Step. Godot is an independent WebSocket viewer and must not
reference Application or Brain. Observation contracts are shared via
Observation.Contracts; package IO belongs to Packages, process/network hosting
to Hosting. LegacyReference under tests is numerical/checkpoint reference code,
not a production entrypoint. Read docs/observation-runtime.md before changing
execution, observation, viewer or launch behavior.

## World Evolution Workflow

- Discover with `./tools/world.ps1 list`. Restore a stopped World from its recorded
  directory; use `host.ps1 -Action start -Restore CHECKPOINT -Directory NEW_DIR` for
  an isolated copy. Never infer that a new directory is the continuing World.
- New mechanics: implement the existing `IRuntimeModule` contract and add a local
  `package.json`; no central packager edit. Publish via `package-module.ps1 -Description ...`.
- `evolve.ps1 -Action check -Description ... -Version NEW_VERSION -Checkpoint ... -Output ...`
  builds/packages/tests only the candidate. Reuse `-Sources`/`-NoBuild` when appropriate.
- Deploy with `evolve.ps1 -Action deploy -Directory WORLD -Session ID -Sources PACKAGE -Output ...`;
  success means validation + probation + active version + observation/save, not command admission.
- `evolve.ps1 -Action observe -Directory WORLD [-Capture] [-Save] -Output ...` returns
  compact JSON and artifact paths. Confirm increasing age, expected revision and no new event faults.
- Publish Host with `host.ps1 -Action publish`; start from pinned releases, not dev `bin`.
  Use `host.ps1 -Action switch` only for World Only; never restart Learning as exact continuation.
- Commands, retry rules, package examples and E2E: `docs/world-evolution-workflow.md`.
