export const POLL_MS = 250;

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
