# Extending the experiment

The existing modules use four narrow Core contracts:

| Contract | Purpose |
| --- | --- |
| `IWorldSystem` | Initialize/update module-owned physical state |
| `IObservationProvider` | Emit normalized sensory tokens |
| `IActionProvider` | Generate executable candidates and perform its action types |
| `IDriveProvider` | Report a scaled internal need |

A module need not implement all four. Movement is a Core action provider; temperature and hunger have no special action types.

## Hunger and apples — implemented

`Modules.Hunger` attaches `HungerState` to the agent, grows its level with simulation time, emits a normalized token and reports a target-zero drive. The existing reward aggregator combines hunger and temperature without module-specific reward logic.

The independent FoodSpawner world system creates apple entities during world life with independent `VisualAppearance(Diamond)` and `Nutrition(0.20)` components. Fire uses `Disc`. Vision observes both through the same backend and relative contract; appearance contains no apple/food semantics or absolute coordinates.

Hunger consumes any entity with nutrition inside the eating radius and removes it from the world. Another food entity needs only a nutrition component; Hunger does not know about apples, their positions or their visual types.

Eating is automatic at contact, subtracting percentage points rather than multiplying hunger. No eating action or separate food bonus is registered. Food spawns on a global interval with a population cap, seeded random positions and minimum agent distance. Reset starts an empty food population and a fresh schedule.

The composition root passes Temperature, Hunger and FoodSpawner as world systems, Temperature and Hunger as drives, and Temperature, Vision and Hunger as observation providers. All observations still fit the existing feature width. Food disappearing changes the token count, not the network dimensions.

The Deep Sets encoder and Q scorer are unchanged. Type slots and feature width remain finite: exhausting either requires a deliberate new architecture/checkpoint version. The next learning experiment can measure balancing the two needs; the existing regression and live smoke verify that training still runs.

## Registry and checkpoint discipline

Logical keys are versioned, registered in a deterministic order and stored in checkpoints. Reordering the existing mappings silently would reinterpret trained embeddings, so loading rejects that mismatch. The current loader also rejects added registry entries; a future fine-tuning/migration workflow must explicitly preserve old slots before accepting schema extensions.

The capacity to accept new token/action types is tested by registering new slots on an existing brain and running the same scorer. It does not mean unused embeddings encode useful behavior before training.

Runtime plugin loading, hot-loading new action types and continual learning remain future milestones. Keep the next experiment statically composed until its behavior is measurable.
