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
