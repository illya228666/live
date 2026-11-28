import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { compatible, legendItems, position, rotation, visibleAppearances } from "../web/scene.mjs";

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

test("an arbitrary module can supply a legend entry and hide its own appearance", () => {
  const style = { label: "Мох", color: "#38a788", visible: true, scale: 1, alpha: 1 };
  assert.deepEqual(legendItems([{ appearance: [style] }, { appearance: [style] }]), [["Мох", "#38a788"]]);
  assert.deepEqual(legendItems([{ appearance: [{ ...style, visible: false }] }]), []);
  assert.equal(visibleAppearances({ appearance: [] })[0].primitive, "disc");
});

test("moth cargo adds a variable glow without replacing the moth or its legend label", () => {
  const path = new URL("../../src/ArtificialLife.Modules.Garden/package.json", import.meta.url);
  const garden = JSON.parse(readFileSync(path, "utf8"));
  const { visuals } = garden.packages.find(item => item.manifest.id === "pollination").manifest;
  const moth = visuals.find(item => item.key === "moth");
  const glow = visuals.find(item => item.key === "moth-cargo");
  const [binding] = glow.bindings;
  assert.equal(glow.component, moth.component);
  assert.equal(binding.component, moth.component);
  assert.equal(binding.field, "cargo");
  assert.equal(binding.property, "alpha");
  assert.equal(glow.browser.primitive, "glow");
  assert.equal(glow.browser.slot, undefined);
  assert.ok(glow.layer < moth.layer);
  const body = { label: moth.browser.label, color: "#" + moth.color, visible: true, scale: 1, alpha: 1 };
  const atCargo = cargo => ({ appearance: [body, { ...glow.browser, visible: true, scale: 1,
    alpha: cargo * binding.scale + (binding.offset ?? 0) }] });
  assert.deepEqual(visibleAppearances(atCargo(0)), [body]);
  assert.equal(visibleAppearances(atCargo(0.02))[1].alpha, 0.5);
  assert.equal(visibleAppearances(atCargo(0.04))[1].alpha, 1);
  assert.deepEqual(legendItems([atCargo(0), atCargo(0.04)]), [["Мотылёк", "#" + moth.color]]);
});
