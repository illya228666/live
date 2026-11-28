export const POLL_MS = 250;

const marker = Object.freeze({
  key: "", visible: true, layer: 0, scale: 1, rotation: 0, alpha: 1,
  color: "#9db5ab", primitive: "disc", label: "Другие объекты",
});

export function visibleAppearances(entity) {
  const styles = entity.appearance.length ? entity.appearance : [marker];
  return styles.filter(style => style.visible && style.scale > 0 && style.alpha > 0);
}

export function legendItems(entities) {
  const items = new Map();
  for (const entity of entities) {
    for (const style of visibleAppearances(entity)) {
      if (style.label && !items.has(style.label)) items.set(style.label, style.color);
    }
  }
  return [...items];
}

export function compatible(previous, next) {
  return previous && previous.sessionId === next.sessionId && previous.epoch === next.epoch
    && previous.revision === next.revision && next.sequence > previous.sequence
    && next.age >= previous.age && previous.width === next.width && previous.height === next.height;
}

export function position(entity, before, progress, limit) {
  if (!before || Math.hypot(entity.x - before.x, entity.y - before.y) > limit) return entity;
  return { ...entity, x: before.x + (entity.x - before.x) * progress,
    y: before.y + (entity.y - before.y) * progress };
}

export function rotation(from, to, progress) {
  if (from === undefined) return to;
  const angle = to - from;
  return from + Math.atan2(Math.sin(angle), Math.cos(angle)) * progress;
}
