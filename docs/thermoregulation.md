# Thermoregulation

## Environment

The composition root creates a central fire entity with `HeatEmitter` and neutral `VisualAppearance(Disc)` components. Temperature uses the emitter's entity position; vision uses the independent appearance. At distance r and simulation time t, `configs/default.json` specifies:

```text
strength(t) = 40 × (1 + 0.3 × sin(2πt / 180))
environment(r,t) = 18 + strength(t) × exp(-r² / (2 × 24²))
```

Temperature falls smoothly toward the 18 °C ambient baseline. Strength oscillates from 28 to 52 °C above ambient over 180 simulated seconds; the source never teleports. The equilibrium comfort ring moves continuously inward and outward.

## Body

Body temperature B obeys a simple first-order model:

```text
dB/dt = production + transfer × (environment - B)
production = 0.3 °C/s
transfer = 0.12 /s
```

The implementation integrates this equation analytically for the local environmental value sampled at the end of each step:

```text
equilibrium = environment + production / transfer
B_next = equilibrium + (B - equilibrium) × exp(-transfer × dt)
```

This remains stable for larger timesteps and retains inertia. The thermal time constant is about 8.3 simulated seconds. To maintain a 36.6 °C body, the creature must typically occupy an environment near 34.1 °C. Close to the source it overheats; at the edges it cools. The neural policy is never given the required ring or movement direction.

The model approximates heat exchange, not human physiology. There is no death threshold. Initial body temperature is uniform within target ±2 °C, with positions uniform across the world and an initial field phase drawn from the seeded clock.

## Senses

| Token | Numeric features |
| --- | --- |
| Body | `(body - target) / 20` |
| Local thermal sensor | `(local environment - 30) / 40`, 0, 0 |
| North / east / south / west thermal samples | `(sample - 30) / 40`, offset X, offset Y |
| Visual disc | `cos(relative bearing)`, `sin(relative bearing)`, `distance / (1 + distance)`, `angular diameter / pi` |

Thermal values are clamped to [-1, 1]; vision features are normalized to the same range. Cardinal thermal sensors sample three simulation units away and clamp their sampling position at world edges. Sensor offsets distinguish direction while all five samples share one type slot. The initial complete default state has eight tokens: six thermal, one visual (fire) and one hunger; each spawned apple adds a visual token. Absolute agent/object coordinates are never observation features. Visual bearing uses the observer's orientation, currently fixed to zero; it has no north/east contract. Appearance carries no fire/food label.

These are measurements; no ideal-position distance, best movement or comfortable-zone vector is exposed.

## Drive and reward

Temperature reports `Current = body`, `Target = 36.6`, `Scale = 6`, `Weight = 1`. Core owns aggregation:

```text
normalizedError_i = min(abs(current_i - target_i) / scale_i, 4)
error = weighted mean of normalized errors
reward = clamp(1 - 2 × tanh(error) + 0.2 × (previousError - error), -1, 1)
```

Weights are finite, non-negative and capped at ten. Scales must be positive. This prevents an arbitrary module from returning unlimited reward and makes drives with different units comparable.

At perfect comfort the reward remains +1 each step, so maintaining a successful state is valuable. Large error approaches -1. The small improvement term helps recovery but does not dominate sustained comfort. There is no reward for source proximity, direction, movement or a particular coordinate.

Comfort metrics count body temperatures within **36.6 ±1 °C**, independently of the reward's continuous scale.
