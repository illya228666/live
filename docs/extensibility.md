# Extending the experiment

The existing temperature module demonstrates four narrow Core contracts:

| Contract | Purpose |
| --- | --- |
| `IWorldSystem` | Initialize/update module-owned physical state |
| `IObservationProvider` | Emit normalized sensory tokens |
| `IActionProvider` | Generate executable candidates and perform its action types |
| `IDriveProvider` | Report a scaled internal need |

A module need not implement all four. Movement is a Core action provider; temperature has no special action type.

## Hypothetical Fruit + Hunger module — not implemented

A future module could add fruit entities to `WorldState.Entities` and a `HungerBody` component attached to `AgentState`. Its world system would increase hunger over time and update its fruit state.

Fruit entities could carry neutral `VisualAppearance` components, using the existing Vision provider without exposing fruit semantics or world coordinates. A hunger observation could use another reserved slot. Each token still fits the existing feature width. More visible objects increase token count, not network dimensions.

Its drive provider could report hunger, its target, a domain-appropriate normalization scale and weight. Core would combine it with the temperature drive. The module would not reward eating directly with an unbounded scalar.

If eating is possible, an action provider could register `fruit.eat.v1`. It would produce candidates only for reachable fruit and execute the selected candidate. The generic Q scorer would evaluate those candidates alongside movement. Stable candidate parameters would identify/describe a reachable fruit using a documented normalized encoding; array position must not become a hidden semantic identifier.

The composition root would register the providers and pass their collections into `Simulation`. Fruit-specific presentation DTOs could be added at the Application boundary. Godot and future frontends would render them independently.

This requires new module/state/provider code and a new learning experiment. It does not require rewriting the Deep Sets encoder or adding Q output neurons. Type slots and feature width are finite: exhausting either requires a deliberate new architecture/checkpoint version.

## Registry and checkpoint discipline

Logical keys are versioned, registered in a deterministic order and stored in checkpoints. Reordering the existing mappings silently would reinterpret trained embeddings, so loading rejects that mismatch. The current loader also rejects added registry entries; a future fine-tuning/migration workflow must explicitly preserve old slots before accepting schema extensions.

The capacity to accept new token/action types is tested by registering new slots on an existing brain and running the same scorer. It does not mean unused embeddings encode useful behavior before training.

Runtime plugin loading, hot-loading new action types and continual learning remain future milestones. Keep the next experiment statically composed until its behavior is measurable.
