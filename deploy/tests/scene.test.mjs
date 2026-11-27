import assert from "node:assert/strict";
import test from "node:test";
import { compatible, position, rotation } from "../web/scene.mjs";

const frame = { sessionId: "world", epoch: "worker-a", revision: 1, sequence: 5, age: 15, width: 100, height: 100 };

test("only frames in the same active generation can be interpolated", () => {
  const next = { ...frame, sequence: 6, age: 18 };
  assert.equal(compatible(frame, next), true);
  for (const changed of [{ epoch: "worker-b" }, { age: 10 }, { revision: 2 }, { sequence: 5 },
    { width: 96 }, { sessionId: "other" }]) {
    assert.equal(compatible(frame, { ...next, ...changed }), false);
  }
});

test("interpolate movement and wrap angles without drawing a teleport trail", () => {
  const before = { id: "1", x: 4, y: 12 };
  const entity = { id: "1", x: 20, y: 20 };
  assert.deepEqual(position(entity, before, .5, 25), { id: "1", x: 12, y: 16 });
  assert.equal(position(entity, before, .5, 10), entity);
  assert.ok(Math.abs(rotation(3.1, -3.1, .5) - Math.PI) < 1e-6);
});
