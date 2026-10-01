# Neural architecture

## Data contracts

The brain references Core and has no knowledge of temperature, fire, food or positions. It receives arrays of `ObservationToken(Type, Features)` and `ActionCandidate(Type, Parameters)`.

Logical strings such as `temperature.body.v1` are mapped to numeric slots by separate observation and action registries. The slots address trainable embedding tables, each preallocated to 256 × 8. A registry preserves insertion order, returns the same slot for repeated keys and fails clearly when capacity is exhausted.

Each token/candidate has at most four numeric values, zero-padded to width four. Values must be finite and within [-1, 1]. Type identity and numerical features carry different information: identity tells the shared network which kind of measurement it is processing; features contain the measured values.

## Deep Sets encoder

For a batch of B states with at most T tokens:

```text
type IDs [B,T]        -> embedding [B,T,8]
features [B,T,4]      -> concatenate [B,T,12]
shared token MLP      -> Linear(12,32), Tanh, Linear(32,32), Tanh
encoded tokens       -> [B,T,32]
masked mean over T   -> fixed state [B,32]
```

Padding is excluded from both the sum and the divisor. Permuting the tokens changes only floating-point summation order. Increasing token count does not change any parameter shapes. The encoder is small enough for this world, though mean pooling loses token cardinality and cannot capture every possible relational structure.

## Candidate Q scorer

For A candidate actions:

```text
state [B,32]             -> repeat [B,A,32]
action embedding [B,A,8]
action parameters [B,A,4]
concatenation [B,A,44]
shared MLP               -> Linear(44,64), ReLU, Linear(64,64), ReLU, Linear(64,1)
Q values                 -> [B,A]
```

There is one output value per invocation of the shared scorer, not one learned output neuron per game mechanic. Batch padding is masked with a large negative value. Max/argmax therefore sees only legal candidates. The default network has 12,673 trainable parameters, including unused type slots.

## Double DQN

Replay entries are ordinary managed observations, selected actions, rewards, next observations, next legal candidates and a terminal flag. Arrays are copied when inserted to keep historical transitions immutable to callers. No replay entry owns a native Tensor.

Every fourth simulation step after 1,000 warm-up entries, 64 transitions are sampled uniformly with replacement. The online network predicts Q for the chosen actions. The online network also chooses the best next action, while the target network evaluates that choice:

```text
y = reward + gamma × (1 - terminal) × Q_target(nextState, argmax Q_online(nextState, candidates))
```

This split reduces the overestimation of ordinary DQN. The target is computed under `no_grad`. Adam minimizes SmoothL1/Huber loss. Gradient norm is clipped to five. Target weights are synchronized every 500 optimizer updates, not every 500 world steps.

Defaults: learning rate 0.001, gamma 0.97, replay capacity 50,000, epsilon linearly declining from 1 to 0.05 over 50,000 steps. Evaluation always uses epsilon zero. The typed options and JSON expose these choices.

## Native resource lifetime

TorchSharp wraps native LibTorch tensors. Each inference, encoding or optimizer update runs within a `NewDisposeScope`, which releases temporary aliases, including intermediate tensors in chained operations. Outputs cross the public boundary only as copied managed float arrays. Replay remains managed data.

Network modules and Adam remain alive for the brain's lifetime. The optimizer is disposed before the target and online modules. Target synchronization uses a short-lived scope. A failed visualization load also disposes the allocated brain. This avoids relying on the .NET GC to estimate native temporary memory pressure.

The current process-global Torch seed/thread settings imply a single training session per process. Multi-agent parallel training needs explicit RNG/thread management in a later iteration.
